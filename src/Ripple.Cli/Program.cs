using System.Diagnostics;
using Ripple.Core;
using Ripple.Core.Extraction;
using Ripple.Core.Graph;

// ripple ingest --repo <path> [--config ripple.eshop.json] [--falkor localhost:6379]
var options = Args.Parse(args);
var command = args.FirstOrDefault() ?? "help";
var falkor = options.GetValueOrDefault("falkor") ?? Environment.GetEnvironmentVariable("FALKORDB") ?? "localhost:6379";
var config = RippleConfig.Load(options.GetValueOrDefault("config") ?? "ripple.eshop.json");

switch (command)
{
    case "ingest":
    {
        var repo = options.GetValueOrDefault("repo") ?? throw new ArgumentException("--repo is required");
        var sw = Stopwatch.StartNew();
        var loaded = await SolutionLoader.LoadAsync(repo, config.Services, Console.Error.WriteLine);
        var batch = await new CodeGraphExtractor(loaded, Console.Error.WriteLine).ExtractAsync();
        Console.Error.WriteLine($"extracted {batch.Nodes.Count} nodes, {batch.Edges.Count} edges in {sw.Elapsed.TotalSeconds:F1}s");

        var store = new GraphStore(falkor, config.Graph);
        store.Drop();
        store.EnsureIndexes();
        store.Write(batch);
        Console.Error.WriteLine($"wrote graph '{config.Graph}' in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var row in store.Query("MATCH (n) RETURN labels(n)[0] AS label, count(n) AS n ORDER BY n DESC"))
            Console.WriteLine($"{row["label"],-14} {row["n"]}");
        foreach (var row in store.Query("MATCH ()-[e]->() RETURN type(e) AS type, count(e) AS n ORDER BY n DESC"))
            Console.WriteLine($"{row["type"],-14} {row["n"]}");
        break;
    }
    default:
        Console.WriteLine("usage: ripple ingest --repo <path> [--config ripple.eshop.json] [--falkor host:port]");
        break;
}

static class Args
{
    public static Dictionary<string, string?> Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
                map[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        return map;
    }
}

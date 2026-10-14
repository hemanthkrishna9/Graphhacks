using Ripple.Core;
using Ripple.Core.Graph;
using Ripple.Core.Review;

// ripple ingest  --repo <path> [--graph name]
// ripple check   [--graph name] [--contract EventName]
// ripple analyze --repo <path> --base <ref> --head <ref> [--reindex]
var (command, options) = Args.Parse(args);
var falkor = options.GetValueOrDefault("falkor") ?? Environment.GetEnvironmentVariable("FALKORDB") ?? "localhost:6379";
void Log(string m) => Console.Error.WriteLine(m);

switch (command)
{
    case "ingest":
    {
        var config = LoadConfig(options);
        var store = new GraphStore(falkor, options.GetValueOrDefault("graph") ?? config.Graph);
        await Indexer.IndexAsync(Require(options, "repo"), config, store, Log);
        foreach (var row in store.Query("MATCH (n) RETURN labels(n)[0] AS label, count(n) AS n ORDER BY n DESC"))
            Console.WriteLine($"{row["label"],-14} {row["n"]}");
        foreach (var row in store.Query("MATCH ()-[e]->() RETURN type(e) AS type, count(e) AS n ORDER BY n DESC"))
            Console.WriteLine($"{row["type"],-14} {row["n"]}");
        return 0;
    }
    case "check":
    {
        var config = LoadConfig(options);
        var store = new GraphStore(falkor, options.GetValueOrDefault("graph") ?? config.Graph);
        var findings = ContractChecker.Run(store, options.GetValueOrDefault("contract"));
        Report.PrintFindings(findings);
        return findings.Count == 0 ? 0 : 1;
    }
    case "analyze":
    {
        var config = LoadConfig(options);
        var analyzer = new PrAnalyzer(config, new GraphStore(falkor, config.Graph), Log);
        var result = await analyzer.AnalyzeAsync(Require(options, "repo"), Require(options, "base"), Require(options, "head"),
            reindex: options.ContainsKey("reindex"));
        Console.WriteLine($"base {result.BaseSha[..10]} ({result.BaselineFindings} pre-existing findings) -> head {result.HeadSha[..10]}");
        Console.WriteLine($"changed files: {string.Join(", ", result.ChangedFiles)}");
        Console.WriteLine($"introduced: {result.Introduced.Count}, resolved: {result.Resolved.Count}");
        Report.PrintFindings(result.Introduced);
        return result.Introduced.Count == 0 ? 0 : 1;
    }
    case "eval":
    {
        var config = LoadConfig(options);
        var loaded = await Ripple.Core.Extraction.SolutionLoader.LoadAsync(Require(options, "repo"), config.Services, Log);
        var store = new GraphStore(falkor, config.Graph + ".eval");
        var limit = options.GetValueOrDefault("limit") is { } l ? int.Parse(l) : (int?)null;
        var report = await new Ripple.Core.Eval.MutationEval(loaded, store, Log).RunAsync(limit);
        var outDir = options.GetValueOrDefault("out") ?? "eval";
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "results.json"), System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var md = EvalMarkdown.Render(report);
        File.WriteAllText(Path.Combine(outDir, "results.md"), md);
        Console.WriteLine(md);
        return 0;
    }
    default:
        Console.WriteLine("""
            usage:
              ripple ingest  --repo <path> [--graph <name>] [--config ripple.eshop.json]
              ripple check   [--graph <name>] [--contract <EventName>]
              ripple analyze --repo <path> --base <ref> --head <ref> [--reindex]
              ripple eval    --repo <path> [--limit N] [--out eval]
            options: --falkor host:port (or FALKORDB env var; default localhost:6379)
            """);
        return command == "help" ? 0 : 2;
}

static RippleConfig LoadConfig(Dictionary<string, string?> options) =>
    RippleConfig.Load(options.GetValueOrDefault("config") ?? Path.Combine(AppContext.BaseDirectory, "ripple.eshop.json"));

static string Require(Dictionary<string, string?> options, string name) =>
    options.GetValueOrDefault(name) ?? throw new ArgumentException($"--{name} is required");

static class Report
{
    public static void PrintFindings(IReadOnlyList<Finding> findings)
    {
        foreach (var group in findings.GroupBy(f => (f.Kind, f.Contract, f.ConsumerService, f.Handler)))
        {
            var f = group.First();
            Console.WriteLine($"\n[{f.Severity}] {f.Kind}: {f.Contract}");
            Console.WriteLine($"  publisher: {f.PublisherService ?? "(none)"}  {f.Publisher}");
            Console.WriteLine($"  consumer:  {f.ConsumerService}  {f.Handler}");
            foreach (var g in group.GroupBy(x => (x.Path, x.Expected, x.Actual)))
                Console.WriteLine($"    path {g.Key.Path ?? "-"}  expected {g.Key.Expected ?? "-"}  actual {g.Key.Actual ?? "missing"}  " +
                                  $"readers: {string.Join(", ", g.Select(x => x.Reader).Where(r => r is not null).Distinct())}");
        }
        Console.WriteLine($"\n{findings.Count} finding rows");
    }
}

static class Args
{
    /// <summary>The first bare word is the command; --name value pairs are options (a flag without value is "true").</summary>
    public static (string Command, Dictionary<string, string?> Options) Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? command = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
                map[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            else command ??= args[i];
        }
        return (command ?? "help", map);
    }
}

static class EvalMarkdown
{
    public static string Render(Ripple.Core.Eval.EvalReport report)
    {
        var sb = new System.Text.StringBuilder();
        var breaks = report.Cases.Count(c => c.Truth.Count > 0);
        var controls = report.Cases.Count(c => c.Kind == "rename-keep");
        sb.AppendLine($"Mutations: {report.Cases.Count} (all compile cleanly). Breaking: {breaks}. Wire-compatible controls: {controls}.");
        sb.AppendLine("Scored per (mutation, consumer service). \"Breaks caught\" = mutations whose broken services were all found and nothing else.");
        sb.AppendLine();
        sb.AppendLine("| Approach | Precision | Recall | TP | FP | FN | Breaks caught exactly | Controls wrongly flagged |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in report.Scores())
        {
            var precision = s.TruePositives + s.FalsePositives == 0 ? "n/a" : s.Precision.ToString("P0");
            sb.AppendLine($"| {s.Approach} | {precision} | {s.Recall:P0} | {s.TruePositives} | {s.FalsePositives} | {s.FalseNegatives} | {s.BreaksCaught}/{s.Breaks} | {s.ControlsFlagged}/{s.Controls} |");
        }
        sb.AppendLine();
        sb.AppendLine("| Mutation | Kind | Truth | Ripple | grep | Call graph |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in report.Cases)
            sb.AppendLine($"| {c.Mutation} | {c.Kind} | {Join(c.Truth)} | {Join(c.Ripple)} | {Join(c.Grep)} | {Join(c.CallGraph)} |");
        return sb.ToString();
    }

    static string Join(IReadOnlyList<string> xs) => xs.Count == 0 ? "-" : string.Join(", ", xs);
}

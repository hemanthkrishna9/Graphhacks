using Ripple.Core;
using Ripple.Core.Graph;
using Ripple.Core.Review;

// ripple ingest  --repo <path> [--graph name]
// ripple check   [--graph name] [--contract EventName]
// ripple analyze --repo <path> --base <ref> --head <ref> [--reindex] [--markdown comment.md] [--repo-url https://github.com/o/r/blob/<sha>]
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
        var repo = Require(options, "repo");
        var analyzer = new PrAnalyzer(config, new GraphStore(falkor, config.Graph), Log);
        var result = await analyzer.AnalyzeAsync(repo, Require(options, "base"), Require(options, "head"),
            reindex: options.ContainsKey("reindex"));
        Console.Error.WriteLine($"base {result.BaseSha[..10]} ({result.BaselineFindings} pre-existing findings) -> head {result.HeadSha[..10]}; " +
                                $"introduced {result.Introduced.Count}, resolved {result.Resolved.Count}");

        // --markdown <file>: the PR comment (graph findings + optional narration). Otherwise print the plain list.
        if (options.GetValueOrDefault("markdown") is { } markdownPath)
        {
            var narration = await Narrator.NarrateAsync(result.Introduced, PrAnalyzer.Diff(repo, result.BaseSha, result.HeadSha), Log);
            var markdown = PrReport.Render(result, narration, options.GetValueOrDefault("repo-url"));
            File.WriteAllText(markdownPath, markdown);
            Console.Error.WriteLine($"wrote {markdownPath}");
        }
        else Report.PrintFindings(result.Introduced);
        return result.Introduced.Count == 0 ? 0 : 1;
    }
    case "queries":
    {
        // Writes every product query, verbatim, into README.md between the QUERIES markers.
        var readme = options.GetValueOrDefault("readme") ?? "README.md";
        var md = new System.Text.StringBuilder();
        foreach (var field in typeof(Ripple.Core.Queries.RippleQueries).GetFields().Concat(typeof(Ripple.Core.Queries.ToolQueries).GetFields())
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != "SchemaNotes"))
        {
            md.AppendLine($"#### `{field.DeclaringType!.Name}.{field.Name}`");
            md.AppendLine();
            md.AppendLine("```cypher");
            md.AppendLine(((string)field.GetRawConstantValue()!).Trim());
            md.AppendLine("```");
            md.AppendLine();
        }
        md.AppendLine("`RippleQueries.Compatible` (publisher→consumer wire types that always deserialise): " +
                      string.Join(", ", Ripple.Core.Queries.RippleQueries.Compatible.Select(c => $"`{c}`")));
        var text = File.ReadAllText(readme);
        const string begin = "<!-- QUERIES:BEGIN -->", end = "<!-- QUERIES:END -->";
        var start = text.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        text = text[..start] + "\n" + md + text[text.IndexOf(end, StringComparison.Ordinal)..];
        File.WriteAllText(readme, text);
        Console.Error.WriteLine($"updated {readme}");
        return 0;
    }
    case "selfcheck":
    {
        // Guards against FalkorDB planning a query as a Cartesian product (see the note in RippleQueries).
        var config = LoadConfig(options);
        var store = new GraphStore(falkor, options.GetValueOrDefault("graph") ?? config.Graph);
        var parameters = new Dictionary<string, object?>
        {
            ["contract"] = null, ["key"] = "", ["path"] = "", ["text"] = "",
            ["compatible"] = Ripple.Core.Queries.RippleQueries.Compatible.Cast<object>().ToList(),
        };
        var failed = 0;
        foreach (var (name, cypher) in Ripple.Core.Queries.RippleQueries.All())
        {
            var plan = store.Explain(cypher, parameters);
            var bad = plan.Any(op => op.Contains("Cartesian Product") || op.Contains("Value Hash Join"));
            failed += bad ? 1 : 0;
            Console.WriteLine($"{(bad ? "FAIL" : "ok  ")} {name}");
        }
        return failed == 0 ? 0 : 1;
    }
    case "eval":
    {
        var config = LoadConfig(options);
        var loaded = await Ripple.Core.Extraction.SolutionLoader.LoadAsync(Require(options, "repo"), config.Services, Log);
        var store = new GraphStore(falkor, config.Graph + ".eval");
        var limit = options.GetValueOrDefault("limit") is { } l ? int.Parse(l) : (int?)null;
        var kinds = options.GetValueOrDefault("kinds")?.Split(',').ToHashSet();
        var report = await new Ripple.Core.Eval.MutationEval(loaded, store, Log).RunAsync(limit, kinds);
        var outDir = options.GetValueOrDefault("out") ?? "eval";
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "results.json"), System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(outDir, "usage-labels.tsv"), new[] { "service\tcontract\tpath" }.Concat(report.UsageLabels));
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
              ripple analyze --repo <path> --base <ref> --head <ref> [--reindex] [--markdown comment.md] [--repo-url https://github.com/o/r/blob/<sha>]
              ripple selfcheck [--graph <name>]   (EXPLAIN every query; fail on Cartesian / value-hash-join plans)
              ripple eval    --repo <path> [--limit N per kind] [--kinds rename,widen,...] [--out eval]
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
        var breaking = report.Cases.Count(c => c.Truth.Count > 0);
        sb.AppendLine($"Mutations: {report.Cases.Count}, all of which compile (the compiler catches none, by construction). " +
                      $"{breaking} break at least one consumer at the wire; {report.Cases.Count - breaking} break nothing.");
        sb.AppendLine("Scored per (mutation, consumer service). Exact = the approach flagged precisely the broken services (including none).");
        sb.AppendLine();
        Table(sb, report.Scores());
        foreach (var kind in report.Cases.Select(c => c.Kind).Distinct())
        {
            var n = report.Cases.Count(c => c.Kind == kind);
            sb.AppendLine($"### {kind} ({n} mutations, {report.Cases.Count(c => c.Kind == kind && c.Truth.Count > 0)} breaking)");
            sb.AppendLine();
            Table(sb, report.Scores(kind));
        }
        sb.AppendLine("### Every mutation");
        sb.AppendLine();
        sb.AppendLine("| Kind | Mutation | Truth (wire) | Ripple | Schema diff | Topology | grep | Wire notes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in report.Cases)
            sb.AppendLine($"| {c.Kind} | {c.Mutation} | {J(c.Truth)} | {Mark(c, "Ripple")} | {Mark(c, "Schema diff (no READS)")} | {Mark(c, "Topology")} | {Mark(c, "grep")} | {string.Join("; ", c.WireNotes).Replace("|", "/")} |");
        return sb.ToString();
    }

    static void Table(System.Text.StringBuilder sb, IReadOnlyList<Ripple.Core.Eval.EvalReport.Score> scores)
    {
        sb.AppendLine("| Approach | Precision | Recall | TP | FP | FN | Exact |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in scores)
            sb.AppendLine($"| {s.Approach} | {(s.Precision is { } p ? p.ToString("P0") : "n/a")} | {s.Recall:P0} | {s.TruePositives} | {s.FalsePositives} | {s.FalseNegatives} | {s.Exact}/{s.Cases} |");
        sb.AppendLine();
    }

    static string Mark(Ripple.Core.Eval.EvalCase c, string approach)
    {
        var p = c.Predictions[approach];
        var ok = p.ToHashSet().SetEquals(c.Truth);
        return (ok ? "✓ " : "✗ ") + J(p);
    }

    static string J(IReadOnlyList<string> xs) => xs.Count == 0 ? "-" : string.Join(", ", xs);
}

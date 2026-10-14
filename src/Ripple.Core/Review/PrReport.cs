using System.Text;
using Ripple.Core.Queries;

namespace Ripple.Core.Review;

/// <summary>
/// Renders a PR analysis as a markdown review comment. Every finding is shown with the graph path that proves it
/// and the name of the Cypher check that returned it; the optional narration only adds prose around them.
/// </summary>
public static class PrReport
{
    public static string Render(PrAnalysis analysis, Narration? narration, string? repoUrl = null)
    {
        var sb = new StringBuilder();
        var findings = analysis.Introduced;
        var services = findings.Select(f => f.ConsumerService).Distinct().ToList();
        var contracts = findings.Select(f => f.Contract).Distinct().ToList();

        sb.AppendLine("## Ripple: cross-service impact");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine($"No new contract breaks. Checked {analysis.ChangedFiles.Count} changed file(s) against the integration-event graph " +
                          $"({analysis.BaselineFindings} pre-existing finding(s) at the base commit are not repeated).");
            return sb.ToString();
        }

        sb.AppendLine($"**This change compiles, but breaks {services.Count} service(s) at run time** " +
                      $"through {contracts.Count} integration event(s): {string.Join(", ", services.Select(s => $"`{s}`"))}.");
        sb.AppendLine();
        if (narration?.Summary is { Length: > 0 } summary)
        {
            sb.AppendLine(summary);
            sb.AppendLine();
        }

        var i = 0;
        foreach (var group in findings.GroupBy(f => (f.Kind, f.Contract, f.ConsumerService, f.Handler)))
        {
            i++;
            var f = group.First();
            sb.AppendLine($"### {i}. `{f.ConsumerService}` — {Title(f)}");
            sb.AppendLine();
            if (narration?.ForFinding(i) is { } explanation)
            {
                sb.AppendLine(explanation.Impact);
                if (explanation.Suggestion is { Length: > 0 } suggestion) sb.AppendLine($"\n**Suggested fix:** {suggestion}");
                sb.AppendLine();
            }
            sb.AppendLine("Graph path:");
            sb.AppendLine("```");
            foreach (var line in PathChain(group.ToList())) sb.AppendLine(line);
            sb.AppendLine("```");
            sb.AppendLine($"<sub>severity **{f.Severity}** · produced by `{f.Check}` · " +
                          $"publisher {Link(repoUrl, f.PublisherFile, f.PublisherLine)} · consumer {Link(repoUrl, f.ReaderFile, f.ReaderLine)}</sub>");
            sb.AppendLine();
        }

        sb.AppendLine("<details><summary>How Ripple knows</summary>");
        sb.AppendLine();
        sb.AppendLine($"Ripple indexed the base (`{analysis.BaseSha[..10]}`) and head (`{analysis.HeadSha[..10]}`) commits into two FalkorDB graphs " +
                      $"(`{analysis.BaseGraph}`, `{analysis.HeadGraph}`) and reports findings present only in head. Each service declares its own copy " +
                      "of an integration event; copies are linked through an `EventContract` node keyed by the routing name and compared field by field " +
                      "on JSON wire paths. The findings above come from these read-only queries:");
        sb.AppendLine();
        foreach (var check in findings.Select(f => f.Check).Distinct())
        {
            var cypher = RippleQueries.ContractChecks.First(c => c.Name == check).Cypher;
            sb.AppendLine($"`{check}`");
            sb.AppendLine("```cypher");
            sb.AppendLine(cypher.Trim());
            sb.AppendLine("```");
        }
        sb.AppendLine("</details>");
        if (narration is { Source: var source }) sb.AppendLine($"\n<sub>Explanations: {source}. Findings and paths come only from the graph.</sub>");
        return sb.ToString();
    }

    static string Title(Finding f) => f.Kind switch
    {
        "dangling-read" => $"reads a field `{f.Contract}` no longer sends",
        "dangling-forward" => $"forwards `{f.Contract}` with fields that are no longer sent",
        "type-drift" => $"cannot deserialise `{f.Contract}` (type changed)",
        "unfillable-read" => $"reads a field of `{f.Contract}` that can never be set",
        "unbindable-constructor" => $"cannot deserialise `{f.Contract}` (constructor parameter matches no property)",
        "starved-subscription" => $"subscribes to `{f.Contract}`, which nothing publishes any more",
        _ => f.Kind,
    };

    /// <summary>publisher method -PUBLISHES-> shape -SHAPE_OF-> contract <-SHAPE_OF- shape <-HANDLES- handler -READS-> field</summary>
    static IEnumerable<string> PathChain(IReadOnlyList<Finding> group)
    {
        var f = group[0];
        if (f.Publisher is not null)
        {
            yield return $"({f.PublisherService}) {Short(f.Publisher)}";
            yield return $"  -[:PUBLISHES]-> (:EventShape {f.PublisherService})";
        }
        yield return $"  -[:SHAPE_OF]-> (:EventContract {{name: '{f.Contract}'}})";
        yield return $"  <-[:SHAPE_OF]- (:EventShape {f.ConsumerService})";
        yield return $"  <-[:HANDLES]- ({f.ConsumerService}) {Short(f.Handler)}";
        foreach (var x in group)
        {
            var reader = x.Reader is not null && x.Reader != x.Handler ? $"  -[:CALLS*]-> {Short(x.Reader)}\n" : "";
            var edge = x.Kind switch
            {
                "dangling-forward" => "-[:FORWARDS]->",
                "type-drift" => "has",
                "starved-subscription" or "unbindable-constructor" => null,
                _ => "-[:READS]->",
            };
            if (edge is null) continue;
            var detail = x.Kind == "type-drift" ? $"  (expects {x.Expected}, receives {x.Actual})" : x.Kind == "unfillable-read" ? "  (no setter or constructor parameter)" : "  (missing in publisher's copy)";
            yield return $"{reader}  {edge} (:Property {{path: '{x.Path}'}}){detail}";
        }
    }

    static string Short(string? fqn)
    {
        if (fqn is null) return "?";
        if (!fqn.Contains('(')) return fqn.Split('.')[^1];   // a type name (e.g. a subscription's handler class)
        var paren = fqn.IndexOf('(');
        var head = paren < 0 ? fqn : fqn[..paren];
        var parts = head.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}()" : head;
    }

    static string Link(string? repoUrl, string? file, int? line) =>
        file is null ? "—" : repoUrl is null ? $"`{file}:{line}`" : $"[`{Path.GetFileName(file)}:{line}`]({repoUrl.TrimEnd('/')}/{file}#L{line})";
}

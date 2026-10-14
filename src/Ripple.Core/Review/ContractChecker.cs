using Ripple.Core.Graph;
using Ripple.Core.Queries;

namespace Ripple.Core.Review;

/// <summary>Runs every contract check (Q1–Q5) against one graph.</summary>
public static class ContractChecker
{
    public static IReadOnlyList<Finding> Run(GraphStore store, string? contract = null)
    {
        var findings = new List<Finding>();
        foreach (var (name, cypher, severity) in RippleQueries.ContractChecks)
            foreach (var row in store.Query(cypher, new Dictionary<string, object?> { ["contract"] = contract }))
                findings.Add(Finding.FromRow(row, name, severity));
        return findings;
    }
}

using Ripple.Core.Graph;
using Ripple.Core.Queries;

namespace Ripple.Core.Review;

/// <summary>Runs every contract check (Q1–Q5) against one graph.</summary>
public static class ContractChecker
{
    /// <param name="maxHops">How far reads are followed through CALLS from the handler (the eval ablates this to 0).</param>
    public static IReadOnlyList<Finding> Run(GraphStore store, string? contract = null, int maxHops = 3)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["contract"] = contract,
            ["compatible"] = RippleQueries.Compatible.Cast<object>().ToList(),
        };
        var findings = new List<Finding>();
        foreach (var (name, cypher, severity) in RippleQueries.ContractChecks)
            foreach (var row in store.Query(cypher.Replace("[:CALLS*0..3]", $"[:CALLS*0..{maxHops}]"), parameters))
                findings.Add(Finding.FromRow(row, name, severity));
        return findings;
    }
}

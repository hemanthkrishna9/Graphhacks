using System.Diagnostics;
using Ripple.Core.Extraction;
using Ripple.Core.Graph;

namespace Ripple.Core;

/// <summary>Repo checkout → Roslyn → FalkorDB graph.</summary>
public static class Indexer
{
    /// <summary>Derived structure computed in the graph itself: the service projection and algorithm scores.</summary>
    public static void Derive(GraphStore graph)
    {
        graph.Execute(Queries.RippleQueries.MaterializeDependsOn);
        graph.Execute(Queries.RippleQueries.MaterializeTriggers);
        graph.Execute(Queries.RippleQueries.BridgeScores);
        graph.Execute(Queries.RippleQueries.DependencyRank);
    }

    public static async Task<GraphStats> IndexAsync(string repoRoot, RippleConfig config, GraphStore store, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var loaded = await SolutionLoader.LoadAsync(repoRoot, config.Services, log);
        var batch = await new CodeGraphExtractor(loaded, log).ExtractAsync();
        var pruned = batch.PruneDanglingEdges();
        log($"extracted {batch.Nodes.Count} nodes, {batch.Edges.Count} edges ({pruned} edges to undeclared code dropped) in {sw.Elapsed.TotalSeconds:F1}s");
        store.Replace(batch, Derive);
        log($"wrote graph '{store.Name}' in {sw.Elapsed.TotalSeconds:F1}s");
        loaded.Workspace.Dispose();
        return new GraphStats(batch.Nodes.Count, batch.Edges.Count, sw.Elapsed);
    }
}

public sealed record GraphStats(int Nodes, int Edges, TimeSpan Elapsed);

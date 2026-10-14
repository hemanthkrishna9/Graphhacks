using System.Diagnostics;
using Ripple.Core.Extraction;
using Ripple.Core.Graph;

namespace Ripple.Core;

/// <summary>Repo checkout → Roslyn → FalkorDB graph.</summary>
public static class Indexer
{
    public static async Task<GraphStats> IndexAsync(string repoRoot, RippleConfig config, GraphStore store, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var loaded = await SolutionLoader.LoadAsync(repoRoot, config.Services, log);
        var batch = await new CodeGraphExtractor(loaded, log).ExtractAsync();
        var pruned = batch.PruneDanglingEdges();
        log($"extracted {batch.Nodes.Count} nodes, {batch.Edges.Count} edges ({pruned} edges to undeclared code dropped) in {sw.Elapsed.TotalSeconds:F1}s");
        store.Replace(batch);
        log($"wrote graph '{store.Name}' in {sw.Elapsed.TotalSeconds:F1}s");
        loaded.Workspace.Dispose();
        return new GraphStats(batch.Nodes.Count, batch.Edges.Count, sw.Elapsed);
    }
}

public sealed record GraphStats(int Nodes, int Edges, TimeSpan Elapsed);

using System.Diagnostics;
using Ripple.Core.Graph;

namespace Ripple.Core.Review;

/// <summary>
/// PR mode: index the base and head commits into two graphs ("{graph}@{sha}") and report the contract findings that
/// exist in head but not in base. Graphs are cached by commit, so re-running on the same commits is query-only.
/// </summary>
public sealed class PrAnalyzer(RippleConfig config, GraphStore store, Action<string> log)
{
    public async Task<PrAnalysis> AnalyzeAsync(string repo, string baseRef, string headRef, bool reindex = false)
    {
        repo = Path.GetFullPath(repo);
        var baseSha = Git(repo, "rev-parse", baseRef);
        var headSha = Git(repo, "rev-parse", headRef);
        var mergeBase = Git(repo, "merge-base", baseSha, headSha);

        var baseGraph = await EnsureGraphAsync(repo, mergeBase, reindex);
        var headGraph = await EnsureGraphAsync(repo, headSha, reindex);

        var before = ContractChecker.Run(baseGraph);
        var after = ContractChecker.Run(headGraph);
        var known = before.Select(f => f.Id).ToHashSet();
        var introduced = after.Where(f => !known.Contains(f.Id)).ToList();
        var fixedIds = after.Select(f => f.Id).ToHashSet();
        var resolved = before.Where(f => !fixedIds.Contains(f.Id)).ToList();

        var changedFiles = Git(repo, "diff", "--name-only", $"{mergeBase}..{headSha}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        log($"{changedFiles.Length} changed files; {before.Count} findings at base, {after.Count} at head, {introduced.Count} introduced");

        return new PrAnalysis(mergeBase, headSha, baseGraph.Name, headGraph.Name, changedFiles, introduced, resolved, before.Count);
    }

    async Task<GraphStore> EnsureGraphAsync(string repo, string sha, bool reindex)
    {
        var graph = store.WithGraph($"{config.Graph}@{sha[..10]}");
        if (graph.Exists() && !reindex)
        {
            log($"using cached graph {graph.Name}");
            return graph;
        }

        var worktree = Path.Combine(Path.GetTempPath(), "ripple-worktrees", sha[..10]);
        if (!Directory.Exists(worktree))
        {
            Git(repo, "worktree", "add", "--detach", worktree, sha);
            foreach (var project in config.Services)
                Run("dotnet", worktree, "restore", project, "-v", "q");
        }
        await Indexer.IndexAsync(worktree, config, graph, log);
        return graph;
    }

    static string Git(string repo, params string[] args) => Run("git", repo, args);

    static string Run(string exe, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} {string.Join(' ', args)} failed: {stderr}{stdout}");
        return stdout.Trim();
    }
}

public sealed record PrAnalysis(
    string BaseSha, string HeadSha, string BaseGraph, string HeadGraph,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<Finding> Introduced,
    IReadOnlyList<Finding> Resolved,
    int BaselineFindings);

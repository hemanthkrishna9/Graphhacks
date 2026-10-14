using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Ripple.Core.Extraction;

/// <summary>
/// Loads the deployable projects of a .NET solution with MSBuildWorkspace.
/// Each deployable (*.API, *Processor, WebApp) becomes a Service in the graph.
/// </summary>
public static class SolutionLoader
{
    static readonly object LocatorLock = new();

    public static void EnsureMsBuild()
    {
        lock (LocatorLock)
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        }
    }

    public static async Task<LoadedSolution> LoadAsync(string repoRoot, IReadOnlyList<string> serviceProjects, Action<string>? log = null)
    {
        EnsureMsBuild();
        log ??= _ => { };
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) log($"workspace: {e.Diagnostic.Message}");
        });

        var services = new List<(string Name, Project Project)>();
        foreach (var rel in serviceProjects)
        {
            var path = Path.GetFullPath(Path.Combine(repoRoot, rel));
            var existing = workspace.CurrentSolution.Projects.FirstOrDefault(p => PathEq(p.FilePath, path));
            var project = existing ?? await workspace.OpenProjectAsync(path);
            services.Add((Path.GetFileNameWithoutExtension(path), project));
            log($"loaded {project.Name}: {project.DocumentIds.Count} documents");
        }

        // Re-resolve projects against the final solution snapshot (opening later projects forks the solution).
        var solution = workspace.CurrentSolution;
        var resolved = services.Select(s => (s.Name, solution.GetProject(s.Project.Id)!)).ToList();
        return new LoadedSolution(workspace, solution, Path.GetFullPath(repoRoot), resolved);
    }

    static bool PathEq(string? a, string b) => a is not null && string.Equals(Path.GetFullPath(a), b, StringComparison.Ordinal);
}

public sealed record LoadedSolution(
    MSBuildWorkspace Workspace,
    Solution Solution,
    string RepoRoot,
    IReadOnlyList<(string Name, Project Project)> Services);

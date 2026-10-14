using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Ripple.Core.Extraction;
using Ripple.Core.Graph;
using Ripple.Core.Review;

namespace Ripple.Core.Eval;

/// <summary>
/// Automated mutation benchmark. For every property of every published integration event, apply two compile-safe
/// mutations on the publisher side with Roslyn's Renamer:
///   rename        — P becomes PRenamed (the JSON wire name changes: a contract break)
///   rename-keep   — the same rename plus [JsonPropertyName("P")] (wire format unchanged: a control, nothing breaks)
/// Each mutation is scored at (mutation, consumer service) granularity against an oracle, for four approaches:
/// the compiler, a grep baseline, a call-graph-only ablation (no contract layer) and Ripple.
///
/// The oracle does not use the graph: it walks consumer copies of the contract with Roslyn's SymbolFinder and calls a
/// consumer broken when code in that service reads the property at the changed wire path, or passes the whole event to
/// an untyped parameter (serialisation).
/// </summary>
public sealed class MutationEval(LoadedSolution loaded, GraphStore store, Action<string> log)
{
    public async Task<EvalReport> RunAsync(int? limit = null)
    {
        var baseSolution = loaded.Solution;
        var deployables = loaded.Services.Select(s => s.Project.AssemblyName).ToHashSet(StringComparer.Ordinal);

        log("indexing baseline");
        var baseBatch = await new CodeGraphExtractor(loaded).ExtractAsync();
        baseBatch.PruneDanglingEdges();
        var baseGraph = store.WithGraph(store.Name + ".base");
        baseGraph.Replace(baseBatch);
        var baselineIds = ContractChecker.Run(baseGraph).Select(f => f.Id).ToHashSet();

        var targets = await EnumerateTargetsAsync(baseSolution, baseBatch);
        log($"{targets.Count} publisher-side event properties to mutate");
        if (limit is { } n) targets = targets.Take(n).ToList();

        var cases = new List<EvalCase>();
        foreach (var target in targets)
        {
            foreach (var kind in new[] { "rename", "rename-keep" })
            {
                var mutated = await MutateAsync(baseSolution, target, kind);
                if (mutated is null) { log($"skip {kind} {target.Label}: does not compile"); continue; }

                var truth = kind == "rename" ? await OracleAsync(baseSolution, target, deployables) : [];
                var grep = GrepBaseline(target, deployables);
                var callGraph = CallGraphBaseline(baseGraph, target, deployables);

                var mutatedLoaded = loaded with { Solution = mutated };
                var batch = await new CodeGraphExtractor(mutatedLoaded).ExtractAsync();
                batch.PruneDanglingEdges();
                store.Replace(batch);
                var introduced = ContractChecker.Run(store).Where(f => !baselineIds.Contains(f.Id)).ToList();
                var ripple = introduced.Select(f => f.ConsumerService).ToHashSet(StringComparer.Ordinal);

                var c = new EvalCase(target.Label, kind, target.PublisherService, truth.Order().ToList(),
                    Compiler: [], Grep: grep.Order().ToList(), CallGraph: callGraph.Order().ToList(), Ripple: ripple.Order().ToList(),
                    RippleFindings: introduced.Select(f => $"{f.Kind} {f.Contract} -> {f.ConsumerService} {f.Path}").Distinct().ToList());
                cases.Add(c);
                log($"{kind,-11} {target.Label}: truth [{string.Join(",", c.Truth)}] ripple [{string.Join(",", c.Ripple)}] " +
                    $"grep [{string.Join(",", c.Grep)}] callgraph [{string.Join(",", c.CallGraph)}]");
            }
        }
        return new EvalReport(cases);
    }

    // ---------------------------------------------------------------- targets

    public sealed record Target(
        string PublisherService, string TypeName, string PropertyName, string SymbolId, string ProjectName,
        IReadOnlyList<(string Contract, string Path)> WirePaths)
    {
        public string Label => $"{PublisherService}:{TypeName}.{PropertyName}";
    }

    /// <summary>Source properties of published event shapes (and types nested in them), excluding the shared base event.</summary>
    async Task<List<Target>> EnumerateTargetsAsync(Solution solution, GraphBatch baseBatch)
    {
        var published = baseBatch.Edges.Keys.Where(e => e.Type == "PUBLISHES").Select(e => e.ToKey).ToHashSet(StringComparer.Ordinal);
        var byProperty = new Dictionary<string, (IPropertySymbol Symbol, Project Project, List<(string, string)> Paths)>(StringComparer.Ordinal);

        foreach (var project in solution.Projects.Where(p => loaded.Services.Any(s => s.Project.AssemblyName == p.AssemblyName)))
        {
            var compilation = (await project.GetCompilationAsync())!;
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                if (!CodeGraphExtractor.IsIntegrationEvent(type) || !published.Contains(CodeGraphExtractor.TypeKey(type))) continue;
                foreach (var (prop, path) in Walk(type, ""))
                {
                    if (!SymbolEqualityComparer.Default.Equals(prop.ContainingAssembly, compilation.Assembly)) continue;   // base IntegrationEvent props
                    var id = prop.GetDocumentationCommentId()!;
                    if (!byProperty.TryGetValue(id, out var entry)) byProperty[id] = entry = (prop, project, []);
                    entry.Paths.Add((type.Name, path));
                }
            }
        }

        return byProperty.Select(kv => new Target(
                kv.Value.Project.AssemblyName, kv.Value.Symbol.ContainingType.Name, kv.Value.Symbol.Name, kv.Key, kv.Value.Project.Name,
                kv.Value.Paths))
            .OrderBy(t => t.Label, StringComparer.Ordinal).ToList();
    }

    static IEnumerable<(IPropertySymbol Prop, string Path)> Walk(ITypeSymbol type, string prefix, int depth = 0)
    {
        foreach (var p in SerializedProperties(type))
        {
            var path = prefix + p.Name;
            yield return (p, path);
            var element = ElementType(p.Type);
            var value = element ?? p.Type;
            if (depth < 4 && value.TypeKind is TypeKind.Class or TypeKind.Struct && value.SpecialType == SpecialType.None && value.Locations.Any(l => l.IsInSource))
                foreach (var inner in Walk(value, path + (element is null ? "." : "[]."), depth + 1))
                    yield return inner;
        }
    }

    // ---------------------------------------------------------------- mutation

    async Task<Solution?> MutateAsync(Solution solution, Target target, string kind)
    {
        var project = solution.Projects.First(p => p.Name == target.ProjectName);
        var compilation = (await project.GetCompilationAsync())!;
        if (DocumentationCommentId.GetFirstSymbolForDeclarationId(target.SymbolId, compilation) is not IPropertySymbol symbol) return null;

        var newName = target.PropertyName + "Renamed";
        var renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName);

        if (kind == "rename-keep")
            renamed = await KeepWireNameAsync(renamed, target, newName) ?? renamed;

        // Every changed project (and its dependents) must still compile: these are "green build" mutations.
        foreach (var changed in renamed.GetChanges(solution).GetProjectChanges().Select(c => c.ProjectId))
        {
            var ids = new[] { changed }.Concat(renamed.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(changed));
            foreach (var id in ids.Distinct())
            {
                var c = await renamed.GetProject(id)!.GetCompilationAsync();
                if (c!.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return null;
            }
        }
        return renamed;
    }

    static async Task<Solution?> KeepWireNameAsync(Solution solution, Target target, string newName)
    {
        var project = solution.Projects.First(p => p.Name == target.ProjectName);
        var compilation = (await project.GetCompilationAsync())!;
        var type = AllTypes(compilation.Assembly.GlobalNamespace).FirstOrDefault(t => t.Name == target.TypeName);
        var prop = type?.GetMembers(newName).OfType<IPropertySymbol>().FirstOrDefault();
        var syntaxRef = prop?.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null) return null;

        var node = await syntaxRef.GetSyntaxAsync();
        var attribute = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName("System.Text.Json.Serialization.JsonPropertyName"),
            SyntaxFactory.ParseAttributeArgumentList($"(\"{target.PropertyName}\")"));
        SyntaxNode replacement = node switch
        {
            PropertyDeclarationSyntax p => p.AddAttributeLists(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute))),
            ParameterSyntax p => p.AddAttributeLists(SyntaxFactory.AttributeList(
                SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.PropertyKeyword)),
                SyntaxFactory.SingletonSeparatedList(attribute))),
            _ => node,
        };
        if (replacement == node) return null;
        var document = solution.GetDocument(syntaxRef.SyntaxTree)!;
        var root = (await document.GetSyntaxRootAsync())!;
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(node, replacement));
    }

    // ---------------------------------------------------------------- oracle (no graph)

    async Task<HashSet<string>> OracleAsync(Solution solution, Target target, HashSet<string> deployables)
    {
        var broken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects.Where(p => deployables.Contains(p.AssemblyName) && p.AssemblyName != target.PublisherService))
        {
            var compilation = (await project.GetCompilationAsync())!;
            foreach (var (contract, path) in target.WirePaths)
            {
                // The consumer's own copy of the contract, and only if this service actually handles it.
                var copy = AllTypes(compilation.Assembly.GlobalNamespace).FirstOrDefault(t => t.Name == contract && CodeGraphExtractor.IsIntegrationEvent(t));
                if (copy is null || !Handles(compilation, copy)) continue;

                // A field the consumer's copy doesn't declare never arrived anyway: renaming it can't break this consumer.
                var prop = Walk(copy, "").Where(x => x.Path == path).Select(x => x.Prop).FirstOrDefault();
                if (prop is null) continue;
                if (await ForwardsWholeAsync(project, compilation, copy)) { broken.Add(project.AssemblyName); continue; }

                var refs = await SymbolFinder.FindReferencesAsync(prop, solution, project.Documents.ToImmutableHashSet());
                foreach (var location in refs.SelectMany(r => r.Locations))
                {
                    var node = (await location.Document.GetSyntaxRootAsync())!.FindNode(location.Location.SourceSpan);
                    var model = (await location.Document.GetSemanticModelAsync())!;
                    if (IsWrite(node) || node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } decl
                        && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(decl), prop.ContainingType))
                        continue;
                    if (IsInLoggingCall(node)) continue;
                    broken.Add(project.AssemblyName);
                    break;
                }
            }
        }
        return broken;
    }

    static bool Handles(Compilation compilation, INamedTypeSymbol contract) =>
        AllTypes(compilation.Assembly.GlobalNamespace).Any(t => !t.IsAbstract && t.AllInterfaces.Any(i =>
            i.Name == "IIntegrationEventHandler" && i.TypeArguments.Length == 1 && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], contract)));

    /// <summary>The consumer passes the whole event object to an object-typed parameter (e.g. a webhook payload).</summary>
    static async Task<bool> ForwardsWholeAsync(Project project, Compilation compilation, INamedTypeSymbol contract)
    {
        foreach (var document in project.Documents)
        {
            var root = (await document.GetSyntaxRootAsync())!;
            var model = compilation.GetSemanticModel(root.SyntaxTree);
            foreach (var arg in root.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(arg.Expression).Type, contract) || IsInLoggingCall(arg)) continue;
                if (model.GetSymbolInfo(arg.Parent!.Parent!).Symbol is IMethodSymbol m)
                {
                    var index = ((BaseArgumentListSyntax)arg.Parent).Arguments.IndexOf(arg);
                    if (index < m.Parameters.Length && m.Parameters[index].Type.SpecialType == SpecialType.System_Object) return true;
                }
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- baselines

    /// <summary>"Diff + grep": any consumer service whose source mentions the old property name as a word.</summary>
    HashSet<string> GrepBaseline(Target target, HashSet<string> deployables)
    {
        var word = new Regex($@"\b{Regex.Escape(target.PropertyName)}\b");
        var hits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, project) in loaded.Services)
        {
            if (project.AssemblyName == target.PublisherService || !deployables.Contains(project.AssemblyName)) continue;
            var dir = Path.GetDirectoryName(project.FilePath)!;
            if (Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Any(f => word.IsMatch(File.ReadAllText(f))))
                hits.Add(project.AssemblyName);
        }
        return hits;
    }

    /// <summary>
    /// Ablation: the same graph without the contract layer. From every method that creates the event, follow CALLS
    /// (syntactic, MediatR, domain-event and interface dispatch) and report other services reached.
    /// </summary>
    static HashSet<string> CallGraphBaseline(GraphStore baseGraph, Target target, HashSet<string> deployables)
    {
        var hits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in target.WirePaths.Select(p => p.Contract).Distinct())
        {
            var rows = baseGraph.Query("""
                MATCH (pub:Method)-[:PUBLISHES]->(:EventShape {name: $contract, service: $service})
                MATCH (pub)-[:CALLS*1..8]->(m:Method)
                WHERE m.service <> $service
                RETURN DISTINCT m.service AS service
                """, new Dictionary<string, object?> { ["contract"] = contract, ["service"] = target.PublisherService });
            foreach (var r in rows)
                if (r["service"] is string s && deployables.Contains(s)) hits.Add(s);
        }
        return hits;
    }

    // ---------------------------------------------------------------- helpers

    static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
        {
            yield return t;
            foreach (var nested in t.GetTypeMembers()) yield return nested;
        }
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in AllTypes(child)) yield return t;
    }

    static IEnumerable<IPropertySymbol> SerializedProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                if (!p.IsStatic && !p.IsIndexer && p.GetMethod is not null && p.Name != "EqualityContract"
                    && p.DeclaredAccessibility == Accessibility.Public && seen.Add(p.Name))
                    yield return p;
    }

    static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol a) return a.ElementType;
        if (type.SpecialType == SpecialType.System_String) return null;
        return type is INamedTypeSymbol { IsGenericType: true } n
            ? n.AllInterfaces.Concat([n]).FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0]
            : null;
    }

    static bool IsWrite(SyntaxNode node)
    {
        var expr = node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node ? ma : node;
        return expr.Parent is AssignmentExpressionSyntax assign && assign.Left == expr;
    }

    static bool IsInLoggingCall(SyntaxNode node) =>
        node.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var name }
        && name.StartsWith("Log", StringComparison.Ordinal);
}

public sealed record EvalCase(
    string Mutation, string Kind, string PublisherService, IReadOnlyList<string> Truth,
    IReadOnlyList<string> Compiler, IReadOnlyList<string> Grep, IReadOnlyList<string> CallGraph, IReadOnlyList<string> Ripple,
    IReadOnlyList<string> RippleFindings);

public sealed record EvalReport(IReadOnlyList<EvalCase> Cases)
{
    public sealed record Score(string Approach, int TruePositives, int FalsePositives, int FalseNegatives, int BreaksCaught, int Breaks, int ControlsFlagged, int Controls)
    {
        public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);
        public double Recall => TruePositives + FalseNegatives == 0 ? 1 : (double)TruePositives / (TruePositives + FalseNegatives);
    }

    public IReadOnlyList<Score> Scores()
    {
        var approaches = new (string Name, Func<EvalCase, IReadOnlyList<string>> Pick)[]
        {
            ("Compiler (dotnet build)", c => c.Compiler),
            ("grep for the property name", c => c.Grep),
            ("Call graph only (no contract layer)", c => c.CallGraph),
            ("Ripple (contract graph)", c => c.Ripple),
        };
        return approaches.Select(a =>
        {
            int tp = 0, fp = 0, fn = 0, caught = 0, breaks = 0, controlsFlagged = 0, controls = 0;
            foreach (var c in Cases)
            {
                var predicted = a.Pick(c).ToHashSet();
                var truth = c.Truth.ToHashSet();
                tp += predicted.Count(truth.Contains);
                fp += predicted.Count(p => !truth.Contains(p));
                fn += truth.Count(t => !predicted.Contains(t));
                if (truth.Count > 0) { breaks++; if (truth.SetEquals(predicted)) caught++; }
                if (c.Kind == "rename-keep") { controls++; if (predicted.Count > 0) controlsFlagged++; }
            }
            return new Score(a.Name, tp, fp, fn, caught, breaks, controlsFlagged, controls);
        }).ToList();
    }
}

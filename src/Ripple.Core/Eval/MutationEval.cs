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
/// Automated mutation benchmark. Every mutation is applied with Roslyn to the real eShop solution and must compile
/// (so the compiler catches none of them by construction). Classes:
///   rename          publisher renames an event property                         (wire name changes)
///   rename-keep     same rename plus [JsonPropertyName(old)]                   (control: wire unchanged)
///   json-ignore     publisher adds [JsonIgnore] to a property                   (field no longer sent)
///   nullable        publisher makes a value-type property nullable              (may send null)
///   widen           publisher changes int to long                               (may send values > int.MaxValue)
///   consumer-rename a consumer renames its copy's property                      (reads a field nobody sends)
///   event-rename    publisher renames the event class                           (routing key changes)
///
/// Ground truth per (mutation, consumer service) = the consumer handles the contract AND, at the wire
/// (<see cref="WireOracle"/>: real System.Text.Json round-trip with adversarial values), deserialisation throws, or the
/// message is no longer routed to it, or a JSON path it uses comes back different. "Uses" comes from a usage oracle
/// (Roslyn SymbolFinder over the consumer's copy, plus whole-event forwarding) — exported as labels for hand review.
/// </summary>
public sealed class MutationEval(LoadedSolution loaded, GraphStore store, Action<string> log)
{
    public static readonly string[] Kinds = ["rename", "rename-keep", "json-ignore", "nullable", "widen", "consumer-rename", "event-rename"];

    readonly HashSet<string> _deployables = loaded.Services.Select(s => s.Project.AssemblyName).ToHashSet(StringComparer.Ordinal);
    readonly WireOracle _wire = new();

    public async Task<EvalReport> RunAsync(int? limitPerKind = null, IReadOnlySet<string>? kinds = null)
    {
        var baseSolution = loaded.Solution;
        log("indexing baseline");
        var baseBatch = await new CodeGraphExtractor(loaded).ExtractAsync();
        baseBatch.PruneDanglingEdges();
        var baseGraph = store.WithGraph(store.Name + ".base");
        baseGraph.Replace(baseBatch);
        var baseline = Approaches.ToDictionary(a => a.Name, a => a.Run(baseGraph).Select(f => f.Id).ToHashSet());

        var mutations = await EnumerateMutationsAsync(baseSolution);
        log($"{mutations.Count} candidate mutations");
        var cases = new List<EvalCase>();
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var group in mutations.GroupBy(m => m.Kind))
        {
            if (kinds is not null && !kinds.Contains(group.Key)) continue;
            foreach (var mutation in limitPerKind is { } n ? group.Take(n) : group)
            {
                var mutated = await ApplyAsync(baseSolution, mutation);
                if (mutated is null) { log($"skip {mutation.Label}: does not compile"); continue; }

                EvalCase c;
                try { c = await ScoreAsync(mutated, mutation, baseGraph, baseline, labels); }
                catch (Exception e) { log($"skip {mutation.Label}: oracle error {e.GetType().Name}: {e.Message}"); continue; }
                cases.Add(c);
                log($"{c.Kind,-15} {c.Mutation}: truth [{string.Join(",", c.Truth)}] ripple [{string.Join(",", c.Predictions["Ripple"])}]");
            }
        }
        store.Drop();   // the scratch graph only ever holds the last mutant
        return new EvalReport(cases, labels.ToList());
    }

    // ---------------------------------------------------------------- mutations

    public sealed record Mutation(string Kind, string Project, string SymbolId, string TypeName, string Member, IReadOnlyList<(string Contract, string Path)> WirePaths)
    {
        public string Label => $"{Project}:{TypeName}{(Member.Length > 0 ? "." + Member : "")}";
        public bool ConsumerSide => Kind == "consumer-rename";
    }

    async Task<List<Mutation>> EnumerateMutationsAsync(Solution solution)
    {
        var list = new List<Mutation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in DeployableProjects(solution))
        {
            var compilation = (await project.GetCompilationAsync())!;
            var published = PublishedEventNames(compilation);
            var handled = HandledEvents(compilation).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace).Where(CodeGraphExtractor.IsIntegrationEvent))
            {
                var isPublisher = published.Contains(type.Name);
                var isConsumer = handled.Contains(type.Name) && !isPublisher;
                if (isPublisher)
                    list.Add(new Mutation("event-rename", project.Name, type.GetDocumentationCommentId()!, type.Name, "", [(type.Name, "")]));

                foreach (var (prop, path) in Walk(type, ""))
                {
                    if (!SymbolEqualityComparer.Default.Equals(prop.ContainingAssembly, compilation.Assembly)) continue;   // base IntegrationEvent
                    var id = prop.GetDocumentationCommentId()!;
                    var kinds = new List<string>();
                    if (isPublisher)
                    {
                        kinds.AddRange(["rename", "rename-keep", "json-ignore"]);
                        if (prop.Type.IsValueType && prop.Type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T) kinds.Add("nullable");
                        if (prop.Type.SpecialType == SpecialType.System_Int32) kinds.Add("widen");
                    }
                    if (isConsumer) kinds.Add("consumer-rename");
                    foreach (var kind in kinds)
                        list.Add(new Mutation(kind, project.Name, id, prop.ContainingType.Name, prop.Name, [(type.Name, path)]));
                }
            }
        }
        // A nested type shared by several events is mutated once, with every (contract, path) it appears at.
        return list.GroupBy(m => (m.Kind, m.Project, m.SymbolId))
            .Select(g => g.First() with { WirePaths = g.SelectMany(m => m.WirePaths).Distinct().ToList() })
            .OrderBy(m => Array.IndexOf(Kinds, m.Kind)).ThenBy(m => m.Label, StringComparer.Ordinal).ToList();
    }

    async Task<Solution?> ApplyAsync(Solution solution, Mutation m)
    {
        var project = solution.Projects.First(p => p.Name == m.Project);
        var compilation = (await project.GetCompilationAsync())!;
        var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(m.SymbolId, compilation);
        if (symbol is null) return null;

        Solution? mutated = m.Kind switch
        {
            "rename" or "consumer-rename" => await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), m.Member + "Renamed"),
            "event-rename" => await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), m.TypeName.Replace("IntegrationEvent", "V2IntegrationEvent")),
            "rename-keep" => await KeepWireNameAsync(await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), m.Member + "Renamed"), m),
            "json-ignore" => await EditDeclarationAsync(solution, symbol, decl => AddPropertyAttribute(decl, "System.Text.Json.Serialization.JsonIgnore", null)),
            "nullable" => await EditDeclarationAsync(solution, symbol, decl => ChangeType(decl, t => SyntaxFactory.NullableType(t.WithoutTrivia()).WithTriviaFrom(t))),
            "widen" => await EditDeclarationAsync(solution, symbol, decl => ChangeType(decl, t => SyntaxFactory.ParseTypeName("long").WithTriviaFrom(t))),
            _ => null,
        };
        if (mutated is null) return null;

        // Every changed project and everything depending on it must compile: these are "green build" mutations.
        foreach (var changed in mutated.GetChanges(solution).GetProjectChanges().Select(c => c.ProjectId))
        {
            var ids = new[] { changed }.Concat(mutated.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(changed));
            foreach (var id in ids.Distinct())
            {
                var c = await mutated.GetProject(id)!.GetCompilationAsync();
                if (c!.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return null;
            }
        }
        return mutated;
    }

    /// <summary>After a rename, pin the JSON name back to the old one. Returns null (mutation skipped) if that fails.</summary>
    static async Task<Solution?> KeepWireNameAsync(Solution renamed, Mutation m)
    {
        var project = renamed.Projects.First(p => p.Name == m.Project);
        var compilation = (await project.GetCompilationAsync())!;
        var newId = m.SymbolId[..(m.SymbolId.LastIndexOf('.') + 1)] + m.Member + "Renamed";
        var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(newId, compilation);
        return symbol is null ? null : await EditDeclarationAsync(renamed, symbol, decl => AddPropertyAttribute(decl, "System.Text.Json.Serialization.JsonPropertyName", $"\"{m.Member}\""));
    }

    static async Task<Solution?> EditDeclarationAsync(Solution solution, ISymbol symbol, Func<SyntaxNode, SyntaxNode?> edit)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null) return null;
        var node = await syntaxRef.GetSyntaxAsync();
        var replacement = edit(node);
        if (replacement is null) return null;
        var document = solution.GetDocument(syntaxRef.SyntaxTree)!;
        var root = (await document.GetSyntaxRootAsync())!;
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(node, replacement));
    }

    static SyntaxNode? AddPropertyAttribute(SyntaxNode decl, string name, string? argument)
    {
        var attribute = SyntaxFactory.Attribute(SyntaxFactory.ParseName(name),
            argument is null ? null : SyntaxFactory.ParseAttributeArgumentList($"({argument})"));
        var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));
        return decl switch
        {
            PropertyDeclarationSyntax p => p.AddAttributeLists(list),
            ParameterSyntax p => p.AddAttributeLists(list.WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.PropertyKeyword)))),
            _ => null,
        };
    }

    static SyntaxNode? ChangeType(SyntaxNode decl, Func<TypeSyntax, TypeSyntax> change) => decl switch
    {
        PropertyDeclarationSyntax p => p.WithType(change(p.Type)),
        ParameterSyntax { Type: { } t } p => p.WithType(change(t)),
        _ => null,
    };

    // ---------------------------------------------------------------- scoring

    async Task<EvalCase> ScoreAsync(Solution mutated, Mutation m, GraphStore baseGraph,
        Dictionary<string, HashSet<string>> baseline, SortedSet<string> labels)
    {
        var contracts = m.WirePaths.Select(p => p.Contract).Distinct().ToList();
        var truth = new HashSet<string>(StringComparer.Ordinal);
        var wireNotes = new List<string>();

        if (m.Kind == "event-rename")
        {
            // Routing is by class name: every consumer of the old name is starved unless another service still publishes it.
            var stillPublished = false;
            foreach (var project in DeployableProjects(mutated))
                stillPublished |= PublishedEventNames((await project.GetCompilationAsync())!).Contains(m.TypeName);
            if (!stillPublished)
                foreach (var project in DeployableProjects(mutated).Where(p => p.Name != m.Project))
                    if (HandledEvents((await project.GetCompilationAsync())!).Any(t => t.Name == m.TypeName))
                    {
                        truth.Add(project.AssemblyName);
                        wireNotes.Add($"{project.AssemblyName}: no longer routed");
                    }
        }
        else
        {
            foreach (var contract in contracts)
            {
                var (publisherProject, publisherType) = await FindPublisherAsync(mutated, contract);
                if (publisherProject is null || publisherType is null) continue;
                var consumers = new List<(Project, INamedTypeSymbol)>();
                foreach (var project in DeployableProjects(mutated).Where(p => p.AssemblyName != publisherProject.AssemblyName))
                {
                    var comp = (await project.GetCompilationAsync())!;
                    if (HandledEvents(comp).FirstOrDefault(t => t.Name == contract) is { } copy) consumers.Add((project, copy));
                }
                var wire = await _wire.RoundTripAsync(mutated, publisherType, publisherProject, consumers);
                foreach (var (project, copy) in consumers)
                {
                    var r = wire[project.AssemblyName];
                    var used = await UsedPathsAsync(mutated, project, copy);
                    foreach (var path in used) labels.Add($"{project.AssemblyName}\t{contract}\t{path}");
                    if (r.Throws) { truth.Add(project.AssemblyName); wireNotes.Add($"{project.AssemblyName} {contract}: throws ({r.Error})"); continue; }
                    var hit = r.LostPaths.Where(used.Contains).ToList();
                    if (hit.Count > 0) { truth.Add(project.AssemblyName); wireNotes.Add($"{project.AssemblyName} {contract}: lost {string.Join(",", hit)}"); }
                    else if (r.LostPaths.Count > 0) wireNotes.Add($"{project.AssemblyName} {contract}: lost unused {string.Join(",", r.LostPaths)}");
                }
            }
        }

        // Graph-based approaches: index the mutant once, run each variant against it.
        var batch = await new CodeGraphExtractor(loaded with { Solution = mutated }).ExtractAsync();
        batch.PruneDanglingEdges();
        store.Replace(batch);
        var predictions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Compiler"] = [],
            ["grep"] = GrepBaseline(m),
            ["Topology"] = TopologyBaseline(baseGraph, m),
        };
        foreach (var approach in Approaches)
        {
            var introduced = approach.Run(store).Where(f => !baseline[approach.Name].Contains(f.Id)).ToList();
            predictions[approach.Name] = introduced.Select(f => f.ConsumerService).Where(_deployables.Contains).Distinct().Order().ToList();
            if (approach.Name == "Ripple")
                wireNotes.AddRange(introduced.Select(f => $"ripple: {f.Kind} {f.Contract} -> {f.ConsumerService} {f.Path} {f.Expected}/{f.Actual}").Distinct());
        }

        return new EvalCase(m.Label, m.Kind, truth.Order().ToList(), predictions, wireNotes);
    }

    /// <summary>Usage oracle: JSON paths of the consumer's copy that its code reads (SymbolFinder), or all paths if it forwards the whole event.</summary>
    static async Task<HashSet<string>> UsedPathsAsync(Solution solution, Project project, INamedTypeSymbol copy)
    {
        var compilation = (await project.GetCompilationAsync())!;
        var all = Walk(copy, "").ToList();
        if (await ForwardsWholeAsync(project, compilation, copy)) return all.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var documents = project.Documents.ToImmutableHashSet();
        foreach (var (prop, path) in all)
        {
            foreach (var location in (await SymbolFinder.FindReferencesAsync(prop, solution, documents)).SelectMany(r => r.Locations))
            {
                var node = (await location.Document.GetSyntaxRootAsync())!.FindNode(location.Location.SourceSpan);
                var model = (await location.Document.GetSemanticModelAsync())!;
                if (IsWrite(node) || IsInLoggingCall(node)) continue;
                if (node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } decl
                    && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(decl), prop.ContainingType)) continue;
                used.Add(path);
                break;
            }
        }
        return used;
    }

    // ---------------------------------------------------------------- approaches (graph variants are ablations of one pipeline)

    public sealed record Approach(string Name, Func<GraphStore, IReadOnlyList<Finding>> Run);

    static readonly Approach[] Approaches =
    [
        new("Schema diff (no READS)", Ablations.SchemaDiff),
        new("Ripple, handler body only", g => ContractChecker.Run(g, maxHops: 0)),
        new("Ripple", g => ContractChecker.Run(g)),
    ];

    /// <summary>grep, made fair: a consumer file that mentions both the event and the changed member.</summary>
    IReadOnlyList<string> GrepBaseline(Mutation m)
    {
        var member = m.Member.Length > 0 ? m.Member : m.TypeName;
        var memberRe = new Regex($@"\b{Regex.Escape(member)}\b");
        var contracts = m.WirePaths.Select(p => p.Contract).Distinct().ToList();
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, project) in loaded.Services)
        {
            if (project.Name == m.Project && !m.ConsumerSide) continue;
            var dir = Path.GetDirectoryName(project.FilePath)!;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/")))
            {
                var text = File.ReadAllText(file);
                if (memberRe.IsMatch(text) && contracts.Any(text.Contains)) { hits.Add(project.AssemblyName); break; }
            }
        }
        return hits.ToList();
    }

    /// <summary>Pub/sub topology only (what a service map knows): every other service that handles a changed contract.</summary>
    IReadOnlyList<string> TopologyBaseline(GraphStore baseGraph, Mutation m)
    {
        if (m.ConsumerSide) return [];
        var publisher = loaded.Services.First(x => x.Project.Name == m.Project).Project.AssemblyName;
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var contract in m.WirePaths.Select(p => p.Contract).Distinct())
            foreach (var row in baseGraph.Query("""
                         MATCH (h:Method)-[:HANDLES]->(:EventShape)-[:SHAPE_OF]->(:EventContract {name: $contract})
                         RETURN DISTINCT h.service AS service
                         """, new Dictionary<string, object?> { ["contract"] = contract }))
                if (row["service"] is string s && s != publisher && _deployables.Contains(s))
                    hits.Add(s);
        return hits.ToList();
    }

    // ---------------------------------------------------------------- Roslyn helpers (no graph)

    IEnumerable<Project> DeployableProjects(Solution solution) =>
        solution.Projects.Where(p => _deployables.Contains(p.AssemblyName)).GroupBy(p => p.AssemblyName).Select(g => g.First());

    async Task<(Project?, INamedTypeSymbol?)> FindPublisherAsync(Solution solution, string contract)
    {
        foreach (var project in DeployableProjects(solution))
        {
            var compilation = (await project.GetCompilationAsync())!;
            if (!PublishedEventNames(compilation).Contains(contract)) continue;
            var type = AllTypes(compilation.Assembly.GlobalNamespace).FirstOrDefault(t => t.Name == contract && CodeGraphExtractor.IsIntegrationEvent(t));
            if (type is not null) return (project, type);
        }
        return (null, null);
    }

    /// <summary>Event types this compilation instantiates (`new X(...)`), i.e. publishes.</summary>
    static HashSet<string> PublishedEventNames(Compilation compilation)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // Source-generated trees (e.g. a JsonSerializerContext) instantiate events too; they don't publish anything.
        foreach (var tree in compilation.SyntaxTrees.Where(t => File.Exists(t.FilePath) && !t.FilePath.Contains("/obj/")))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                if (model.GetTypeInfo(creation).Type is INamedTypeSymbol t && CodeGraphExtractor.IsIntegrationEvent(t)
                    && SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly))
                    names.Add(t.Name);
        }
        return names;
    }

    static IEnumerable<INamedTypeSymbol> HandledEvents(Compilation compilation) =>
        AllTypes(compilation.Assembly.GlobalNamespace)
            .Where(t => !t.IsAbstract)
            .SelectMany(t => t.AllInterfaces)
            .Where(i => i.Name == "IIntegrationEventHandler" && i.TypeArguments.Length == 1)
            .Select(i => (INamedTypeSymbol)i.TypeArguments[0])
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    /// <summary>The consumer passes its copy of the event to an object-typed parameter (e.g. a webhook payload).</summary>
    static async Task<bool> ForwardsWholeAsync(Project project, Compilation compilation, INamedTypeSymbol contract)
    {
        foreach (var document in project.Documents)
        {
            var root = (await document.GetSyntaxRootAsync())!;
            var model = compilation.GetSemanticModel(root.SyntaxTree);
            foreach (var arg in root.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(arg.Expression).Type, contract) || IsInLoggingCall(arg)) continue;
                if (model.GetSymbolInfo(arg.Parent!.Parent!).Symbol is IMethodSymbol method)
                {
                    var index = ((BaseArgumentListSyntax)arg.Parent).Arguments.IndexOf(arg);
                    if (index < method.Parameters.Length && method.Parameters[index].Type.SpecialType == SpecialType.System_Object) return true;
                }
            }
        }
        return false;
    }

    static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
            foreach (var x in WithNested(t)) yield return x;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in AllTypes(child)) yield return t;
    }

    static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol t)
    {
        yield return t;
        foreach (var n in t.GetTypeMembers())
            foreach (var x in WithNested(n)) yield return x;
    }

    /// <summary>(property, JSON path) pairs, honouring [JsonPropertyName] and [JsonIgnore].</summary>
    static IEnumerable<(IPropertySymbol Prop, string Path)> Walk(ITypeSymbol type, string prefix, int depth = 0)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.IsStatic || p.IsIndexer || p.GetMethod is null || p.Name == "EqualityContract" || !seen.Add(p.Name)) continue;
                if (p.DeclaredAccessibility != Accessibility.Public && !p.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIncludeAttribute")) continue;
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;
                var wire = p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute")?.ConstructorArguments[0].Value as string ?? p.Name;
                var path = prefix + wire;
                yield return (p, path);
                var element = ElementType(p.Type);
                var value = element ?? p.Type;
                if (depth < 4 && value.TypeKind is TypeKind.Class or TypeKind.Struct && value.SpecialType == SpecialType.None && value.Locations.Any(l => l.IsInSource))
                    foreach (var inner in Walk(value, path + (element is null ? "." : "[]."), depth + 1))
                        yield return inner;
            }
        }
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

/// <summary>Graph ablations that are not part of the product.</summary>
static class Ablations
{
    /// <summary>
    /// Schema-registry style: a consumer is affected when its copy declares a path the publisher no longer sends, or a
    /// path with a drifted type, or its contract is no longer published — whether or not its code uses the field.
    /// </summary>
    public static IReadOnlyList<Finding> SchemaDiff(GraphStore g)
    {
        const string missing = """
            MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
            WHERE ps <> cs
            WITH pub, ps, c, cs, h
            MATCH (cs)-[:HAS_PROPERTY]->(cp:Property)
            WHERE NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
            RETURN DISTINCT 'schema-missing' AS kind, c.name AS contract, pub.service AS publisherService, null AS publisher,
                   h.service AS consumerService, null AS handler, null AS reader, cp.path AS path, null AS expected, null AS actual
            """;
        var findings = g.Query(missing).Select(r => Finding.FromRow(r, "schema diff", "n/a")).ToList();
        var p = new Dictionary<string, object?> { ["contract"] = null, ["compatible"] = Queries.RippleQueries.Compatible.Cast<object>().ToList() };
        findings.AddRange(g.Query(Queries.RippleQueries.TypeDrift, p).Select(r => Finding.FromRow(r, "schema diff", "n/a")));
        findings.AddRange(g.Query(Queries.RippleQueries.StarvedSubscriptions, p).Select(r => Finding.FromRow(r, "schema diff", "n/a")));
        return findings;
    }
}

public sealed record EvalCase(string Mutation, string Kind, IReadOnlyList<string> Truth,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Predictions, IReadOnlyList<string> WireNotes);

public sealed record EvalReport(IReadOnlyList<EvalCase> Cases, IReadOnlyList<string> UsageLabels)
{
    public static readonly string[] ApproachOrder = ["Compiler", "grep", "Topology", "Schema diff (no READS)", "Ripple, handler body only", "Ripple"];

    public sealed record Score(string Approach, string Kind, int TruePositives, int FalsePositives, int FalseNegatives, int Exact, int Cases)
    {
        public double? Precision => TruePositives + FalsePositives == 0 ? null : (double)TruePositives / (TruePositives + FalsePositives);
        public double Recall => TruePositives + FalseNegatives == 0 ? 1 : (double)TruePositives / (TruePositives + FalseNegatives);
    }

    public IReadOnlyList<Score> Scores(string? kind = null) => ApproachOrder.Select(a =>
    {
        int tp = 0, fp = 0, fn = 0, exact = 0, n = 0;
        foreach (var c in Cases.Where(c => kind is null || c.Kind == kind))
        {
            var predicted = c.Predictions[a].ToHashSet();
            var truth = c.Truth.ToHashSet();
            tp += predicted.Count(truth.Contains);
            fp += predicted.Count(x => !truth.Contains(x));
            fn += truth.Count(x => !predicted.Contains(x));
            n++;
            if (predicted.SetEquals(truth)) exact++;
        }
        return new Score(a, kind ?? "all", tp, fp, fn, exact, n);
    }).ToList();
}

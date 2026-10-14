using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ripple.Core.Graph;

namespace Ripple.Core.Extraction;

/// <summary>
/// Walks the Roslyn semantic model of every project in a loaded solution and emits the Ripple code graph.
///
/// Node keys:
///   Service        assembly name                     (deployables have kind 'service', referenced projects 'library')
///   File           repo-relative path
///   Type, Method   "{assembly}|{fully qualified name}"
///   EventContract  short event type name             (the RabbitMQ routing key: @event.GetType().Name)
///   EventShape     "{assembly}|{event type fqn}"     (one service's own declaration of the contract)
///   Property       "{shape key}|{json path}"         (e.g. OrderStockItems[].Units)
///
/// Every edge carries source = 'syntactic' (resolved directly from the semantic model) or 'inferred'.
/// </summary>
public sealed class CodeGraphExtractor
{
    const int MaxShapeDepth = 4;

    static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.CSharpErrorMessageFormat
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType)
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters);

    readonly LoadedSolution _loaded;
    readonly HashSet<string> _deployables;
    readonly Action<string> _log;
    readonly GraphBatch _batch = new();
    readonly HashSet<string> _ourAssemblies = new(StringComparer.Ordinal);

    // Per event shape: property symbol -> json paths it appears at (a nested type can appear at several paths).
    readonly Dictionary<string, Dictionary<IPropertySymbol, List<string>>> _shapePaths = new();

    // Handlers are wired after every shape is known (a handler's file can come before its event's file).
    readonly List<(INamedTypeSymbol Handler, INamedTypeSymbol Event, Compilation Compilation)> _handlers = new();

    public CodeGraphExtractor(LoadedSolution loaded, Action<string>? log = null)
    {
        _loaded = loaded;
        _deployables = loaded.Services.Select(s => s.Project.AssemblyName).ToHashSet(StringComparer.Ordinal);
        _log = log ?? (_ => { });
    }

    public static bool IsTestProject(Project p) =>
        p.Name.Contains("Test", StringComparison.OrdinalIgnoreCase) || p.Name.EndsWith("AppHost", StringComparison.Ordinal);

    public async Task<GraphBatch> ExtractAsync(Func<Document, bool>? documentFilter = null)
    {
        var projects = ProjectsToIndex().ToList();
        foreach (var p in projects) _ourAssemblies.Add(p.AssemblyName);

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException($"No compilation for {project.Name}");
            var assembly = project.AssemblyName;
            _batch.Node("Service", assembly, ("name", assembly), ("kind", _deployables.Contains(assembly) ? "service" : "library"));

            foreach (var document in project.Documents)
            {
                if (document.FilePath is null || IsGenerated(document.FilePath)) continue;
                if (documentFilter is not null && !documentFilter(document)) continue;
                var tree = await document.GetSyntaxTreeAsync();
                var model = compilation.GetSemanticModel(tree!);
                ExtractDocument(assembly, document, (await tree!.GetRootAsync()), model);
            }
            _log($"extracted {project.Name}");
        }

        foreach (var (handler, evt, compilation) in _handlers) AddHandler(handler, evt, compilation);
        return _batch;
    }

    IEnumerable<Project> ProjectsToIndex()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _loaded.Solution.Projects)
        {
            if (p.Language != LanguageNames.CSharp || IsTestProject(p)) continue;
            if (p.FilePath is null || !p.FilePath.StartsWith(_loaded.RepoRoot, StringComparison.Ordinal)) continue;
            if (seen.Add(p.AssemblyName)) yield return p;
        }
    }

    static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || path.EndsWith(".g.cs", StringComparison.Ordinal);

    string RelPath(string path) => Path.GetRelativePath(_loaded.RepoRoot, path).Replace('\\', '/');

    // ---------------------------------------------------------------- per document

    void ExtractDocument(string assembly, Document document, SyntaxNode root, SemanticModel model)
    {
        var file = RelPath(document.FilePath!);
        _batch.Node("File", file, ("path", file));
        _batch.Edge("CONTAINS", "Service", assembly, "File", file);

        foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;
            var typeKey = TypeKey(type);
            _batch.Node("Type", typeKey, ("fqn", Fqn(type)), ("kind", TypeKind(type)), ("service", assembly), ("file", file));
            _batch.Edge("DECLARES", "File", file, "Type", typeKey);

            foreach (var iface in type.Interfaces)
                if (IsOurs(iface)) _batch.Edge("IMPLEMENTS", "Type", typeKey, "Type", TypeKey(iface.OriginalDefinition), ("source", "syntactic"));
            if (type.BaseType is { } baseType && IsOurs(baseType))
                _batch.Edge("IMPLEMENTS", "Type", typeKey, "Type", TypeKey(baseType.OriginalDefinition), ("source", "syntactic"));

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!IsIndexableMethod(method)) continue;
                var location = method.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree == root.SyntaxTree);
                if (location is null) continue;
                var span = MemberSpan(method, root.SyntaxTree) ?? location.GetLineSpan();
                var methodKey = MethodKey(method);
                _batch.Node("Method", methodKey,
                    ("fqn", Fqn(method)), ("name", method.Name), ("signature", method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                    ("service", assembly), ("file", file),
                    ("startLine", span.StartLinePosition.Line + 1), ("endLine", span.EndLinePosition.Line + 1),
                    ("isPublic", method.DeclaredAccessibility == Accessibility.Public));
                _batch.Edge("HAS_METHOD", "Type", typeKey, "Method", methodKey);
            }

            if (IsIntegrationEvent(type)) AddEventShape(assembly, file, type);
            if (HandledEvent(type) is { } handled) _handlers.Add((type, handled, model.Compilation));
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    OnInvocation(assembly, invocation, model);
                    break;
                case BaseObjectCreationExpressionSyntax creation:
                    OnCreation(assembly, creation, model);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- calls, publishes, subscriptions

    void OnInvocation(string assembly, InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) return;
        callee = callee.ReducedFrom ?? callee;

        if (callee.Name == "AddSubscription" && callee.TypeArguments.Length == 2 && invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic })
        {
            var typeArgs = generic.TypeArgumentList.Arguments.Select(a => model.GetTypeInfo(a).Type).ToList();
            if (typeArgs is [INamedTypeSymbol evt, INamedTypeSymbol handler] && IsIntegrationEvent(evt))
            {
                AddContract(evt.Name);
                _batch.Edge("SUBSCRIBES", "Service", assembly, "EventContract", evt.Name, ("handler", Fqn(handler)), ("source", "syntactic"));
            }
        }

        if (EnclosingMethod(invocation, model) is not { } caller) return;
        var target = callee.OriginalDefinition;
        if (!IsOurs(target) || !IsIndexableMethod(target)) return;
        _batch.Edge("CALLS", "Method", MethodKey(caller), "Method", MethodKey(target), ("source", "syntactic"));
    }

    void OnCreation(string assembly, BaseObjectCreationExpressionSyntax creation, SemanticModel model)
    {
        if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol created) return;
        if (EnclosingMethod(creation, model) is not { } caller) return;

        if (IsIntegrationEvent(created))
        {
            _batch.Edge("PUBLISHES", "Method", MethodKey(caller), "EventShape", TypeKey(created.OriginalDefinition),
                ("source", "syntactic"), ("line", creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }

        // A constructor call is a call edge too (handlers, commands and services are often created with `new`).
        if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol ctor && IsOurs(ctor) && IsIndexableMethod(ctor.OriginalDefinition))
            _batch.Edge("CALLS", "Method", MethodKey(caller), "Method", MethodKey(ctor.OriginalDefinition), ("source", "syntactic"));
    }

    // ---------------------------------------------------------------- contract layer

    void AddContract(string name) => _batch.Node("EventContract", name, ("name", name));

    void AddEventShape(string assembly, string file, INamedTypeSymbol type)
    {
        var shapeKey = TypeKey(type);
        AddContract(type.Name);
        _batch.Node("EventShape", shapeKey, ("fqn", Fqn(type)), ("name", type.Name), ("service", assembly), ("file", file));
        _batch.Edge("SHAPE_OF", "EventShape", shapeKey, "EventContract", type.Name);
        _batch.Edge("DECLARES", "File", file, "EventShape", shapeKey);
        _batch.Edge("DEFINES_SHAPE", "Type", shapeKey, "EventShape", shapeKey);

        var paths = new Dictionary<IPropertySymbol, List<string>>(SymbolEqualityComparer.Default);
        FlattenShape(shapeKey, type, prefix: "", depth: 0, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), paths);
        _shapePaths[shapeKey] = paths;
    }

    void FlattenShape(string shapeKey, ITypeSymbol type, string prefix, int depth,
        HashSet<ITypeSymbol> visiting, Dictionary<IPropertySymbol, List<string>> paths)
    {
        if (!visiting.Add(type)) return;
        foreach (var prop in SerializedProperties(type))
        {
            var path = prefix + prop.Name;
            var element = ElementType(prop.Type);
            var valueType = element ?? prop.Type;
            var propKey = $"{shapeKey}|{path}";
            _batch.Node("Property", propKey,
                ("name", prop.Name), ("path", path), ("type", prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                ("jsonKind", JsonKind(prop.Type)), ("shape", shapeKey), ("depth", depth));
            _batch.Edge("HAS_PROPERTY", "EventShape", shapeKey, "Property", propKey);

            var definition = (IPropertySymbol)prop.OriginalDefinition;
            if (!paths.TryGetValue(definition, out var list)) paths[definition] = list = [];
            list.Add(path);

            if (depth < MaxShapeDepth && IsComposite(valueType))
                FlattenShape(shapeKey, valueType, path + (element is null ? "." : "[]."), depth + 1, visiting, paths);
        }
        visiting.Remove(type);
    }

    void AddHandler(INamedTypeSymbol handlerType, INamedTypeSymbol eventType, Compilation compilation)
    {
        var iface = handlerType.AllInterfaces.First(i => IsHandlerInterface(i) && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], eventType));
        if (handlerType.FindImplementationForInterfaceMember(iface.GetMembers("Handle").First()) is not IMethodSymbol handle) return;

        var shapeKey = TypeKey(eventType.OriginalDefinition);
        var handleKey = MethodKey(handle);
        _batch.Edge("HANDLES", "Method", handleKey, "EventShape", shapeKey, ("source", "syntactic"));

        var eventParam = handle.Parameters.FirstOrDefault();
        foreach (var syntaxRef in handle.DeclaringSyntaxReferences)
        {
            var body = syntaxRef.GetSyntax();
            var model = compilation.GetSemanticModel(body.SyntaxTree);
            CollectContractUse(handleKey, shapeKey, eventParam, body, model);
        }
    }

    /// <summary>
    /// READS: the handler touches a property of its event (or of a type nested in it), including inside lambdas and patterns.
    /// FORWARDS: the handler passes the whole event object on (e.g. serialises it into a webhook payload), so every field is consumed.
    /// </summary>
    void CollectContractUse(string handleKey, string shapeKey, IParameterSymbol? eventParam, SyntaxNode body, SemanticModel model)
    {
        _shapePaths.TryGetValue(shapeKey, out var paths);
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(id).Symbol;
            if (symbol is IPropertySymbol prop && paths is not null && paths.TryGetValue((IPropertySymbol)prop.OriginalDefinition, out var hit))
            {
                foreach (var path in hit)
                    _batch.Edge("READS", "Method", handleKey, "Property", $"{shapeKey}|{path}",
                        ("source", "syntactic"), ("line", id.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
            else if (eventParam is not null && SymbolEqualityComparer.Default.Equals(symbol, eventParam)
                     && id.Parent is ArgumentSyntax arg && !IsLoggingArgument(arg, model))
            {
                _batch.Edge("FORWARDS", "Method", handleKey, "EventShape", shapeKey,
                    ("source", "syntactic"), ("line", id.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }
    }

    static bool IsLoggingArgument(ArgumentSyntax arg, SemanticModel model)
    {
        var call = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (call is null || model.GetSymbolInfo(call).Symbol is not IMethodSymbol m) return false;
        var owner = (m.ReducedFrom ?? m).ContainingType;
        return owner.Name is "LoggerExtensions" or "ILogger" || owner.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- symbol helpers

    bool IsOurs(ISymbol symbol) => symbol.ContainingAssembly is { } a && _ourAssemblies.Contains(a.Name);

    static bool IsIndexableMethod(IMethodSymbol m) =>
        !m.IsImplicitlyDeclared && m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.ExplicitInterfaceImplementation
            or MethodKind.StaticConstructor or MethodKind.PropertyGet or MethodKind.PropertySet;

    IMethodSymbol? EnclosingMethod(SyntaxNode node, SemanticModel model)
    {
        var symbol = model.GetEnclosingSymbol(node.SpanStart);
        // Code inside lambdas and local functions counts toward the member that contains it.
        while (symbol is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction or MethodKind.AnonymousFunction })
            symbol = symbol.ContainingSymbol;
        return symbol is IMethodSymbol m && IsIndexableMethod(m) && IsOurs(m) ? m : null;
    }

    static FileLinePositionSpan? MemberSpan(IMethodSymbol method, SyntaxTree tree) =>
        method.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == tree)?.GetSyntax().GetLocation().GetLineSpan();

    public static string Fqn(ISymbol symbol) => symbol.ToDisplayString(FqnFormat);
    public static string TypeKey(INamedTypeSymbol type) => $"{type.ContainingAssembly.Name}|{Fqn(type)}";
    public static string MethodKey(IMethodSymbol method) => $"{method.ContainingAssembly.Name}|{Fqn(method)}";

    static string TypeKind(INamedTypeSymbol t) => t switch
    {
        { IsRecord: true } => "record",
        { TypeKind: Microsoft.CodeAnalysis.TypeKind.Interface } => "interface",
        { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } => "enum",
        { TypeKind: Microsoft.CodeAnalysis.TypeKind.Struct } => "struct",
        _ => "class",
    };

    /// <summary>eShop integration events derive from eShop.EventBus.Events.IntegrationEvent.</summary>
    public static bool IsIntegrationEvent(ITypeSymbol type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name == "IntegrationEvent" && t.ContainingNamespace.ToDisplayString().EndsWith("EventBus.Events", StringComparison.Ordinal))
                return true;
        return false;
    }

    static bool IsHandlerInterface(INamedTypeSymbol i) => i.Name == "IIntegrationEventHandler" && i.TypeArguments.Length == 1;

    static INamedTypeSymbol? HandledEvent(INamedTypeSymbol type) =>
        type.IsAbstract ? null : type.AllInterfaces.FirstOrDefault(IsHandlerInterface)?.TypeArguments[0] as INamedTypeSymbol;

    /// <summary>Public instance properties with a getter, base types included, minus [JsonIgnore] — what System.Text.Json writes.</summary>
    static IEnumerable<IPropertySymbol> SerializedProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.IsStatic || p.IsIndexer || p.GetMethod is null) continue;
                if (p.Name == "EqualityContract") continue;
                var included = p.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIncludeAttribute");
                if (p.DeclaredAccessibility != Accessibility.Public && !included) continue;
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonIgnoreAttribute")) continue;
                if (seen.Add(p.Name)) yield return p;
            }
        }
    }

    static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is INamedTypeSymbol { IsGenericType: true } named && named.AllInterfaces.Concat([named]).FirstOrDefault(i =>
                i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) is { } enumerable)
            return enumerable.TypeArguments[0];
        return null;
    }

    static bool IsComposite(ITypeSymbol t) =>
        t.TypeKind is Microsoft.CodeAnalysis.TypeKind.Class or Microsoft.CodeAnalysis.TypeKind.Struct
        && t.SpecialType == SpecialType.None
        && t.Locations.Any(l => l.IsInSource);

    static string JsonKind(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) type = nullable.TypeArguments[0];
        if (ElementType(type) is not null) return "array";
        switch (type.SpecialType)
        {
            case SpecialType.System_String or SpecialType.System_Char: return "string";
            case SpecialType.System_Boolean: return "bool";
            case SpecialType.System_DateTime: return "string";
            case >= SpecialType.System_SByte and <= SpecialType.System_Double: return "number";
        }
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
            return type.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonConverterAttribute"
                && a.ConstructorArguments.FirstOrDefault().Value?.ToString()?.Contains("JsonStringEnumConverter") == true) ? "string" : "number";
        if (type.Name is "Guid" or "DateTimeOffset" or "TimeSpan" or "DateOnly") return "string";
        return "object";
    }
}

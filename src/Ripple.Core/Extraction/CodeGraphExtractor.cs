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
///   Property       "{shape key}|{json path}"         (wire names, e.g. OrderStockItems[].Units)
///
/// Every edge carries source = 'syntactic' (resolved directly from the semantic model) or 'inferred'
/// (interface dispatch, MediatR command / domain-event dispatch).
///
/// Two passes: pass 1 declares types, methods and event shapes; pass 2 walks bodies for calls, publishes,
/// subscriptions and contract reads (a read can sit in any file of the consuming service).
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

    // Property symbol (of an event type or a type nested in one) -> every (shape, wire path) it appears at.
    // Keyed by string ("{assembly}|{fqn}"), not by symbol: the same type seen from two projects is two different symbols.
    // Owner = the shape or nested type the path belongs to; an inherited property (e.g. IntegrationEvent.Id) has one entry
    // per owner, and a read is attributed only to the owner its receiver expression has.
    readonly Dictionary<string, List<(string ShapeKey, string Path, string Owner)>> _propertyPaths = new(StringComparer.Ordinal);
    readonly HashSet<string> _shapeTypes = new(StringComparer.Ordinal);

    // Dispatch tables for inferred edges.
    readonly List<(INamedTypeSymbol Handler, INamedTypeSymbol Event)> _handlers = new();
    readonly Dictionary<string, List<IMethodSymbol>> _requestHandlers = new(StringComparer.Ordinal);   // MediatR IRequestHandler<T>, INotificationHandler<T>, by message type key
    readonly List<INamedTypeSymbol> _sourceTypes = new();

    public CodeGraphExtractor(LoadedSolution loaded, Action<string>? log = null)
    {
        _loaded = loaded;
        _deployables = loaded.Services.Select(s => s.Project.AssemblyName).ToHashSet(StringComparer.Ordinal);
        _log = log ?? (_ => { });
    }

    public static bool IsTestProject(Project p) =>
        p.Name.Contains("Test", StringComparison.OrdinalIgnoreCase) || p.Name.EndsWith("AppHost", StringComparison.Ordinal);

    public async Task<GraphBatch> ExtractAsync()
    {
        var projects = ProjectsToIndex().ToList();
        foreach (var p in projects) _ourAssemblies.Add(p.AssemblyName);

        var documents = new List<(string Assembly, string File, SyntaxNode Root, SemanticModel Model)>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException($"No compilation for {project.Name}");
            var assembly = project.AssemblyName;
            _batch.Node("Service", assembly, ("name", assembly), ("kind", _deployables.Contains(assembly) ? "service" : "library"));

            foreach (var document in project.Documents)
            {
                if (document.FilePath is null || IsGenerated(document.FilePath)) continue;
                var tree = (await document.GetSyntaxTreeAsync())!;
                var root = await tree.GetRootAsync();
                var model = compilation.GetSemanticModel(tree);
                var file = RelPath(document.FilePath);
                DeclareDocument(assembly, file, root, model);
                documents.Add((assembly, file, root, model));
            }
        }

        foreach (var (handler, evt) in _handlers) AddHandler(handler, evt);
        foreach (var (assembly, _, root, model) in documents) WalkBodies(assembly, root, model);
        AddInterfaceDispatch();

        _log($"extracted {projects.Count} projects, {documents.Count} documents");
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

    // ---------------------------------------------------------------- pass 1: declarations

    void DeclareDocument(string assembly, string file, SyntaxNode root, SemanticModel model)
    {
        _batch.Node("File", file, ("path", file));
        _batch.Edge("CONTAINS", "Service", assembly, "File", file);

        foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;
            _sourceTypes.Add(type);
            var typeKey = TypeKey(type);
            _batch.Node("Type", typeKey, ("fqn", Fqn(type)), ("name", type.Name), ("kind", TypeKind(type)), ("service", assembly), ("file", file));
            _batch.Edge("DECLARES", "File", file, "Type", typeKey);

            foreach (var iface in type.Interfaces)
                if (IsOurs(iface)) _batch.Edge("IMPLEMENTS", "Type", typeKey, "Type", TypeKey(iface.OriginalDefinition), ("source", "syntactic"));
            if (type.BaseType is { } baseType && IsOurs(baseType))
                _batch.Edge("IMPLEMENTS", "Type", typeKey, "Type", TypeKey(baseType.OriginalDefinition), ("source", "syntactic"));

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!IsIndexableMethod(method)) continue;
                var syntax = method.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == root.SyntaxTree);
                if (syntax is null) continue;
                var span = syntax.GetSyntax().GetLocation().GetLineSpan();
                var methodKey = MethodKey(method);
                _batch.Node("Method", methodKey,
                    ("fqn", Fqn(method)), ("name", method.Name), ("signature", method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                    ("type", type.Name), ("service", assembly), ("file", file),
                    ("startLine", span.StartLinePosition.Line + 1), ("endLine", span.EndLinePosition.Line + 1),
                    ("isPublic", method.DeclaredAccessibility == Accessibility.Public));
                _batch.Edge("HAS_METHOD", "Type", typeKey, "Method", methodKey);
            }

            if (IsIntegrationEvent(type)) AddEventShape(assembly, file, type);
            if (!type.IsAbstract)
            {
                if (HandledEvent(type) is { } handled) _handlers.Add((type, handled));
                RegisterMediatRHandler(type);
            }
        }
    }

    /// <summary>MediatR: IRequestHandler&lt;TRequest(,TResponse)&gt; and INotificationHandler&lt;TNotification&gt; implementations.</summary>
    void RegisterMediatRHandler(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name is not ("IRequestHandler" or "INotificationHandler") || !iface.ContainingNamespace.ToDisplayString().StartsWith("MediatR", StringComparison.Ordinal))
                continue;
            if (iface.GetMembers("Handle").FirstOrDefault() is not IMethodSymbol ifaceHandle) continue;
            if (type.FindImplementationForInterfaceMember(ifaceHandle) is not IMethodSymbol handle) continue;
            if (iface.TypeArguments[0].OriginalDefinition is not INamedTypeSymbol messageType) continue;
            var message = TypeKey(messageType);
            if (!_requestHandlers.TryGetValue(message, out var list)) _requestHandlers[message] = list = [];
            list.Add(handle);
        }
    }

    // ---------------------------------------------------------------- pass 2: bodies

    void WalkBodies(string assembly, SyntaxNode root, SemanticModel model)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    OnInvocation(assembly, invocation, model);
                    break;
                case BaseObjectCreationExpressionSyntax creation:
                    OnCreation(creation, model);
                    break;
                case IdentifierNameSyntax id:
                    OnIdentifier(assembly, id, model);
                    break;
            }
        }
    }

    void OnInvocation(string assembly, InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) return;
        callee = callee.ReducedFrom ?? callee;

        if (callee.Name == "AddSubscription" && invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic })
        {
            var typeArgs = generic.TypeArgumentList.Arguments.Select(a => model.GetTypeInfo(a).Type).ToList();
            if (typeArgs is [INamedTypeSymbol evt, INamedTypeSymbol handler] && IsIntegrationEvent(evt))
            {
                AddContract(evt.Name);
                _batch.Edge("SUBSCRIBES", "Service", assembly, "EventContract", evt.Name, ("handler", Fqn(handler)), ("source", "syntactic"));
            }
        }

        if (EnclosingMethod(invocation, model) is not { } caller) return;
        var callerKey = MethodKey(caller);

        // MediatR: mediator.Send(new XCommand(..)) / Publish(notification) -> the handler's Handle (inferred).
        if (callee.Name is "Send" or "Publish" && callee.ContainingNamespace.ToDisplayString().StartsWith("MediatR", StringComparison.Ordinal)
            && invocation.ArgumentList.Arguments.FirstOrDefault() is { } arg
            && model.GetTypeInfo(arg.Expression).Type is { } messageType)
        {
            foreach (var target in HandlersFor(messageType))
                _batch.Edge("CALLS", "Method", callerKey, "Method", MethodKey(target), ("source", "inferred"), ("via", "mediatr"));
        }

        var definition = callee.OriginalDefinition;
        if (!IsOurs(definition) || !IsIndexableMethod(definition)) return;
        _batch.Edge("CALLS", "Method", callerKey, "Method", MethodKey(definition), ("source", "syntactic"));
    }

    void OnCreation(BaseObjectCreationExpressionSyntax creation, SemanticModel model)
    {
        if (model.GetTypeInfo(creation).Type is not INamedTypeSymbol created) return;
        if (EnclosingMethod(creation, model) is not { } caller) return;
        var callerKey = MethodKey(caller);

        if (IsIntegrationEvent(created))
            _batch.Edge("PUBLISHES", "Method", callerKey, "EventShape", TypeKey(created.OriginalDefinition),
                ("source", "syntactic"), ("line", Line(creation)));

        // Domain events: `AddDomainEvent(new XDomainEvent(..))` is dispatched later by MediatR to INotificationHandler<X>.
        if (created.AllInterfaces.Any(i => i.Name == "INotification" && i.ContainingNamespace.ToDisplayString().StartsWith("MediatR", StringComparison.Ordinal))
            && creation.Parent is ArgumentSyntax)
        {
            foreach (var target in HandlersFor(created))
                _batch.Edge("CALLS", "Method", callerKey, "Method", MethodKey(target), ("source", "inferred"), ("via", "domain-event"));
        }

        if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol ctor && IsOurs(ctor) && IsIndexableMethod(ctor.OriginalDefinition))
            _batch.Edge("CALLS", "Method", callerKey, "Method", MethodKey(ctor.OriginalDefinition), ("source", "syntactic"));
    }

    /// <summary>
    /// READS: any method that reads a property of an event shape (or of a type nested in one), outside the shape's own
    /// declaration. FORWARDS: an event object is handed to an untyped (object / generic) parameter, i.e. serialised or
    /// passed on whole — every field is then consumed.
    /// </summary>
    void OnIdentifier(string assembly, IdentifierNameSyntax id, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is IPropertySymbol prop && _propertyPaths.TryGetValue(PropertyKey(prop), out var paths))
        {
            if (IsWrite(id) || IsInsideType(id, model, prop.ContainingType)) return;
            if (EnclosingMethod(id, model) is not { } reader) return;
            var context = IsLoggingArgument(id, model) ? "log" : "code";
            var receiver = ReceiverType(id, model) is INamedTypeSymbol r ? TypeKey(r.OriginalDefinition) : null;
            var declaring = TypeKey(prop.ContainingType.OriginalDefinition);
            foreach (var (shapeKey, path, owner) in paths)
            {
                // Known receiver: only that owner's path. Unknown receiver (e.g. a property pattern): only non-inherited paths.
                if (receiver is not null ? owner != receiver : owner != declaring) continue;
                _batch.Edge("READS", "Method", MethodKey(reader), "Property", $"{shapeKey}|{path}",
                    ("source", "syntactic"), ("line", Line(id)), ("context", context));
            }
            return;
        }

        if (id.Parent is ArgumentSyntax argument && model.GetTypeInfo(id).Type is INamedTypeSymbol argType
            && _shapeTypes.Contains(TypeKey(argType.OriginalDefinition)) && !IsLoggingArgument(id, model)
            && ParameterFor(argument, model) is { } parameter && parameter.Type is { SpecialType: SpecialType.System_Object } or ITypeParameterSymbol
            && EnclosingMethod(id, model) is { } forwarder)
        {
            _batch.Edge("FORWARDS", "Method", MethodKey(forwarder), "EventShape", TypeKey(argType.OriginalDefinition),
                ("source", "syntactic"), ("line", Line(id)), ("to", Fqn(parameter.ContainingSymbol)));
        }
    }

    /// <summary>Interface dispatch: a call to an interface method may run any implementation in the solution (inferred).</summary>
    void AddInterfaceDispatch()
    {
        var interfaceCalls = _batch.Edges.Keys
            .Where(e => e.Type == "CALLS")
            .Select(e => e.ToKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var type in _sourceTypes)
        {
            if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface || type.IsAbstract) continue;
            foreach (var iface in type.AllInterfaces.Where(IsOurs))
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var ifaceKey = MethodKey(member.OriginalDefinition);
                    if (!interfaceCalls.Contains(ifaceKey)) continue;
                    if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol impl && IsIndexableMethod(impl.OriginalDefinition))
                        _batch.Edge("CALLS", "Method", ifaceKey, "Method", MethodKey(impl.OriginalDefinition), ("source", "inferred"), ("via", "interface"));
                }
            }
        }
    }

    IEnumerable<IMethodSymbol> HandlersFor(ITypeSymbol message)
    {
        for (var t = message as INamedTypeSymbol; t is not null; t = t.BaseType)
            if (_requestHandlers.TryGetValue(TypeKey(t.OriginalDefinition), out var list))
                foreach (var h in list) yield return h;
    }

    // ---------------------------------------------------------------- contract layer

    void AddContract(string name) => _batch.Node("EventContract", name, ("name", name));

    void AddEventShape(string assembly, string file, INamedTypeSymbol type)
    {
        var shapeKey = TypeKey(type);
        _shapeTypes.Add(shapeKey);
        AddContract(type.Name);
        _batch.Node("EventShape", shapeKey, ("fqn", Fqn(type)), ("name", type.Name), ("service", assembly), ("file", file),
            ("line", Line(type.DeclaringSyntaxReferences[0].GetSyntax())));
        _batch.Edge("SHAPE_OF", "EventShape", shapeKey, "EventContract", type.Name);
        _batch.Edge("DECLARES", "File", file, "EventShape", shapeKey);
        _batch.Edge("DEFINES_SHAPE", "Type", shapeKey, "EventShape", shapeKey);
        var unbound = new List<string>();
        FlattenShape(shapeKey, type, prefix: "", depth: 0, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), unbound);
        _batch.Node("EventShape", shapeKey, ("unboundParams", unbound.ToArray()));
    }

    void FlattenShape(string shapeKey, ITypeSymbol type, string prefix, int depth, HashSet<ITypeSymbol> visiting, List<string> unbound)
    {
        if (!visiting.Add(type)) return;
        foreach (var param in UnboundConstructorParameters(type)) unbound.Add(prefix + "(ctor " + param + ")");
        foreach (var prop in SerializedProperties(type))
        {
            var path = prefix + WireName(prop);
            var element = ElementType(prop.Type);
            var valueType = element ?? prop.Type;
            var propKey = $"{shapeKey}|{path}";
            var decl = prop.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            _batch.Node("Property", propKey,
                ("name", prop.Name), ("path", path), ("type", prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
                ("wireType", WireType(prop.Type)), ("jsonKind", JsonKind(prop.Type)), ("settable", IsSettable(prop)),
                ("shape", shapeKey), ("depth", depth),
                ("file", decl is null ? null : RelPath(decl.SyntaxTree.FilePath)), ("line", decl is null ? null : Line(decl)));
            _batch.Edge("HAS_PROPERTY", "EventShape", shapeKey, "Property", propKey);

            var definition = PropertyKey(prop);
            if (!_propertyPaths.TryGetValue(definition, out var list)) _propertyPaths[definition] = list = [];
            list.Add((shapeKey, path, type is INamedTypeSymbol owner ? TypeKey((INamedTypeSymbol)owner.OriginalDefinition) : ""));

            if (depth < MaxShapeDepth && IsComposite(valueType))
                FlattenShape(shapeKey, valueType, path + (element is null ? "." : "[]."), depth + 1, visiting, unbound);
        }
        visiting.Remove(type);
    }

    void AddHandler(INamedTypeSymbol handlerType, INamedTypeSymbol eventType)
    {
        var iface = handlerType.AllInterfaces.First(i => IsHandlerInterface(i) && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], eventType));
        if (handlerType.FindImplementationForInterfaceMember(iface.GetMembers("Handle").First()) is not IMethodSymbol handle) return;
        _batch.Edge("HANDLES", "Method", MethodKey(handle), "EventShape", TypeKey(eventType.OriginalDefinition), ("source", "syntactic"));
    }

    // ---------------------------------------------------------------- System.Text.Json model

    /// <summary>Public instance properties with a getter (plus [JsonInclude]), base types included, minus [JsonIgnore].</summary>
    static IEnumerable<IPropertySymbol> SerializedProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.IsStatic || p.IsIndexer || p.GetMethod is null || p.Name == "EqualityContract") continue;
                if (p.DeclaredAccessibility != Accessibility.Public && !HasAttribute(p, "JsonIncludeAttribute")) continue;
                if (HasAttribute(p, "JsonIgnoreAttribute")) continue;
                if (seen.Add(p.Name)) yield return p;
            }
        }
    }

    /// <summary>The JSON name: [JsonPropertyName] if present, else the C# name (eShop's event bus uses no naming policy).</summary>
    static string WireName(IPropertySymbol p) =>
        p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute")?.ConstructorArguments.FirstOrDefault().Value as string
        ?? p.Name;

    /// <summary>
    /// Can deserialisation populate this property? Yes if it has a public (or [JsonInclude]) setter/init, or the type's
    /// constructor has a parameter of the same name (case-insensitive, as System.Text.Json matches them).
    /// </summary>
    static bool IsSettable(IPropertySymbol p)
    {
        if (p.SetMethod is { } set && (set.DeclaredAccessibility == Accessibility.Public || HasAttribute(p, "JsonIncludeAttribute"))) return true;
        var ctors = p.ContainingType.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared || c.Parameters.Length > 0).ToList();
        var chosen = ctors.FirstOrDefault(c => HasAttribute(c, "JsonConstructorAttribute"))
                     ?? ctors.Where(c => c.DeclaredAccessibility == Accessibility.Public).OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        return chosen?.Parameters.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// The constructor System.Text.Json deserialises through ([JsonConstructor], else the public parameterless one, else
    /// a single public parameterised one), and its parameters that match no property by name (case-insensitive).
    /// </summary>
    static IEnumerable<string> UnboundConstructorParameters(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return [];
        var ctors = named.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public && !IsCopyConstructor(c, named)).ToList();
        var chosen = named.InstanceConstructors.FirstOrDefault(c => HasAttribute(c, "JsonConstructorAttribute"))
                     ?? (ctors.Any(c => c.Parameters.Length == 0) ? null : ctors.Count == 1 ? ctors[0] : null);
        if (chosen is null) return [];
        var names = SerializedProperties(type).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return chosen.Parameters.Where(p => !names.Contains(p.Name)).Select(p => p.Name).ToList();
    }

    static bool IsCopyConstructor(IMethodSymbol c, INamedTypeSymbol type) =>
        c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type);

    static bool HasAttribute(ISymbol s, string name) => s.GetAttributes().Any(a => a.AttributeClass?.Name == name);

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

    static bool IsStringEnum(ITypeSymbol type) =>
        type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum && type.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonConverterAttribute"
            && a.ConstructorArguments.FirstOrDefault().Value?.ToString()?.Contains("JsonStringEnumConverter") == true);

    /// <summary>
    /// Normalised wire type used to detect type drift between publisher and consumer: int32, int64, decimal, double,
    /// bool, string, guid, datetime, enum:string, enum:number, array&lt;T&gt;, object; '?' suffix when nullable.
    /// </summary>
    static string WireType(ITypeSymbol type)
    {
        var nullable = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n) { type = n.TypeArguments[0]; nullable = true; }
        var core = ElementType(type) is { } element ? $"array<{WireType(element)}>" : type.SpecialType switch
        {
            SpecialType.System_String or SpecialType.System_Char => "string",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 => "int32",
            SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 => "int64",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Single or SpecialType.System_Double => "double",
            SpecialType.System_DateTime => "datetime",
            _ when type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum => IsStringEnum(type) ? "enum:string" : "enum:number",
            _ when type.Name == "Guid" => "guid",
            _ when type.Name is "DateTimeOffset" or "DateOnly" => "datetime",
            _ => "object",
        };
        return nullable ? core + "?" : core;
    }

    static string JsonKind(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) type = nullable.TypeArguments[0];
        if (ElementType(type) is not null) return "array";
        return WireType(type) switch
        {
            "string" or "guid" or "datetime" or "enum:string" => "string",
            "int32" or "int64" or "decimal" or "double" or "enum:number" => "number",
            "bool" => "bool",
            _ => "object",
        };
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

    /// <summary>Static type of the expression a member is read from (`x.Prop`, `x?.Prop`), or null if not syntactically obvious.</summary>
    static ITypeSymbol? ReceiverType(IdentifierNameSyntax id, SemanticModel model)
    {
        ExpressionSyntax? receiver = id.Parent switch
        {
            MemberAccessExpressionSyntax ma when ma.Name == id => ma.Expression,
            MemberBindingExpressionSyntax mb when mb.Name == id => mb.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null,
        };
        var type = receiver is null ? null : model.GetTypeInfo(receiver).Type;
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;
    }

    static bool IsWrite(IdentifierNameSyntax id)
    {
        SyntaxNode node = id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma : id;
        return node.Parent is AssignmentExpressionSyntax assign && assign.Left == node;
    }

    static bool IsInsideType(SyntaxNode node, SemanticModel model, INamedTypeSymbol type)
    {
        var decl = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        return decl is not null && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(decl), type.OriginalDefinition);
    }

    static IParameterSymbol? ParameterFor(ArgumentSyntax argument, SemanticModel model)
    {
        if (argument.Parent?.Parent is not { } call || model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) return null;
        var args = ((BaseArgumentListSyntax)argument.Parent).Arguments;
        if (argument.NameColon is { } nameColon) return method.Parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.Text);
        var index = args.IndexOf(argument);
        return index < method.Parameters.Length ? method.Parameters[index] : method.Parameters.LastOrDefault(p => p.IsParams);
    }

    static bool IsLoggingArgument(SyntaxNode node, SemanticModel model)
    {
        var call = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (call is null || model.GetSymbolInfo(call).Symbol is not IMethodSymbol m) return false;
        var owner = (m.ReducedFrom ?? m).ContainingType;
        return owner.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal);
    }

    static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static string Fqn(ISymbol symbol) => symbol.ToDisplayString(FqnFormat);
    public static string TypeKey(INamedTypeSymbol type) => $"{type.ContainingAssembly.Name}|{Fqn(type)}";
    static string PropertyKey(IPropertySymbol p) => $"{p.ContainingAssembly.Name}|{Fqn(p.OriginalDefinition)}";
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
        type.AllInterfaces.FirstOrDefault(IsHandlerInterface)?.TypeArguments[0] as INamedTypeSymbol;
}

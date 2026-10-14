using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis;

namespace Ripple.Core.Eval;

/// <summary>
/// Ground truth at the wire, independent of the graph: compile the (mutated) solution in memory, load the publisher's
/// event type and each consumer's own copy into a collectible AssemblyLoadContext, serialise a publisher instance
/// filled with adversarial values using eShop's event-bus JSON options, deserialise it into the consumer copy, and
/// report which consumer JSON paths came back different (lost) or whether deserialisation threw.
/// </summary>
public sealed class WireOracle : IDisposable
{
    // eShop's EventBusSubscriptionInfo.DefaultSerializerOptions: reflection resolver, no naming policy, case-sensitive.
    static readonly JsonSerializerOptions EventBusOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    static readonly string[] SharedFrameworkDirs = Directory.Exists("/usr/lib/dotnet/shared")
        ? Directory.GetDirectories("/usr/lib/dotnet/shared").SelectMany(Directory.GetDirectories).ToArray()
        : [];
    static readonly string RuntimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    readonly Dictionary<(ProjectId, VersionStamp), byte[]> _emitted = new();

    public sealed record Result(bool Throws, string? Error, IReadOnlySet<string> LostPaths, string PublisherJson);

    /// <summary>Round-trips publisherType (in its project) into each consumer copy. Keys of the result: consumer assembly.</summary>
    public async Task<Dictionary<string, Result>> RoundTripAsync(Solution solution, INamedTypeSymbol publisherType, Project publisherProject,
        IReadOnlyList<(Project Project, INamedTypeSymbol Copy)> consumers)
    {
        var context = new EvalLoadContext(await EmitAllAsync(solution));
        try
        {
            var pubType = context.Resolve(publisherProject.AssemblyName, MetadataName(publisherType));
            var instance = Sentinels.Create(pubType, adversarial: true);
            var json = JsonSerializer.Serialize(instance, pubType, EventBusOptions);
            var sent = Flatten(JsonNode.Parse(json));

            var results = new Dictionary<string, Result>(StringComparer.Ordinal);
            foreach (var (project, copy) in consumers)
            {
                var consumerType = context.Resolve(project.AssemblyName, MetadataName(copy));
                try
                {
                    var received = JsonSerializer.Deserialize(json, consumerType, EventBusOptions);
                    var seen = Flatten(JsonNode.Parse(JsonSerializer.Serialize(received, consumerType, EventBusOptions)));
                    var lost = seen.Where(kv => !sent.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
                    results[project.AssemblyName] = new Result(false, null, lost, json);
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
                {
                    results[project.AssemblyName] = new Result(true, e.Message, new HashSet<string>(), json);
                }
            }
            return results;
        }
        finally
        {
            context.Unload();
        }
    }

    async Task<Dictionary<string, byte[]>> EmitAllAsync(Solution solution)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            var key = (project.Id, await project.GetDependentVersionAsync());
            if (!_emitted.TryGetValue(key, out var bytes))
            {
                var compilation = (await project.GetCompilationAsync())!;
                using var ms = new MemoryStream();
                var emit = compilation.Emit(ms);
                if (!emit.Success) continue;   // e.g. test projects we never load
                _emitted[key] = bytes = ms.ToArray();
            }
            images[project.AssemblyName] = bytes;
        }
        return images;
    }

    /// <summary>Flattens JSON into path -> raw value text, arrays as "[]" (element 0), matching Ripple's path syntax.</summary>
    public static Dictionary<string, string> Flatten(JsonNode? node, string prefix = "")
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    var path = prefix.Length == 0 ? name : prefix + "." + name;
                    if (child is JsonObject or JsonArray)
                        foreach (var kv in Flatten(child, path)) map[kv.Key] = kv.Value;
                    else map[path] = child?.ToJsonString() ?? "null";
                }
                break;
            case JsonArray arr:
                if (arr.Count == 0) { map[prefix + "[]"] = "[]"; break; }
                var first = arr[0];
                if (first is JsonObject)
                    foreach (var kv in Flatten(first, prefix + "[]")) map[kv.Key.Replace("[].", "[].")] = kv.Value;
                else map[prefix + "[]"] = first?.ToJsonString() ?? "null";
                break;
        }
        // Flatten(obj, "X[]") produces "X[].Prop" because nested object paths are joined with '.'.
        return map;
    }

    /// <summary>CLR metadata name of a Roslyn type: Namespace.Outer+Inner`1.</summary>
    public static string MetadataName(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (var t = type; t is not null; t = t.ContainingType) parts.Push(t.MetadataName);
        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".";
        return ns + string.Join("+", parts);
    }

    public void Dispose() => _emitted.Clear();

    sealed class EvalLoadContext(Dictionary<string, byte[]> images) : AssemblyLoadContext(isCollectible: true)
    {
        readonly Dictionary<string, Assembly> _loaded = new(StringComparer.Ordinal);

        public Type Resolve(string assemblyName, string metadataName) =>
            LoadFromAssemblyName(new AssemblyName(assemblyName)).GetType(metadataName, throwOnError: true)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null) return null;
            if (_loaded.TryGetValue(name.Name, out var a)) return a;
            if (images.TryGetValue(name.Name, out var image))
                return _loaded[name.Name] = LoadFromStream(new MemoryStream(image));

            // Framework assemblies already in the default context (System.Text.Json above all) must be shared.
            if (File.Exists(Path.Combine(RuntimeDir, name.Name + ".dll"))) return null;
            foreach (var dir in SharedFrameworkDirs)
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return _loaded[name.Name] = LoadFromAssemblyPath(path);
            }
            var nuget = FindInNuGetCache(name);
            return nuget is null ? null : _loaded[name.Name] = LoadFromAssemblyPath(nuget);
        }

        static string? FindInNuGetCache(AssemblyName name)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", name.Name!.ToLowerInvariant());
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, name.Name + ".dll", SearchOption.AllDirectories)
                .Where(p => p.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}"))
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }
}

/// <summary>Builds instances with distinctive (and, when adversarial, edge-case) values without running constructors.</summary>
static class Sentinels
{
    static int _counter;

    public static object Create(Type type, bool adversarial, int depth = 0)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.Name == "<EqualityContract>k__BackingField") continue;
                field.SetValue(instance, Value(field.FieldType, adversarial, depth));
            }
        }
        return instance;
    }

    static object? Value(Type type, bool adversarial, int depth)
    {
        var n = Interlocked.Increment(ref _counter);
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return adversarial ? null : Value(underlying, adversarial, depth);
        if (type == typeof(string)) return $"s{n}";
        if (type == typeof(int)) return 1000 + n;
        if (type == typeof(long)) return adversarial ? 5_000_000_000L + n : 1000L + n;
        if (type == typeof(decimal)) return adversarial ? 12.5m + n : 1000m + n;
        if (type == typeof(double)) return adversarial ? 12.5 + n : 1000.0 + n;
        if (type == typeof(float)) return 12.5f + n;
        if (type == typeof(short)) return (short)(100 + n % 100);
        if (type == typeof(byte)) return (byte)(n % 200 + 1);
        if (type == typeof(bool)) return true;
        if (type == typeof(Guid)) return new Guid(n, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]);
        if (type == typeof(DateTime)) return new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc).AddMinutes(n);
        if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).AddMinutes(n);
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.Length > 1 ? values.GetValue(1) : values.GetValue(0);
        }
        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var array = Array.CreateInstance(element, 1);
            array.SetValue(Value(element, adversarial, depth + 1), 0);
            return array;
        }
        if (type.IsGenericType && type.GetGenericArguments().Length == 1
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) || type.IsInterface && type.IsGenericType)
        {
            var element = type.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(element);
            if (!type.IsAssignableFrom(listType)) return null;
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            list.Add(Value(element, adversarial, depth + 1));
            return list;
        }
        if (depth < 4 && (type.IsClass || type.IsValueType) && !type.IsPrimitive && type.Namespace?.StartsWith("System") != true)
            return Create(type, adversarial, depth + 1);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

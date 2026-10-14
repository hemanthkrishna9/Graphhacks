namespace Ripple.Core.Graph;

/// <summary>
/// In-memory accumulation of nodes and edges, de-duplicated by key, written to FalkorDB in UNWIND batches.
/// Every node has a unique string <c>key</c> per label.
/// </summary>
public sealed class GraphBatch
{
    readonly Dictionary<(string Label, string Key), Dictionary<string, object>> _nodes = new();
    readonly Dictionary<EdgeId, Dictionary<string, object>> _edges = new();

    public IReadOnlyDictionary<(string Label, string Key), Dictionary<string, object>> Nodes => _nodes;
    public IReadOnlyDictionary<EdgeId, Dictionary<string, object>> Edges => _edges;

    public string Node(string label, string key, params (string Name, object? Value)[] props)
    {
        if (!_nodes.TryGetValue((label, key), out var map))
        {
            map = new Dictionary<string, object> { ["key"] = key };
            _nodes[(label, key)] = map;
        }
        foreach (var (name, value) in props)
            if (value is not null) map[name] = value;
        return key;
    }

    public void Edge(string type, string fromLabel, string fromKey, string toLabel, string toKey, params (string Name, object? Value)[] props)
    {
        var id = new EdgeId(type, fromLabel, fromKey, toLabel, toKey);
        if (!_edges.TryGetValue(id, out var map))
        {
            map = new Dictionary<string, object>();
            _edges[id] = map;
        }
        foreach (var (name, value) in props)
            if (value is not null) map[name] = value;
    }

    public bool HasNode(string label, string key) => _nodes.ContainsKey((label, key));

    /// <summary>Removes edges whose endpoints were never declared (e.g. calls into source-generated code). Returns how many.</summary>
    public int PruneDanglingEdges()
    {
        var dangling = _edges.Keys.Where(e => !HasNode(e.FromLabel, e.FromKey) || !HasNode(e.ToLabel, e.ToKey)).ToList();
        foreach (var e in dangling) _edges.Remove(e);
        return dangling.Count;
    }
}

public readonly record struct EdgeId(string Type, string FromLabel, string FromKey, string ToLabel, string ToKey);

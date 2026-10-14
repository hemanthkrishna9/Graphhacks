using NFalkorDB;
using StackExchange.Redis;

namespace Ripple.Core.Graph;

/// <summary>Thin wrapper over NFalkorDB: batched upserts, indexes, atomic replace and read-only queries.</summary>
public sealed class GraphStore
{
    public static readonly string[] Labels =
        ["Service", "File", "Type", "Method", "EventContract", "EventShape", "Property", "Endpoint", "Test", "Person", "Commit", "PullRequest", "Finding"];

    static readonly (string Label, string Property)[] ExtraIndexes =
        [("Method", "fqn"), ("Method", "name"), ("Type", "fqn"), ("File", "path"), ("Property", "path"), ("EventShape", "name")];

    const int BatchSize = 500;
    readonly IDatabase _redis;
    readonly NFalkorDB.Graph _graph;
    public string Name { get; }

    public GraphStore(string connection, string graphName)
    {
        Name = graphName;
        _redis = ConnectionMultiplexer.Connect(connection).GetDatabase();
        _graph = new FalkorDB(_redis).SelectGraph(graphName);
    }

    GraphStore(IDatabase redis, string graphName)
    {
        Name = graphName;
        _redis = redis;
        _graph = new FalkorDB(redis).SelectGraph(graphName);
    }

    public GraphStore WithGraph(string graphName) => new(_redis, graphName);

    public bool Exists() => _redis.KeyExists(Name);

    public void Drop()
    {
        if (Exists()) _graph.Delete();
    }

    /// <summary>
    /// Writes the batch into a staging graph, then renames it over this graph, so readers never see a half-written graph.
    /// </summary>
    public void Replace(GraphBatch batch, Action<GraphStore>? derive = null)
    {
        var staging = WithGraph(Name + ".staging");
        staging.Drop();
        staging.EnsureIndexes();
        staging.Write(batch);
        derive?.Invoke(staging);
        _redis.KeyRename(staging.Name, Name);
    }

    public void EnsureIndexes()
    {
        // Creating the first index creates the graph key.
        var existing = Exists()
            ? Query("CALL db.indexes() YIELD label, properties RETURN label, properties")
                .SelectMany(r => ((object[])r["properties"]!).Select(p => $"{r["label"]}.{p}")).ToHashSet()
            : [];
        foreach (var (label, property) in Labels.Select(l => (l, "key")).Concat(ExtraIndexes))
            if (!existing.Contains($"{label}.{property}"))
                _graph.Query($"CREATE INDEX FOR (n:{label}) ON (n.{property})");
    }

    /// <summary>Upserts nodes, then edges. Fails loudly if any edge endpoint is missing (MATCH would drop it silently).</summary>
    public void Write(GraphBatch batch)
    {
        foreach (var group in batch.Nodes.GroupBy(n => n.Key.Label))
            foreach (var chunk in group.Select(n => (object)n.Value).Chunk(BatchSize))
                _graph.Query($"UNWIND $rows AS r MERGE (n:{group.Key} {{key: r.key}}) SET n += r",
                    new Dictionary<string, object> { ["rows"] = chunk.ToList() });

        foreach (var group in batch.Edges.GroupBy(e => (e.Key.Type, e.Key.FromLabel, e.Key.ToLabel)))
        {
            var rows = group.Select(e => (object)new Dictionary<string, object>
            {
                ["a"] = e.Key.FromKey, ["b"] = e.Key.ToKey, ["p"] = e.Value,
            });
            foreach (var chunk in rows.Chunk(BatchSize))
            {
                var result = _graph.Query(
                    $"UNWIND $rows AS r MATCH (a:{group.Key.FromLabel} {{key: r.a}}) MATCH (b:{group.Key.ToLabel} {{key: r.b}}) " +
                    $"MERGE (a)-[e:{group.Key.Type}]->(b) SET e += r.p RETURN count(e) AS n",
                    new Dictionary<string, object> { ["rows"] = chunk.ToList() });
                var written = Convert.ToInt32(result.First().GetValue<object>("n"));
                if (written != chunk.Length)
                {
                    var missing = chunk.Cast<Dictionary<string, object>>()
                        .Select(r => $"{r["a"]} -> {r["b"]}")
                        .Take(3);
                    throw new InvalidOperationException(
                        $"{group.Key.Type}: wrote {written} of {chunk.Length} edges; an endpoint node is missing. Sample: {string.Join("; ", missing)}");
                }
            }
        }
    }

    public IReadOnlyList<Dictionary<string, object?>> Query(string cypher, IDictionary<string, object?>? parameters = null, bool readOnly = true)
    {
        var p = (parameters ?? new Dictionary<string, object?>()).ToDictionary(kv => kv.Key, kv => kv.Value!);
        var rs = readOnly ? _graph.ReadOnlyQuery(cypher, p) : _graph.Query(cypher, p);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var rec in rs)
        {
            var row = new Dictionary<string, object?>();
            foreach (var key in rec.Keys) row[key] = rec.GetValue<object>(key);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The execution plan FalkorDB would use (GRAPH.EXPLAIN), one operation per line.</summary>
    public IReadOnlyList<string> Explain(string cypher, IDictionary<string, object?>? parameters = null) =>
        _graph.Explain(cypher, (parameters ?? new Dictionary<string, object?>()).ToDictionary(kv => kv.Key, kv => kv.Value!)).ToList();

    public void Execute(string cypher, IDictionary<string, object?>? parameters = null) => Query(cypher, parameters, readOnly: false);
}

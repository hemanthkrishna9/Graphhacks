using NFalkorDB;

namespace Ripple.Core.Graph;

/// <summary>Thin wrapper over NFalkorDB: batched upserts, indexes and read-only queries.</summary>
public sealed class GraphStore
{
    public static readonly string[] Labels =
        ["Service", "File", "Type", "Method", "EventContract", "EventShape", "Property", "Endpoint", "Test", "Person", "Commit", "PullRequest", "Finding"];

    const int BatchSize = 500;
    readonly NFalkorDB.Graph _graph;
    public string Name { get; }

    public GraphStore(string connection, string graphName)
    {
        Name = graphName;
        _graph = new FalkorDB(connection).SelectGraph(graphName);
    }

    public void Drop()
    {
        try { _graph.Delete(); } catch (Exception e) when (e.Message.Contains("empty key") || e.Message.Contains("Invalid graph")) { }
    }

    public void EnsureIndexes()
    {
        foreach (var label in Labels)
        {
            try { _graph.Query($"CREATE INDEX FOR (n:{label}) ON (n.key)"); }
            catch (Exception e) when (e.Message.Contains("already indexed")) { }
        }
    }

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
                _graph.Query(
                    $"UNWIND $rows AS r MATCH (a:{group.Key.FromLabel} {{key: r.a}}) MATCH (b:{group.Key.ToLabel} {{key: r.b}}) " +
                    $"MERGE (a)-[e:{group.Key.Type}]->(b) SET e += r.p",
                    new Dictionary<string, object> { ["rows"] = chunk.ToList() });
        }
    }

    public IReadOnlyList<Dictionary<string, object?>> Query(string cypher, IDictionary<string, object>? parameters = null, bool readOnly = true)
    {
        var rs = readOnly ? _graph.ReadOnlyQuery(cypher, parameters ?? new Dictionary<string, object>())
                          : _graph.Query(cypher, parameters ?? new Dictionary<string, object>());
        var rows = new List<Dictionary<string, object?>>();
        foreach (var rec in rs)
        {
            var row = new Dictionary<string, object?>();
            foreach (var key in rec.Keys) row[key] = rec.GetValue<object>(key);
            rows.Add(row);
        }
        return rows;
    }

    public void Execute(string cypher, IDictionary<string, object>? parameters = null) =>
        _graph.Query(cypher, parameters ?? new Dictionary<string, object>());
}

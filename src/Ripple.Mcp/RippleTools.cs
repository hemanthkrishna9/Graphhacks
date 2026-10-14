using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Ripple.Core.Graph;
using Ripple.Core.Queries;
using Ripple.Core.Review;

namespace Ripple.Mcp;

/// <summary>
/// Domain tools over the Ripple graph. Every tool runs fixed, read-only Cypher from <see cref="RippleQueries"/> or
/// <see cref="ToolQueries"/>; results are returned as JSON rows so the calling agent can cite exact graph paths.
/// </summary>
[McpServerToolType]
public static class RippleTools
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool(Name = "graph_schema", ReadOnly = true),
     Description("Describe the Ripple code graph: node labels, relationship types and key properties, with counts. Call this before writing Cypher.")]
    public static string GraphSchema(GraphStore store)
    {
        var labels = store.Query("MATCH (n) RETURN labels(n)[0] AS label, count(n) AS count ORDER BY count DESC");
        var edges = store.Query("MATCH (a)-[e]->(b) RETURN labels(a)[0] AS from, type(e) AS type, labels(b)[0] AS to, count(e) AS count ORDER BY count DESC");
        return JsonSerializer.Serialize(new { graph = store.Name, labels, edges, notes = ToolQueries.SchemaNotes }, Json);
    }

    [McpServerTool(Name = "list_contracts", ReadOnly = true),
     Description("List every integration-event contract (routing name) with the services that publish it and the services that handle it.")]
    public static string ListContracts(GraphStore store) =>
        JsonSerializer.Serialize(store.Query(ToolQueries.Contracts), Json);

    [McpServerTool(Name = "contract_breaks", ReadOnly = true),
     Description("Run Ripple's contract checks on the current graph: dangling reads, dangling forwards, type drift, unfillable reads and starved subscriptions. Optionally narrow to one contract (event name).")]
    public static string ContractBreaks(GraphStore store,
        [Description("Integration event name, e.g. OrderStatusChangedToPaidIntegrationEvent. Omit for all contracts.")] string? contract = null) =>
        JsonSerializer.Serialize(ContractChecker.Run(store, contract), Json);

    [McpServerTool(Name = "what_breaks_if_field_changes", ReadOnly = true),
     Description("Before changing or removing a field of an integration event: which handlers in which services read it (or forward the whole event), through which methods, at which file and line.")]
    public static string WhatBreaksIfFieldChanges(GraphStore store,
        [Description("Integration event name, e.g. OrderStatusChangedToPaidIntegrationEvent")] string contract,
        [Description("JSON path of the field, e.g. OrderStockItems[].Units or BuyerName")] string path) =>
        JsonSerializer.Serialize(store.Query(ToolQueries.FieldConsumers,
            new Dictionary<string, object?> { ["contract"] = contract, ["path"] = path }), Json);

    [McpServerTool(Name = "find_methods", ReadOnly = true),
     Description("Find methods whose fully qualified name contains the given text. Returns keys to pass to impact_of_method.")]
    public static string FindMethods(GraphStore store,
        [Description("Text to search for, e.g. 'Order.SetPaidStatus' or 'UpdateItem'")] string text) =>
        JsonSerializer.Serialize(store.Query(ToolQueries.FindMethods, new Dictionary<string, object?> { ["text"] = text }), Json);

    [McpServerTool(Name = "impact_of_method", ReadOnly = true),
     Description("What can a change to this method affect? In-process callers (up to 5 hops, including MediatR and interface dispatch) and handlers in other services reached through integration events it publishes.")]
    public static string ImpactOfMethod(GraphStore store,
        [Description("Method key from find_methods (\"{assembly}|{fqn}\")")] string key)
    {
        var p = new Dictionary<string, object?> { ["key"] = key };
        return JsonSerializer.Serialize(new
        {
            crossService = store.Query(RippleQueries.CrossServiceReach, p),
            callers = store.Query(RippleQueries.Callers, p),
        }, Json);
    }

    [McpServerTool(Name = "run_cypher", ReadOnly = true),
     Description("Run a read-only Cypher query against the Ripple graph (FalkorDB GRAPH.RO_QUERY; writes are rejected by the server). Returns at most 200 rows. Use graph_schema first.")]
    public static string RunCypher(GraphStore store, [Description("Read-only openCypher query")] string cypher)
    {
        try
        {
            return JsonSerializer.Serialize(store.Query(cypher).Take(200), Json);
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { error = e.Message }, Json);
        }
    }
}

public static class ToolQueries
{
    public const string SchemaNotes = """
        Keys: Service.key = assembly name; Type/Method/EventShape key = "{assembly}|{fqn}"; EventContract.key = event type name
        (the RabbitMQ routing key); Property.key = "{shapeKey}|{json path}". Each service declares its OWN copy of an event
        (EventShape) linked to the shared EventContract by SHAPE_OF; contracts match field-by-field on Property.path
        (JSON wire names, case-sensitive). Edges carry source='syntactic'|'inferred' (inferred: via='mediatr'|'domain-event'|'interface').
        Contract path: (pub:Method)-[:PUBLISHES]->(:EventShape)-[:SHAPE_OF]->(:EventContract)<-[:SHAPE_OF]-(:EventShape)<-[:HANDLES]-(h:Method)
        """;

    public const string Contracts = """
        MATCH (c:EventContract)
        OPTIONAL MATCH (pub:Method)-[:PUBLISHES]->(:EventShape)-[:SHAPE_OF]->(c)
        OPTIONAL MATCH (h:Method)-[:HANDLES]->(:EventShape)-[:SHAPE_OF]->(c)
        RETURN c.name AS contract, collect(DISTINCT pub.service) AS publishers, collect(DISTINCT h.service) AS consumers
        ORDER BY contract
        """;

    public const string FieldConsumers = """
        MATCH (c:EventContract {name: $contract})<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        MATCH (cs)-[:HAS_PROPERTY]->(cp:Property {path: $path})
        OPTIONAL MATCH (h)-[:CALLS*0..3]->(r:Method)-[rd:READS]->(cp)
        OPTIONAL MATCH (h)-[:CALLS*0..3]->(f:Method)-[fw:FORWARDS]->(cs)
        WITH h, cs, cp, collect(DISTINCT CASE WHEN rd.context = 'code' THEN {method: r.fqn, file: r.file, line: rd.line} END) AS reads,
             collect(DISTINCT CASE WHEN f IS NOT NULL THEN {method: f.fqn, file: f.file, line: fw.line, to: fw.to} END) AS forwards
        WHERE size(reads) > 0 OR size(forwards) > 0
        RETURN h.service AS service, h.fqn AS handler, cp.wireType AS wireType, reads, forwards
        ORDER BY service
        """;

    public const string FindMethods = """
        MATCH (m:Method) WHERE m.fqn CONTAINS $text
        RETURN m.key AS key, m.service AS service, m.file AS file, m.startLine AS line
        ORDER BY service, key
        LIMIT 25
        """;
}

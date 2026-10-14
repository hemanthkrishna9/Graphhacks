namespace Ripple.Core.Queries;

/// <summary>Read-only queries behind the MCP tools (beyond the contract checks in RippleQueries).</summary>
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
        WITH cs, h
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

namespace Ripple.Core.Queries;

/// <summary>
/// The read-only Cypher queries Ripple's findings come from. These strings are the product:
/// the review pipeline, the MCP tools and the README all use them verbatim.
/// </summary>
public static class RippleQueries
{
    /// <summary>
    /// Q1a — Dangling contract reads. A handler in one service reads a field (at a JSON path) of its own copy of an
    /// integration event, but a publisher's copy of the same contract no longer has that path, so the consumer gets a
    /// default value at run time. Optional $contract narrows to one event.
    /// </summary>
    public const string DanglingReads = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)-[:READS]->(cp:Property)<-[:HAS_PROPERTY]-(cs)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
          AND NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
        RETURN DISTINCT 'dangling-read' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS consumer, cp.path AS path, cp.jsonKind AS expected, null AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q1b — Dangling forwarded fields. A handler passes the whole event on (e.g. into a webhook payload), so every
    /// field of its copy is consumed; any path the publisher no longer sends goes out as a default value.
    /// </summary>
    public const string DanglingForwards = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:FORWARDS]-(h:Method)
        MATCH (cs)-[:HAS_PROPERTY]->(cp:Property)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
          AND NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
        RETURN DISTINCT 'dangling-forward' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS consumer, cp.path AS path, cp.jsonKind AS expected, null AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q1c — Type drift. Publisher and consumer both have the path, but the JSON kinds differ (e.g. number vs string),
    /// so deserialisation throws in the consumer.
    /// </summary>
    public const string TypeDrift = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        MATCH (cs)-[:HAS_PROPERTY]->(cp:Property), (ps)-[:HAS_PROPERTY]->(pp:Property)
        WHERE ps <> cs AND pp.path = cp.path AND pp.jsonKind <> cp.jsonKind AND ($contract IS NULL OR c.name = $contract)
        RETURN DISTINCT 'type-drift' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS consumer, cp.path AS path, cp.jsonKind AS expected, pp.jsonKind AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q2 — Cross-service reach of a changed method: does it (or anything it calls, up to 4 hops) publish an
    /// integration event, and which handlers in other services consume that contract?
    /// </summary>
    public const string CrossServiceReach = """
        MATCH (m:Method {key: $key})
        MATCH (m)-[:CALLS*0..4]->(pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE h.service <> m.service
        RETURN DISTINCT c.name AS contract, pub.fqn AS publisher, h.service AS consumerService, h.fqn AS consumer
        ORDER BY consumerService
        LIMIT 50
        """;

    /// <summary>Q3 — In-process impact: methods that reach the changed method through calls (up to 5 hops).</summary>
    public const string Callers = """
        MATCH path = (caller:Method)-[:CALLS*1..5]->(m:Method {key: $key})
        RETURN caller.service AS service, caller.fqn AS caller, min(length(path)) AS hops
        ORDER BY hops, caller
        LIMIT 50
        """;
}

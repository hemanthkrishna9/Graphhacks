namespace Ripple.Core.Queries;

/// <summary>
/// The read-only Cypher queries Ripple's findings come from. These strings are the product:
/// the review pipeline, the MCP tools and the README all use them verbatim.
///
/// Contract checks (Q1–Q5) return the same columns so they can be unioned into one finding list:
///   kind, contract, publisherService, publisher, consumerService, handler, reader, path, expected, actual
/// $contract (nullable) narrows any of them to one event contract.
/// </summary>
public static class RippleQueries
{
    /// <summary>
    /// Q1 — Dangling reads. A handler (or a method it calls, up to 3 hops) reads a field of its own copy of the event,
    /// but a publisher's copy no longer has that JSON path, so the consumer silently gets a default value.
    /// </summary>
    public const string DanglingReads = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
        MATCH (h)-[:CALLS*0..3]->(r:Method)-[rd:READS]->(cp:Property)<-[:HAS_PROPERTY]-(cs)
        WHERE rd.context <> 'log' AND NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
        RETURN DISTINCT 'dangling-read' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q2 — Dangling forwards. A handler (or a method it calls) hands the whole event to an untyped parameter
    /// (serialised into a webhook payload, say), so every field is consumed; any path the publisher no longer sends
    /// goes out as a default value.
    /// </summary>
    public const string DanglingForwards = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
        MATCH (h)-[:CALLS*0..3]->(r:Method)-[:FORWARDS]->(cs)-[:HAS_PROPERTY]->(cp:Property)
        WHERE NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
        RETURN DISTINCT 'dangling-forward' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q3 — Type drift. Both copies have the path but with different wire types (int32 vs decimal, string vs guid,
    /// nullable vs non-nullable...). Deserialisation throws in the consumer and the message is lost. Enums written as
    /// strings are compatible with strings, and a non-nullable value fits a nullable one.
    /// </summary>
    public const string TypeDrift = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
        MATCH (ps)-[:HAS_PROPERTY]->(pp:Property), (cs)-[:HAS_PROPERTY]->(cp:Property)
        WHERE pp.path = cp.path AND pp.wireType <> cp.wireType
          AND cp.wireType <> pp.wireType + '?'
          AND NOT (replace(pp.wireType, 'enum:string', 'string') = replace(cp.wireType, 'enum:string', 'string'))
          AND NOT (replace(pp.wireType, 'enum:string', 'string') + '?' = replace(cp.wireType, 'enum:string', 'string'))
        RETURN DISTINCT 'type-drift' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, null AS reader, cp.path AS path, cp.wireType AS expected, pp.wireType AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q4 — Unfillable reads. The consumer reads a property that deserialisation can never populate (get-only, no
    /// matching constructor parameter), so it is always default even though the publisher sends it.
    /// </summary>
    public const string UnfillableReads = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
        MATCH (h)-[:CALLS*0..3]->(r:Method)-[rd:READS]->(cp:Property {settable: false})<-[:HAS_PROPERTY]-(cs)
        WHERE rd.context <> 'log'
        RETURN DISTINCT 'unfillable-read' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q5 — Starved subscriptions. A service subscribes to a contract that nothing publishes any more
    /// (a PR deleted or renamed the publishing event): its handler never runs.
    /// </summary>
    public const string StarvedSubscriptions = """
        MATCH (s:Service)-[sub:SUBSCRIBES]->(c:EventContract)
        WHERE ($contract IS NULL OR c.name = $contract)
          AND NOT (:Method)-[:PUBLISHES]->(:EventShape)-[:SHAPE_OF]->(c)
        RETURN DISTINCT 'starved-subscription' AS kind, c.name AS contract, null AS publisherService, null AS publisher,
               s.name AS consumerService, sub.handler AS handler, null AS reader, null AS path, null AS expected, null AS actual
        ORDER BY contract, consumerService
        """;

    public static readonly (string Name, string Cypher, string Severity)[] ContractChecks =
    [
        ("Q1 dangling reads", DanglingReads, "high"),
        ("Q2 dangling forwards", DanglingForwards, "high"),
        ("Q3 type drift", TypeDrift, "critical"),
        ("Q4 unfillable reads", UnfillableReads, "medium"),
        ("Q5 starved subscriptions", StarvedSubscriptions, "high"),
    ];

    /// <summary>
    /// Q6 — Cross-service reach of a method: does it (or anything it calls, up to 6 hops including MediatR and
    /// domain-event dispatch) publish an integration event, and which handlers in other services consume it?
    /// </summary>
    public const string CrossServiceReach = """
        MATCH (m:Method {key: $key})
        MATCH path = (m)-[:CALLS*0..6]->(pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE h.service <> m.service
        RETURN c.name AS contract, pub.fqn AS publisher, h.service AS consumerService, h.fqn AS handler, min(length(path)) AS hops
        ORDER BY hops, consumerService
        LIMIT 50
        """;

    /// <summary>Q7 — In-process impact: methods that reach the changed method through calls (up to 5 hops).</summary>
    public const string Callers = """
        MATCH path = (caller:Method)-[:CALLS*1..5]->(m:Method {key: $key})
        RETURN caller.service AS service, caller.fqn AS caller, min(length(path)) AS hops
        ORDER BY hops, caller
        LIMIT 50
        """;
}

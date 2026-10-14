namespace Ripple.Core.Queries;

/// <summary>
/// The read-only Cypher queries Ripple's findings come from. These strings are the product:
/// the review pipeline, the MCP tools and the README all use them verbatim.
///
/// Contract checks (Q1–Q5) return the same columns so they can be unioned into one finding list:
///   kind, contract, publisherService, publisher, consumerService, handler, reader, path, expected, actual
/// $contract (nullable) narrows any of them to one event contract.
///
/// Note the `WITH` after each first WHERE: on FalkorDB 4.x (module 60002) a WHERE on the first MATCH followed by a
/// MATCH that starts from two already-bound nodes is planned as a Cartesian product that ignores the bindings.
/// Projecting with WITH avoids it. Likewise a join written as `a.path = b.path` across two traversals can be planned as a
/// Value Hash Join that loses a binding, so joins on property values are written as inline pattern properties.
/// `ripple selfcheck` EXPLAINs every query and fails on either operator.
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
        WITH pub, ps, c, cs, h
        MATCH (h)-[:CALLS*0..3]->(r:Method)-[rd:READS]->(cp:Property)<-[:HAS_PROPERTY]-(cs)
        WHERE rd.context <> 'log' AND NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
        RETURN DISTINCT 'dangling-read' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual,
               pub.file AS publisherFile, pub.startLine AS publisherLine, r.file AS readerFile, rd.line AS readerLine
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
        WITH pub, ps, c, cs, h
        MATCH (h)-[:CALLS*0..3]->(r:Method)-[fw:FORWARDS]->(cs)-[:HAS_PROPERTY]->(cp:Property)
        WHERE NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
        RETURN DISTINCT 'dangling-forward' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual,
               pub.file AS publisherFile, pub.startLine AS publisherLine, r.file AS readerFile, fw.line AS readerLine
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q3 — Type drift. Both copies have the path but the consumer's wire type cannot accept every value the publisher's
    /// can send, so deserialisation throws in the consumer and the message is lost. Compatibility is directional:
    /// a publisher int32 fits a consumer int64/decimal/double, a guid/datetime/string enum fits a string, a non-nullable
    /// value fits a nullable one. ($compatible lists the allowed publisher->consumer pairs; see RippleQueries.Compatible.)
    /// </summary>
    public const string TypeDrift = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
        WITH pub, ps, c, cs, h
        MATCH (cs)-[:HAS_PROPERTY]->(cp:Property)
        WITH pub, ps, c, cs, h, cp
        MATCH (ps)-[:HAS_PROPERTY]->(pp:Property {path: cp.path})
        WHERE pp.wireType <> cp.wireType
          AND NOT replace(pp.wireType, '?', '') + '>' + replace(cp.wireType, '?', '') IN $compatible
          AND NOT (replace(pp.wireType, '?', '') = replace(cp.wireType, '?', '') AND cp.wireType ENDS WITH '?')
        RETURN DISTINCT 'type-drift' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, null AS reader, cp.path AS path, cp.wireType AS expected, pp.wireType AS actual,
               pub.file AS publisherFile, pub.startLine AS publisherLine, cp.file AS readerFile, cp.line AS readerLine
        ORDER BY contract, consumerService, path
        """;

    /// <summary>Publisher wire type > consumer wire type pairs that always deserialise (System.Text.Json defaults).</summary>
    public static readonly string[] Compatible =
    [
        "int32>int64", "int32>decimal", "int32>double", "int64>decimal", "int64>double", "decimal>double", "double>decimal",
        "guid>string", "datetime>string", "enum:string>string", "enum:number>int32", "enum:number>int64",
    ];

    /// <summary>
    /// Q4 — Unfillable reads. The consumer reads a property that deserialisation can never populate (get-only, no
    /// matching constructor parameter), so it is always default even though the publisher sends it.
    /// </summary>
    public const string UnfillableReads = """
        MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
        WITH pub, ps, c, cs, h
        MATCH (h)-[:CALLS*0..3]->(r:Method)-[rd:READS]->(cp:Property {settable: false})<-[:HAS_PROPERTY]-(cs)
        WHERE rd.context <> 'log'
        RETURN DISTINCT 'unfillable-read' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
               h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual,
               pub.file AS publisherFile, pub.startLine AS publisherLine, r.file AS readerFile, rd.line AS readerLine
        ORDER BY contract, consumerService, path
        """;

    /// <summary>
    /// Q4b — Unbindable constructor. System.Text.Json deserialises through the consumer copy's constructor, and every
    /// constructor parameter must match a property by name; if one doesn't (e.g. a property was renamed but its
    /// constructor parameter wasn't), every message for that contract throws in the consumer.
    /// </summary>
    public const string UnbindableConstructor = """
        MATCH (c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
        WHERE size(cs.unboundParams) > 0 AND ($contract IS NULL OR c.name = $contract)
        UNWIND cs.unboundParams AS param
        RETURN DISTINCT 'unbindable-constructor' AS kind, c.name AS contract, null AS publisherService, null AS publisher,
               h.service AS consumerService, h.fqn AS handler, null AS reader, param AS path, null AS expected, null AS actual,
               null AS publisherFile, null AS publisherLine, cs.file AS readerFile, cs.line AS readerLine
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
        ("Q4b unbindable constructor", UnbindableConstructor, "critical"),
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

    /// <summary>
    /// Projection for graph algorithms: (consumer:Service)-[:DEPENDS_ON {contracts, weight}]->(publisher:Service),
    /// derived from the contract layer. FalkorDB's algo.* procedures take one label and one relationship type, so the
    /// multi-hop event paths are folded into this single edge type first. (The WITH between the two MATCHes works
    /// around a planner issue noted in docs/falkordb-cartesian-repro.md.)
    /// </summary>
    public const string MaterializeDependsOn = """
        MATCH (pub:Method)-[:PUBLISHES]->(:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(:EventShape)<-[:HANDLES]-(h:Method)
        WHERE pub.service <> h.service
        WITH h.service AS consumer, pub.service AS publisher, collect(DISTINCT c.name) AS contracts
        MATCH (a:Service {key: consumer})
        WITH a, publisher, contracts
        MATCH (b:Service {key: publisher})
        MERGE (a)-[d:DEPENDS_ON]->(b)
        SET d.contracts = contracts, d.weight = size(contracts)
        """;

    /// <summary>
    /// Bridge score: betweenness centrality over the service dependency projection. A change to a service that many
    /// event paths run through reaches further; stored on Service.bridgeScore for the report and the MCP tools.
    /// </summary>
    public const string BridgeScores = """
        CALL algo.betweenness({nodeLabels: ['Service'], relationshipTypes: ['DEPENDS_ON']}) YIELD node, score
        SET node.bridgeScore = score
        """;

    /// <summary>PageRank over the same projection: services whose events many others depend on (Service.dependencyRank).</summary>
    public const string DependencyRank = """
        CALL algo.pageRank('Service', 'DEPENDS_ON') YIELD node, score
        SET node.dependencyRank = score
        """;

    /// <summary>Q8 — Service risk: what each service publishes to and consumes from others, with its algorithm scores.</summary>
    public const string ServiceRisk = """
        MATCH (s:Service {kind: 'service'})
        OPTIONAL MATCH (s)-[out:DEPENDS_ON]->(up:Service)
        WITH s, collect(DISTINCT {service: up.name, contracts: out.contracts}) AS consumes
        OPTIONAL MATCH (down:Service)-[inc:DEPENDS_ON]->(s)
        RETURN s.name AS service, s.bridgeScore AS bridgeScore, s.dependencyRank AS dependencyRank,
               collect(DISTINCT {service: down.name, contracts: inc.contracts}) AS consumedBy, consumes
        ORDER BY bridgeScore DESC, dependencyRank DESC
        """;

    /// <summary>Every product query by name (contract checks, impact queries and MCP tool queries).</summary>
    public static IEnumerable<(string Name, string Cypher)> All() =>
        typeof(RippleQueries).GetFields().Concat(typeof(ToolQueries).GetFields())
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && ((string)f.GetRawConstantValue()!).Contains("MATCH")
                        && !((string)f.GetRawConstantValue()!).Contains("MERGE"))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!));
}

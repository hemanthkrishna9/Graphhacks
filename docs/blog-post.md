# Your microservices have a call graph your compiler can't see

*Draft for the Graph Hacks side prize. Numbers are from the repository's `eval/` folder.*

Here is a pull request to Microsoft's eShop reference app:

```diff
 public record OrderStockItem
 {
     public int ProductId { get; }
-    public int Units { get; }
+    public int Quantity { get; }
```

It builds. Every test passes. An AI reviewer looking at the diff says it's a harmless rename. In production, the
Catalog service confirms orders it can't fill and throws on every paid order (so stock never goes down), and the
Webhooks service tells external subscribers that every order line has zero units.

## Why nothing catches it

eShop's services talk through integration events on RabbitMQ, and each service declares **its own copy** of every event
it publishes or consumes. Ordering has an `OrderStockItem`; so does Catalog, in a different namespace, in a different
assembly. The bus routes messages by the event's class name. System.Text.Json fills the consumer's copy by matching
property names. No reference connects the two types, so the compiler has nothing to check, and a call graph has no
edge from Ordering to Catalog at all — the "call" is a message on a queue.

The coupling is real, though. It just lives in a layer most tools don't model: the contract.

## Modelling the contract as a graph

Ripple loads the solution with Roslyn's `MSBuildWorkspace` and walks the semantic model of every project. Besides the
usual structure (services, files, types, methods, calls), it extracts a contract layer into FalkorDB:

```
(:Method)-[:PUBLISHES]->(:EventShape)-[:SHAPE_OF]->(:EventContract)<-[:SHAPE_OF]-(:EventShape)<-[:HANDLES]-(:Method)
                              |                                              |
                        [:HAS_PROPERTY]                                [:HAS_PROPERTY]
                              v                                              v
                 (:Property {path:'OrderStockItems[].Units'})   (:Property {path:'OrderStockItems[].Units'})<-[:READS]-(:Method)
```

- An `EventContract` is keyed by the routing name, because that's what the bus uses.
- Each service's copy is an `EventShape`. Its fields are flattened into JSON wire paths, honouring
  `[JsonPropertyName]`, `[JsonIgnore]` and `[JsonInclude]`.
- `READS` edges come from the semantic model: any place in the consuming service that reads one of those properties,
  including inside lambdas and pattern matches.
- `FORWARDS` marks a handler that hands the whole event to an untyped parameter. eShop's Webhooks service does this
  when it serialises the event into a webhook payload.

The headline check is then one Cypher query: find a handler that reads a path its own copy declares, where a
publisher's copy of the same contract doesn't have that path. To review a pull request, Ripple indexes the base and head
commits into two graphs and reports what's new in head. A 20-second ingest makes that cheaper than incremental
re-indexing.

## Does the graph actually do the work?

To find out, we built a benchmark that doesn't trust Ripple:

1. **221 mutations of real eShop code**, applied with Roslyn and kept only if everything still compiles. They cover:
   publisher renames, renames that keep the wire name (controls that should flag nothing), `[JsonIgnore]`, nullability
   changes, `int`→`long`, consumer-side renames and event class renames.
2. **Ground truth from the wire.** Compile each mutated solution in memory, load publisher and consumer types into an
   `AssemblyLoadContext`, serialise an instance full of adversarial values with eShop's own JSON options, deserialise
   it into each consumer's copy, and diff.

| Approach | Precision | Recall |
|---|---|---|
| Compiler | n/a | 0% |
| grep (event name + member in one file) | 47% | 94% |
| Pub/sub topology (who subscribes) | 43% | 72% |
| Schema diff (Ripple's graph without `READS`) | 78% | 100% |
| **Ripple** | **100%** | **100%** |

(on 221 auto-generated, compile-clean mutations in seven classes; per consumer service)

The interesting row is the schema diff. It's a proxy for what a schema-registry compatibility check would tell you: every
subscriber whose copy of the event declares a changed field. It catches everything, but it raised 48 false alarms on fields nobody reads. The `READS`
edges are what remove those alarms, and they only exist because the graph connects the contract down to individual
lines of consumer code.

The perfect score also has limits we document. eShop's handlers read fields inline, so the benchmark never exercises
reads several calls deep. Enum renames, runtime converters and reflection aren't modelled at all.

## Three things FalkorDB taught us

The benchmark also caught Ripple returning wrong answers. Tracking them down, we found three query-planner issues in
FalkorDB 6.0.2:

- A `WHERE` followed by a `MATCH` from two bound nodes became a Cartesian product.
- Two index lookups on projected values returned nothing.
- A join on property values lost one side's binding.

Each has a repro and a workaround in the repo. Every Ripple query now goes through `GRAPH.EXPLAIN` in CI-style
self-checks, and any plan with a Cartesian product or a value hash join fails.

## Try it

`docker compose up` brings up FalkorDB with the eShop graphs preloaded. `ripple analyze` reviews a branch, and the MCP
server lets any agent ask "what breaks if I change this field?" before it edits. Code, queries and the full evaluation
are in the repository.

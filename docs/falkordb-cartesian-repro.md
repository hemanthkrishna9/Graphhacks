# FalkorDB: bound variables dropped after a filtered MATCH

Found while building Ripple (FalkorDB graph module 60002 = v6.0.2, Redis 8.10.2, image `falkordb/falkordb@sha256:107455e8…`, 2026-10-15).

A `WHERE` on the first `MATCH`, followed by a `MATCH` that starts from **two** already-bound nodes, is planned as a
Cartesian product that re-scans both nodes by label, so the bindings are ignored.

```
GRAPH.QUERY t "CREATE (a:S {n:'a'})-[:P]->(:Q {t:1}), (b:S {n:'b'})-[:P]->(:Q {t:2}),
                      (c:S {n:'c'})-[:P]->(:Q {t:1}), (d:S {n:'d'})-[:P]->(:Q {t:2}),
                      (a)-[:L]->(b), (c)-[:L]->(d)"

# Expected 2 rows (a,b) and (c,d). Returns 32 rows: every x with every y, twice.
GRAPH.QUERY t "MATCH (x:S)-[:L]->(y:S) WHERE x <> y
               MATCH (x)-[:P]->(pp:Q) MATCH (y)-[:P]->(cp:Q)
               RETURN x.n, y.n, pp.t, cp.t"

GRAPH.EXPLAIN t "<same query>"
#   Project
#       Cartesian Product
#           Filter
#               Conditional Traverse | (x)->(y:S)
#                   Node By Label Scan | (x:S)
#           Conditional Traverse | (x)->(pp:Q)
#               Node By Label Scan | (x:S)        <- x re-scanned
#           Conditional Traverse | (y)->(cp:Q)
#               Node By Label Scan | (y:S)        <- y re-scanned
```

Correct results (2 rows) when any of these hold:
- no `WHERE` on the first `MATCH`;
- the later `MATCH` references only one of the bound nodes;
- a `WITH x, y` projection follows the `WHERE` (Ripple's workaround);
- everything is one path pattern.

Ripple's `selfcheck` command runs `GRAPH.EXPLAIN` on every product query and fails if any plan contains a
Cartesian Product.

## Second observation: consecutive MATCHes on projected values return nothing

On the Ripple graph for eShop (not yet reduced to a minimal repro: the same shape works on a toy graph), this returns
**0 rows**, while each half returns the expected 9 pairs:

```
MATCH (pub:Method)-[:PUBLISHES]->(:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(:EventShape)<-[:HANDLES]-(h:Method)
WHERE pub.service <> h.service
WITH h.service AS consumer, pub.service AS publisher, collect(DISTINCT c.name) AS contracts
MATCH (a:Service {key: consumer}) MATCH (b:Service {key: publisher})
RETURN consumer, publisher
```

Inserting `WITH a, publisher, contracts` between the two MATCHes returns the 9 rows. Ripple uses that form when it
materialises `(:Service)-[:DEPENDS_ON]->(:Service)`.

## Third observation: a value join loses one side's binding

Ripple's type-drift check pairs each publisher field with the consumer field at the same JSON path. Written as a join
on property values, FalkorDB plans it as a **Value Hash Join** whose `(ps)->(pp)` branch is no longer restricted to the
bound `ps` (checked by returning `ps.key = pp.shape`: most rows were `false`):

```
MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
WHERE ps <> cs
WITH pub, ps, c, cs, h
MATCH (ps)-[:HAS_PROPERTY]->(pp:Property)
MATCH (cs)-[:HAS_PROPERTY]->(cp:Property)
WHERE pp.path = cp.path                       -- planned as: Value Hash Join (ps)->(pp) / (cs)->(cp)
RETURN ps.key = pp.shape AS bound, count(*)   -- 'false' for most rows
```

Matching the publisher field with an inline pattern property instead binds correctly:

```
MATCH (cs)-[:HAS_PROPERTY]->(cp:Property)
WITH pub, ps, c, cs, h, cp
MATCH (ps)-[:HAS_PROPERTY]->(pp:Property {path: cp.path})
```

`ripple selfcheck` fails on any plan that contains `Cartesian Product` or `Value Hash Join`.

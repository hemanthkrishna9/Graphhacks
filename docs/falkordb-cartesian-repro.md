# FalkorDB: bound variables dropped after a filtered MATCH

Found while building Ripple (FalkorDB module version 60002, Redis 8.10.2, `falkordb/falkordb:latest` on 2026-10-15).

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

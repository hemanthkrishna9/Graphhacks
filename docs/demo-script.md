# Demo video script (3 minutes)

Setup before recording: `docker compose up -d` (graphs preloaded), FalkorDB Browser open on http://localhost:3000,
eShop clone with branch `demo/rename-units` (one commit: `OrderStockItem.Units` → `Quantity` in Ordering), terminal
in the Ripple repo, Claude Desktop with the Ripple MCP server configured.

| Time | Beat | On screen | Say |
|---|---|---|---|
| 0:00–0:20 | The trap | The 3-line diff in Ordering. `dotnet build` → 0 errors. | "A rename in Ordering. It builds, tests pass, every reviewer approves. It breaks two other services." |
| 0:20–0:35 | Why nobody sees it | Side by side: Ordering's `OrderStockItem` and Catalog's `OrderStockItem`. | "Each service has its own copy of the event. RabbitMQ routes by class name, JSON matches by field name. No compiler links them." |
| 0:35–1:15 | The catch | `ripple analyze --base main --head demo/rename-units --markdown comment.md`, then the rendered comment. | "Ripple indexes both commits into FalkorDB and diffs the contract checks. Three findings: Catalog stops decrementing stock and oversells; Webhooks sends `Units: 0` to subscribers. Each one carries the graph path and the query that found it." |
| 1:15–1:35 | In the graph | FalkorDB Browser: the path query from the README, drawn for the Paid event. | "Publisher, its copy, the shared contract, Catalog's copy, the handler, the field it reads." |
| 1:35–2:05 | The proof | `eval/results.md` summary table. | "221 mutations of eShop that all compile. Ground truth from a real JSON round-trip. Compiler: zero. A schema registry: 48 false alarms. Ripple: every broken service, no false alarms." |
| 2:05–2:35 | The agent | Claude Desktop: "What breaks if I change `OrderStockItems[].ProductId` on the paid event?" → `what_breaks_if_field_changes`; then "Which service is riskiest to change?" → `service_risk` (betweenness). | "The same queries are MCP tools. Any agent can ask the graph before it edits." |
| 2:35–2:50 | FalkorDB | `ripple selfcheck` output + docs/falkordb-cartesian-repro.md. | "Along the way we found three FalkorDB planner issues; every query is EXPLAIN-checked." |
| 2:50–3:00 | Close | README title. | "Ripple. The diff is three lines. The blast radius is three services." |

Record by 4 PM IST on Oct 18; rehearse twice.

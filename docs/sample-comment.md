## Ripple: cross-service impact

**This change compiles, but breaks 2 service(s) at run time** through 2 integration event(s): `Catalog.API`, `Webhooks.API`.

### 1. `Catalog.API` — reads a field `OrderStatusChangedToAwaitingValidationIntegrationEvent` no longer sends

`Catalog.API` reads `OrderStockItems[].Units` from its own copy of `OrderStatusChangedToAwaitingValidationIntegrationEvent`, but `Ordering.API` no longer sends it. Deserialisation succeeds and the value is silently the default.

**Suggested fix:** Keep the old wire name with `[JsonPropertyName]` on the publisher, or update every consumer's copy in the same change.

Graph path:
```
(Ordering.API) OrderStatusChangedToAwaitingValidationDomainEventHandler.Handle()
  -[:PUBLISHES]-> (:EventShape Ordering.API)
  -[:SHAPE_OF]-> (:EventContract {name: 'OrderStatusChangedToAwaitingValidationIntegrationEvent'})
  <-[:SHAPE_OF]- (:EventShape Catalog.API)
  <-[:HANDLES]- (Catalog.API) OrderStatusChangedToAwaitingValidationIntegrationEventHandler.Handle()
  -[:READS]-> (:Property {path: 'OrderStockItems[].Units'})  (missing in publisher's copy)
```
<sub>severity **high** · produced by `Q1 dangling reads` · publisher [`OrderStatusChangedToAwaitingValidationDomainEventHandler.cs:23`](https://github.com/dotnet/eShop/blob/main/src/Ordering.API/Application/DomainEventHandlers/OrderStatusChangedToAwaitingValidationDomainEventHandler.cs#L23) · consumer [`OrderStatusChangedToAwaitingValidationIntegrationEventHandler.cs:20`](https://github.com/dotnet/eShop/blob/main/src/Catalog.API/IntegrationEvents/EventHandling/OrderStatusChangedToAwaitingValidationIntegrationEventHandler.cs#L20)</sub>

### 2. `Catalog.API` — reads a field `OrderStatusChangedToPaidIntegrationEvent` no longer sends

`Catalog.API` reads `OrderStockItems[].Units` from its own copy of `OrderStatusChangedToPaidIntegrationEvent`, but `Ordering.API` no longer sends it. Deserialisation succeeds and the value is silently the default.

**Suggested fix:** Keep the old wire name with `[JsonPropertyName]` on the publisher, or update every consumer's copy in the same change.

Graph path:
```
(Ordering.API) OrderStatusChangedToPaidDomainEventHandler.Handle()
  -[:PUBLISHES]-> (:EventShape Ordering.API)
  -[:SHAPE_OF]-> (:EventContract {name: 'OrderStatusChangedToPaidIntegrationEvent'})
  <-[:SHAPE_OF]- (:EventShape Catalog.API)
  <-[:HANDLES]- (Catalog.API) OrderStatusChangedToPaidIntegrationEventHandler.Handle()
  -[:READS]-> (:Property {path: 'OrderStockItems[].Units'})  (missing in publisher's copy)
```
<sub>severity **high** · produced by `Q1 dangling reads` · publisher [`OrderStatusChangedToPaidDomainEventHandler.cs:22`](https://github.com/dotnet/eShop/blob/main/src/Ordering.API/Application/DomainEventHandlers/OrderStatusChangedToPaidDomainEventHandler.cs#L22) · consumer [`OrderStatusChangedToPaidIntegrationEventHandler.cs:17`](https://github.com/dotnet/eShop/blob/main/src/Catalog.API/IntegrationEvents/EventHandling/OrderStatusChangedToPaidIntegrationEventHandler.cs#L17)</sub>

### 3. `Webhooks.API` — forwards `OrderStatusChangedToPaidIntegrationEvent` with fields that are no longer sent

`Webhooks.API` passes its whole copy of `OrderStatusChangedToPaidIntegrationEvent` on (serialised, e.g. into a webhook payload), including `OrderStockItems[].Units`, which `Ordering.API` no longer sends. Downstream receivers silently get default values.

**Suggested fix:** Keep the old wire name with `[JsonPropertyName]` on the publisher, or update every consumer's copy in the same change.

Graph path:
```
(Ordering.API) OrderStatusChangedToPaidDomainEventHandler.Handle()
  -[:PUBLISHES]-> (:EventShape Ordering.API)
  -[:SHAPE_OF]-> (:EventContract {name: 'OrderStatusChangedToPaidIntegrationEvent'})
  <-[:SHAPE_OF]- (:EventShape Webhooks.API)
  <-[:HANDLES]- (Webhooks.API) OrderStatusChangedToPaidIntegrationEventHandler.Handle()
  -[:FORWARDS]-> (:Property {path: 'OrderStockItems[].Units'})  (missing in publisher's copy)
```
<sub>severity **high** · produced by `Q2 dangling forwards` · publisher [`OrderStatusChangedToPaidDomainEventHandler.cs:22`](https://github.com/dotnet/eShop/blob/main/src/Ordering.API/Application/DomainEventHandlers/OrderStatusChangedToPaidDomainEventHandler.cs#L22) · consumer [`OrderStatusChangedToPaidIntegrationEventHandler.cs:14`](https://github.com/dotnet/eShop/blob/main/src/Webhooks.API/IntegrationEvents/OrderStatusChangedToPaidIntegrationEventHandler.cs#L14)</sub>

<details><summary>How Ripple knows</summary>

Ripple indexed the base (`24d6f061d6`) and head (`ccbac459e0`) commits into two FalkorDB graphs (`eshop@24d6f061d6.dcdfb111`, `eshop@ccbac459e0.dcdfb111`) and reports findings present only in head. Each service declares its own copy of an integration event; copies are linked through an `EventContract` node keyed by the routing name and compared field by field on JSON wire paths. The findings above come from these read-only queries:

`Q1 dangling reads`
```cypher
MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
WITH pub, ps, c, cs, h
MATCH (h)-[:CALLS*0..3]->(r:Method)-[rd:READS]->(cp:Property)<-[:HAS_PROPERTY]-(cs)
WHERE rd.context <> 'log' AND NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
RETURN DISTINCT 'dangling-read' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
       h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual,
       pub.file AS publisherFile, pub.startLine AS publisherLine, r.file AS readerFile, rd.line AS readerLine
ORDER BY contract, consumerService, path
```
`Q2 dangling forwards`
```cypher
MATCH (pub:Method)-[:PUBLISHES]->(ps:EventShape)-[:SHAPE_OF]->(c:EventContract)<-[:SHAPE_OF]-(cs:EventShape)<-[:HANDLES]-(h:Method)
WHERE ps <> cs AND ($contract IS NULL OR c.name = $contract)
WITH pub, ps, c, cs, h
MATCH (h)-[:CALLS*0..3]->(r:Method)-[fw:FORWARDS]->(cs)-[:HAS_PROPERTY]->(cp:Property)
WHERE NOT (ps)-[:HAS_PROPERTY]->(:Property {path: cp.path})
RETURN DISTINCT 'dangling-forward' AS kind, c.name AS contract, pub.service AS publisherService, pub.fqn AS publisher,
       h.service AS consumerService, h.fqn AS handler, r.fqn AS reader, cp.path AS path, cp.wireType AS expected, null AS actual,
       pub.file AS publisherFile, pub.startLine AS publisherLine, r.file AS readerFile, fw.line AS readerLine
ORDER BY contract, consumerService, path
```
</details>

<sub>Explanations: template (set ANTHROPIC_API_KEY for model-written explanations). Findings and paths come only from the graph.</sub>

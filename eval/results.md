Mutations: 80 (all compile cleanly). Breaking: 25. Wire-compatible controls: 40.
Scored per (mutation, consumer service). "Breaks caught" = mutations whose broken services were all found and nothing else.

| Approach | Precision | Recall | TP | FP | FN | Breaks caught exactly | Controls wrongly flagged |
|---|---|---|---|---|---|---|---|
| Compiler (dotnet build) | n/a | 0 % | 0 | 0 | 28 | 0/25 | 0/40 |
| grep for the property name | 12 % | 100 % | 28 | 206 | 0 | 9/25 | 40/40 |
| Call graph only (no contract layer) | n/a | 0 % | 0 | 0 | 28 | 0/25 | 0/40 |
| Ripple (contract graph) | 100 % | 100 % | 28 | 0 | 0 | 25/25 | 0/40 |

| Mutation | Kind | Truth | Ripple | grep | Call graph |
|---|---|---|---|---|---|
| Catalog.API:ConfirmedOrderStockItem.HasStock | rename | Ordering.API | Ordering.API | Ordering.API | - |
| Catalog.API:ConfirmedOrderStockItem.HasStock | rename-keep | - | - | Ordering.API | - |
| Catalog.API:ConfirmedOrderStockItem.ProductId | rename | Ordering.API | Ordering.API | Basket.API, Ordering.API, WebApp, Webhooks.API | - |
| Catalog.API:ConfirmedOrderStockItem.ProductId | rename-keep | - | - | Basket.API, Ordering.API, WebApp, Webhooks.API | - |
| Catalog.API:OrderStockConfirmedIntegrationEvent.OrderId | rename | Ordering.API | Ordering.API | Ordering.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Catalog.API:OrderStockConfirmedIntegrationEvent.OrderId | rename-keep | - | - | Ordering.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Catalog.API:OrderStockRejectedIntegrationEvent.OrderId | rename | Ordering.API | Ordering.API | Ordering.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Catalog.API:OrderStockRejectedIntegrationEvent.OrderId | rename-keep | - | - | Ordering.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Catalog.API:OrderStockRejectedIntegrationEvent.OrderStockItems | rename | Ordering.API | Ordering.API | Ordering.API, Webhooks.API | - |
| Catalog.API:OrderStockRejectedIntegrationEvent.OrderStockItems | rename-keep | - | - | Ordering.API, Webhooks.API | - |
| Catalog.API:ProductPriceChangedIntegrationEvent.NewPrice | rename | - | - | Webhooks.API | - |
| Catalog.API:ProductPriceChangedIntegrationEvent.NewPrice | rename-keep | - | - | Webhooks.API | - |
| Catalog.API:ProductPriceChangedIntegrationEvent.OldPrice | rename | - | - | Webhooks.API | - |
| Catalog.API:ProductPriceChangedIntegrationEvent.OldPrice | rename-keep | - | - | Webhooks.API | - |
| Catalog.API:ProductPriceChangedIntegrationEvent.ProductId | rename | - | - | Basket.API, Ordering.API, WebApp, Webhooks.API | - |
| Catalog.API:ProductPriceChangedIntegrationEvent.ProductId | rename-keep | - | - | Basket.API, Ordering.API, WebApp, Webhooks.API | - |
| OrderProcessor:GracePeriodConfirmedIntegrationEvent.OrderId | rename | Ordering.API | Ordering.API | Catalog.API, Ordering.API, PaymentProcessor, WebApp, Webhooks.API | - |
| OrderProcessor:GracePeriodConfirmedIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, Ordering.API, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStartedIntegrationEvent.UserId | rename | Basket.API | Basket.API | Basket.API, WebApp, Webhooks.API | - |
| Ordering.API:OrderStartedIntegrationEvent.UserId | rename-keep | - | - | Basket.API, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.BuyerIdentityGuid | rename | WebApp | WebApp | WebApp | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.BuyerIdentityGuid | rename-keep | - | - | WebApp | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.BuyerName | rename | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.BuyerName | rename-keep | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.OrderId | rename | Catalog.API | Catalog.API | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.OrderStatus | rename | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.OrderStatus | rename-keep | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.OrderStockItems | rename | Catalog.API | Catalog.API | Catalog.API, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToAwaitingValidationIntegrationEvent.OrderStockItems | rename-keep | - | - | Catalog.API, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.BuyerIdentityGuid | rename | WebApp | WebApp | WebApp | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.BuyerIdentityGuid | rename-keep | - | - | WebApp | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.BuyerName | rename | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.BuyerName | rename-keep | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.OrderId | rename | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.OrderStatus | rename | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToCancelledIntegrationEvent.OrderStatus | rename-keep | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.BuyerIdentityGuid | rename | WebApp | WebApp | WebApp | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.BuyerIdentityGuid | rename-keep | - | - | WebApp | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.BuyerName | rename | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.BuyerName | rename-keep | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.OrderId | rename | Webhooks.API | Webhooks.API | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.OrderStatus | rename | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.OrderStatus | rename-keep | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.OrderStockItems | rename | Catalog.API, Webhooks.API | Catalog.API, Webhooks.API | Catalog.API, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToPaidIntegrationEvent.OrderStockItems | rename-keep | - | - | Catalog.API, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.BuyerIdentityGuid | rename | WebApp | WebApp | WebApp | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.BuyerIdentityGuid | rename-keep | - | - | WebApp | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.BuyerName | rename | Webhooks.API | Webhooks.API | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.BuyerName | rename-keep | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.OrderId | rename | Webhooks.API | Webhooks.API | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.OrderStatus | rename | Webhooks.API | Webhooks.API | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToShippedIntegrationEvent.OrderStatus | rename-keep | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.BuyerIdentityGuid | rename | WebApp | WebApp | WebApp | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.BuyerIdentityGuid | rename-keep | - | - | WebApp | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.BuyerName | rename | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.BuyerName | rename-keep | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.OrderId | rename | PaymentProcessor | PaymentProcessor | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.OrderStatus | rename | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToStockConfirmedIntegrationEvent.OrderStatus | rename-keep | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.BuyerIdentityGuid | rename | WebApp | WebApp | WebApp | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.BuyerIdentityGuid | rename-keep | - | - | WebApp | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.BuyerName | rename | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.BuyerName | rename-keep | - | - | WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.OrderId | rename | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, OrderProcessor, PaymentProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.OrderStatus | rename | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStatusChangedToSubmittedIntegrationEvent.OrderStatus | rename-keep | - | - | OrderProcessor, WebApp, Webhooks.API | - |
| Ordering.API:OrderStockItem.ProductId | rename | Catalog.API, Webhooks.API | Catalog.API, Webhooks.API | Basket.API, Catalog.API, WebApp, Webhooks.API | - |
| Ordering.API:OrderStockItem.ProductId | rename-keep | - | - | Basket.API, Catalog.API, WebApp, Webhooks.API | - |
| Ordering.API:OrderStockItem.Units | rename | Catalog.API, Webhooks.API | Catalog.API, Webhooks.API | Catalog.API, Webhooks.API | - |
| Ordering.API:OrderStockItem.Units | rename-keep | - | - | Catalog.API, Webhooks.API | - |
| PaymentProcessor:OrderPaymentFailedIntegrationEvent.OrderId | rename | Ordering.API | Ordering.API | Catalog.API, Ordering.API, OrderProcessor, WebApp, Webhooks.API | - |
| PaymentProcessor:OrderPaymentFailedIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, Ordering.API, OrderProcessor, WebApp, Webhooks.API | - |
| PaymentProcessor:OrderPaymentSucceededIntegrationEvent.OrderId | rename | Ordering.API | Ordering.API | Catalog.API, Ordering.API, OrderProcessor, WebApp, Webhooks.API | - |
| PaymentProcessor:OrderPaymentSucceededIntegrationEvent.OrderId | rename-keep | - | - | Catalog.API, Ordering.API, OrderProcessor, WebApp, Webhooks.API | - |

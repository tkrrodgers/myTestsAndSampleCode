---
type: Code Map
title: Fulfillment delivery-estimate components
description: Maps delivery-estimate knowledge to likely implementation, caller, and tests.
tags: [code-map, fulfillment, delivery-estimate]
status: stable
generated: { by: "human:ces-training-author", at: "2026-09-02T00:00:00Z" }
verified: { by: "human:ces-training-reviewer", at: "2026-09-02T00:00:00Z" }
stale_after: "2027-01-31T00:00:00Z"
sources:
  - id: service-source
    resource: ../../src/Fulfillment.Application/DeliveryEstimateService.cs
    title: Delivery estimate service source
  - id: service-tests
    resource: ../../src/Fulfillment.Application/DeliveryEstimateServiceTests.cs
    title: Delivery estimate service tests
---
# Fulfillment Component Code Map

| Role | Path | Why inspect it |
| --- | --- | --- |
| Primary implementation | `src/Fulfillment.Application/DeliveryEstimateService.cs` | Applies delay data to the estimate |
| Caller / contract impact | `src/Fulfillment.Application/GetOrderStatusHandler.cs` | Returns the estimate to the order-status response |
| Verification | `src/Fulfillment.Application/DeliveryEstimateServiceTests.cs` | Covers changed and unchanged estimates |

This map identifies likely locations; current code inspection must confirm them before a change is proposed.
---
type: Architecture Boundary
title: Fulfillment and shipping service boundaries
description: Ownership boundary between carrier-event ingestion and customer-facing estimates.
tags: [architecture, ownership, fulfillment, shipping]
status: stable
generated: { by: "human:ces-training-author", at: "2026-09-02T00:00:00Z" }
verified: { by: "human:ces-training-reviewer", at: "2026-09-02T00:00:00Z" }
stale_after: "2027-01-31T00:00:00Z"
sources:
  - id: component-map
    resource: ../code-map/fulfillment-components.md
    title: Fulfillment component code map
---
# Service Boundaries

- `Shipping.Infrastructure` ingests and normalizes carrier events.
- `Fulfillment.Application` owns the customer-facing delivery estimate and applies business decisions.
- `Checkout.Web` displays the result but does not calculate or revise the estimate.

Therefore a ticket about estimate calculation should be localized to Fulfillment before inspecting UI formatting code.
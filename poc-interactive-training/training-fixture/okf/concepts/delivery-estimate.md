---
type: Business Concept
title: Delivery estimate
description: Projected customer-facing arrival date for an order.
tags: [fulfillment, delivery, estimate]
status: stable
generated: { by: "human:ces-training-author", at: "2026-09-02T00:00:00Z" }
verified: { by: "human:ces-training-reviewer", at: "2026-09-02T00:00:00Z" }
stale_after: "2027-01-31T00:00:00Z"
sources:
  - id: training-story
    resource: ../../jira/FUL-1842.md
    title: FUL-1842 synthetic training story
---
# Delivery Estimate

A delivery estimate is the projected customer-facing arrival date. It is distinct from order-status formatting and from the raw carrier event.

## Governing Links

- [Service boundaries](../architecture/service-boundaries.md) define who owns the estimate and carrier data.
- [ADR-024](../decisions/adr-024-delay-source.md) defines which event may revise it.
- [Fulfillment component code map](../code-map/fulfillment-components.md) maps the concept to current implementation candidates.

## Known Gap

The API field that communicates the estimate source is not named in this fixture. Ask the API owner; do not invent it.
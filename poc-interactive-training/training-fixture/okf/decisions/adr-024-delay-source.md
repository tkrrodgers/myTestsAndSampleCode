---
type: Decision
title: "ADR-024: Confirmed carrier delay is the revision source"
description: Defines when the delivery estimate may be revised.
tags: [adr, delivery, carrier-delay]
status: stable
generated: { by: "human:ces-training-author", at: "2026-09-02T00:00:00Z" }
verified: { by: "human:ces-training-reviewer", at: "2026-09-02T00:00:00Z" }
stale_after: "2027-01-31T00:00:00Z"
sources:
  - id: training-story
    resource: ../../jira/FUL-1842.md
    title: FUL-1842 synthetic training story
---
# ADR-024

## Decision

Only a **confirmed** carrier-delay event may revise the customer-facing delivery estimate.

## Consequences

- Missing, provisional, or rejected carrier-delay data leaves the original estimate unchanged.
- The result must expose its source, but the field name requires API-owner confirmation.
- Tests must cover confirmed-delay and unchanged-estimate paths.
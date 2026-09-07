---
okf_version: "0.2"
title: Fulfillment Training Knowledge Bundle
description: Entry point for the synthetic FUL-1842 delivery-estimate lesson.
status: stable
generated: { by: "human:ces-training-author", at: "2026-09-02T00:00:00Z" }
verified: { by: "human:ces-training-reviewer", at: "2026-09-07T00:00:00Z" }
stale_after: "2027-01-31T00:00:00Z"
---
# Fulfillment Training Knowledge Bundle

Start here. This index lists the concepts relevant to the synthetic fulfillment system.

## Scope

This bundle covers **one ticket only**: `jira/FUL-1842.md`, the delivery-estimate revision. It is the
context for the guided investigation lesson and nothing else.

Other scenes in the training platform draw on different context sources, which are deliberately **not**
linked from here because they are not part of this task:

- the three trading repositories used by the common-code audit and the context-sufficiency tiers,
- Google Cloud documentation retrieved at run time to judge the model comparison,
- the vendor documentation pack used to make Gemma an SME on crypto execution.

If a task is not FUL-1842, this is the wrong bundle. Say so rather than stretching these artifacts to fit.

## Concepts

- [Delivery estimate](concepts/delivery-estimate.md) — customer-facing projected arrival date and its governing references.

## Supporting Knowledge

- [Service boundaries](architecture/service-boundaries.md) — ownership of carrier events and customer-facing estimates.
- [ADR-024: confirmed delay source](decisions/adr-024-delay-source.md) — decision controlling when an estimate may change.
- [Fulfillment component code map](code-map/fulfillment-components.md) — concept-to-code/test mapping.

## Consumer Rule

Follow the concept links before searching code. Verify mapped files against current source, cite paths, and surface missing knowledge rather than guessing.

The API field that names the estimate source is **intentionally absent** from this bundle. Raising it as an
open question is the correct outcome; inventing a name is the failure this lesson exists to demonstrate.
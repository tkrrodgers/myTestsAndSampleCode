---
type: Architecture
title: Async processing pipeline
description: The ordered stage sequence every options/bond order flows through and how latency is measured.
tags: [pipeline, async, non-functional, metrics]
resource: /src/Pipeline/OrderProcessingPipeline.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
---

# Overview

`OrderProcessingPipeline` runs each `OrderEvent` through an identical, ordered
list of `IOrderStage` implementations. The orchestration is asset-class
agnostic; the instrument-specific behaviour lives entirely in the injected
stages.

# Stage sequence

1. **validate** — `OptionsStrategyValidator`
2. **risk** — `RegTMarginCalculator`
3. **execute** — `YieldToMaturityCalculator`
4. **settle** — `DerivativesClearingService`

A stage returning a failed `StageResult` short-circuits the remaining stages and
marks the order rejected; the terminal state is always persisted.

# Observability

Each stage is timed with `Stopwatch.GetTimestamp()` and recorded into a
Prometheus-style histogram (`stage_latency_ms`) via `MetricsCollector`. Counters
track received, rejected, and terminal filled orders. The whole-pipeline latency
is recorded under the `pipeline_total` stage label.

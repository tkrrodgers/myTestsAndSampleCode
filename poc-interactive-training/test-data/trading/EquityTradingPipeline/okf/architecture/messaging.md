---
type: Architecture
title: Messaging & Kafka topics
description: How the equity service consumes orders off Kafka and commits offsets.
tags: [kafka, messaging, non-functional, pipeline]
resource: /src/Messaging/KafkaOrderConsumer.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: platform-adr
    resource: https://wiki.internal/broker-dealer/adr/0007-kafka-topic-conventions
    title: ADR-0007 Kafka topic naming conventions
---

# Overview

`KafkaOrderConsumer` subscribes to the shared order-execution topics and drives
each order through the processing pipeline. It runs in the consumer group
`order-execution-pipeline`, the same group name every asset-class service uses,
so the technical footprint stays uniform across the platform.

# Topic convention

Topics follow the house convention `orders.<stage>.v1` (see `TopicFor(stage)`),
for example `orders.execute.v1`. The version suffix lets the schema evolve
without breaking existing consumers.

# Delivery semantics

Auto-commit is **disabled** (`EnableAutoCommit = false`). The consumer commits
the offset only *after* the pipeline acknowledges the record, giving
at-least-once delivery: a crash mid-processing replays the record rather than
dropping it. `MaxPollRecords = 500` bounds the in-flight partition; consumer lag
is published as the `kafka_consumer_lag` gauge on every poll.

# Schema

| Field | Meaning |
|-------|---------|
| `ConsumerGroup` | `order-execution-pipeline` (shared across services) |
| `MaxPollRecords` | 500, bounds the in-memory partition |
| `EnableAutoCommit` | false — manual commit after pipeline ack |
| `AutoOffsetReset` | `earliest` |

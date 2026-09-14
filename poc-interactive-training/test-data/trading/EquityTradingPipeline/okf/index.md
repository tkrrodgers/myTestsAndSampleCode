---
okf_version: "0.2"
---

# Equity Trading Pipeline — Knowledge Bundle

Context for the exchange-listed equity order-execution service. This bundle
documents both the shared platform architecture (Kafka / Postgres / async
pipeline) and the equity-specific business logic (order validation, FIFO
tax-lot allocation, Rule 606 routing, and T+1 clearing).

# Architecture

* [Messaging & Kafka topics](architecture/messaging.md) - consumer group, `orders.<stage>.v1` topics, at-least-once commit.
* [Postgres persistence](architecture/persistence.md) - connection pool sizing and order-state upsert.
* [Async processing pipeline](architecture/pipeline.md) - the validate → risk → route → execute → settle stage sequence and per-stage latency metrics.

# Domain

* [Equity order validation](domain/equity-order-validation.md) - order types, LULD price band, odd-lot handling.
* [FIFO tax-lot allocation](domain/fifo-tax-lots.md) - cost-basis method, realized gain/loss, long-term holding.
* [Rule 606 order routing](domain/rule-606-routing.md) - venue selection, maker rebate vs taker fee, disclosure.
* [Clearing & settlement](domain/clearing-settlement.md) - T+1 CNS settlement, Section 31 fee, net money.

---
okf_version: "0.2"
---

# Fixed-Income & Options Engine — Knowledge Bundle

Context for the derivatives and OTC fixed-income order-execution service. This
bundle documents the shared platform architecture (Kafka / Postgres / async
pipeline) and the instrument-specific business logic (multi-leg options
validation, Reg T margin, yield-to-maturity pricing, and instrument-dependent
clearing).

# Architecture

* [Messaging & Kafka topics](architecture/messaging.md) - consumer group, `orders.<stage>.v1` topics, at-least-once commit.
* [Postgres persistence](architecture/persistence.md) - connection pool sizing and order-state upsert.
* [Async processing pipeline](architecture/pipeline.md) - the validate → risk → execute → settle stage sequence and per-stage latency metrics.

# Domain

* [Options strategy validation](domain/options-strategies.md) - vertical spreads, straddles, strangles, and bond well-formedness.
* [Reg T margin](domain/regt-margin.md) - initial margin for long/short options, spreads, and bonds.
* [Yield-to-maturity pricing](domain/yield-to-maturity.md) - Newton-Raphson YTM, accrued interest, dirty price.
* [Clearing & settlement](domain/clearing-settlement.md) - OCC options T+1, Treasury T+1, corporate bond T+2.

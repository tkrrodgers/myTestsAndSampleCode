---
type: Architecture
title: Postgres persistence
description: Connection-pool sizing and order-state upsert semantics for the equity service.
tags: [postgres, persistence, non-functional]
resource: /src/Persistence/OrderRepository.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: platform-adr
    resource: https://wiki.internal/broker-dealer/adr/0011-postgres-pooling
    title: ADR-0011 Postgres connection pooling
---

# Overview

`OrderRepository` persists terminal order state to PostgreSQL. Every asset-class
service ships the same pooling configuration so capacity planning is identical
across the fleet.

# Connection pool

The pool is sized to `MaxPoolSize = 20` connections against
`orders-primary.db.svc:5432`, database `orders`, with a 5-second connection
timeout. A `SemaphoreSlim` bounds concurrent writers to the pool size, matching
the production pool so load tests are representative.

# Write path

On completion the pipeline calls `SaveAsync`, which upserts the order by
`OrderId` into the normalized `orders` table with its terminal status
(`FILLED` / `REJECTED`), the reject reason (if any), and the full stage journal.
The write is idempotent on `OrderId`, so an at-least-once replay overwrites
rather than duplicating.

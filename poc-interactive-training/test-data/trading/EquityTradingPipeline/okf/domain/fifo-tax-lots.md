---
type: Domain
title: FIFO tax-lot allocation
description: First-In-First-Out cost-basis allocation for equity fills and realized gain/loss.
tags: [equities, tax-lots, fifo, cost-basis, 1099-b]
resource: /src/Trading/FifoTaxLotAllocator.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: irs-pub550
    resource: https://www.irs.gov/publications/p550
    title: IRS Publication 550 — Investment Income and Expenses
---

# Overview

`FifoTaxLotAllocator` is the `execute` stage. It fills the order and maintains a
per-client, per-symbol queue of open tax lots keyed as `clientId:symbol`.

# Allocation method

* **Buy** — opens a new tax lot at the execution price and enqueues it. The lot
  records quantity, cost basis per share, and the open timestamp.
* **Sell** — allocated against the **oldest** open lots first (First-In,
  First-Out). For each matched lot the realized gain/loss is
  `(executionPrice − costBasisPerShare) × matchedQuantity`. A partially consumed
  lot is re-enqueued at the front so FIFO ordering is preserved.
* **Holding period** — matched quantity from a lot held ≥ 365 days is tallied as
  long-term; the rest is short-term. This split drives the client's 1099-B.
* **Insufficient lots** — a sell that exceeds the available open quantity is
  rejected (the desk does not create a short position here).

FIFO is the default IRS cost-basis method for equities and is applied
deterministically so year-end reporting is reproducible.

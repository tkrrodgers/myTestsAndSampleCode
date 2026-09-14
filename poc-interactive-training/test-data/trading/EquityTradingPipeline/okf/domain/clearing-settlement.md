---
type: Domain
title: Clearing & settlement
description: T+1 continuous-net-settlement clearing for executed equity trades.
tags: [equities, clearing, settlement, dtcc, t+1]
resource: /src/Trading/EquityClearingService.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: t1-rule
    resource: https://www.sec.gov/rules/final/2023/34-96930.pdf
    title: SEC shortening of the standard settlement cycle to T+1
---

# Overview

`EquityClearingService` is the `settle` stage. It clears executed trades through
DTCC/NSCC continuous net settlement (CNS) and computes the money legs.

# Settlement mechanics

* **Settlement date** — one business day after trade date (**T+1**), skipping
  weekends via `AddBusinessDays`.
* **Principal** — `price × quantity`.
* **Section 31 fee** — applied on the **sell** side only, at the current
  per-dollar rate, and netted into the money owed/received.
* **Net money** — buys pay principal plus venue fee; sells receive principal
  less the Section 31 fee, adjusted for the venue fee/rebate.
* **Clearing corp** — recorded as `DTCC/NSCC-CNS`, with the settlement date
  stamped onto the order state.

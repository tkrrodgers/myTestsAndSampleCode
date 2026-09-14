---
type: Domain
title: Rule 606 order routing
description: Venue selection for equity orders and the routing decision captured for SEC Rule 606 disclosure.
tags: [equities, routing, rule-606, best-execution, pfof]
resource: /src/Trading/Rule606OrderRouter.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: rule-606
    resource: https://www.sec.gov/rules/final/34-84528.pdf
    title: SEC Rule 606 order-routing disclosure
---

# Overview

`Rule606OrderRouter` is the `route` stage. It selects an execution venue and
journals the routing decision so the quarterly Rule 606 report can attribute
order flow and any payment-for-order-flow by venue.

# Venue selection

The router evaluates a fixed venue set (`ARCA`, `NSDQ`, `EDGX`, `IEX`), each with
a taker fee, maker rebate, and a relative liquidity weight.

* **Marketable orders** (MARKET, or a limit marked marketable) route to the venue
  with the lowest effective taker cost after a small liquidity-based
  price-improvement adjustment — a simplified best-execution model.
* **Resting limit orders** route to the venue paying the highest maker rebate.

The chosen venue, a human-readable rationale, and the estimated venue fee (a
negative number when a rebate is earned) are written to the order state and
journaled for disclosure.

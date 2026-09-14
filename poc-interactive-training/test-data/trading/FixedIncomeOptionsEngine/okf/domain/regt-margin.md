---
type: Domain
title: Reg T margin
description: Regulation T initial margin for long/short options, defined-risk spreads, and bonds.
tags: [margin, reg-t, options, bonds, risk]
resource: /src/Trading/RegTMarginCalculator.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: reg-t
    resource: https://www.federalreserve.gov/supervisionreg/regtcg.htm
    title: Federal Reserve Regulation T
---

# Overview

`RegTMarginCalculator` is the `risk` stage. It computes the initial margin
requirement and rejects the order if it exceeds the account `buyingPower`.

# Option margin

* **Long options** — paid for in full; margin equals
  `premium × contracts × 100` (no margin loan on listed long options).
* **Short naked options** — the standard Reg T formula:
  `max(20% × underlying − OTM amount, 10% × underlying) + premium`, per contract
  (×100 multiplier). The out-of-the-money amount is `max(0, strike − underlying)`.
* **Vertical spreads** — defined risk: margin equals the **maximum loss**,
  `max(0, strikeWidth − netCredit) × contracts × 100`.

# Bond margin

Notional is `price/100 × faceValue × quantity`. U.S. Treasuries
(`issuerType = TREASURY`) take a 1% haircut; corporate bonds require 10%.

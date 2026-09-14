---
type: Domain
title: Equity order validation
description: Exchange rules enforced on listed-equity orders before they consume risk or routing capacity.
tags: [equities, validation, luld, order-types]
resource: /src/Trading/EquityOrderValidator.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
sources:
  - id: luld-plan
    resource: https://www.sec.gov/rules/sro/nms/2012/34-67091.pdf
    title: National Market System Limit Up-Limit Down plan
---

# Overview

`EquityOrderValidator` is the `validate` stage. It accepts only exchange-listed
equity orders and rejects anything structurally invalid before it consumes
downstream capacity.

# Rules enforced

* **Symbol** — 1–5 characters (listed-equity ticker shape).
* **Order type** — one of `MARKET`, `LIMIT`, `STOP`. Priced types (`LIMIT`,
  `STOP`) must carry a positive trigger price.
* **Quantity** — a positive whole number of shares (fractional shares are
  rejected at this venue).
* **LULD band** — for priced orders with a reference price, the limit price must
  fall within ±10% of the reference price, approximating the Limit Up-Limit Down
  price band. Orders outside the band are rejected.
* **Odd lots** — orders whose quantity is not a multiple of 100 are flagged
  (`oddLot = true`) for downstream routing but not rejected.

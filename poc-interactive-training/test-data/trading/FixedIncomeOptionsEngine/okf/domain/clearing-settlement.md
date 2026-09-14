---
type: Domain
title: Clearing & settlement
description: Instrument-dependent clearing and settlement cycles for options and bonds.
tags: [clearing, settlement, occ, dtcc, fedwire]
resource: /src/Trading/DerivativesClearingService.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
---

# Overview

`DerivativesClearingService` is the `settle` stage. The clearing venue and
settlement cycle **vary by instrument type**.

# Settlement matrix

| Instrument | Clearer | Cycle |
|------------|---------|-------|
| Listed option | OCC | T+1 |
| U.S. Treasury | Fedwire | T+1 |
| Corporate bond | DTCC | T+2 |

* **Options** — net money is `premium × contracts × 100`.
* **Bonds** — net money is `dirtyPrice/100 × faceValue × quantity`, using the
  dirty price computed by the pricing stage.

The settlement date is computed with `AddBusinessDays` (weekends skipped) and
stamped onto the order state along with the clearing corporation.

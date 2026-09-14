---
type: Domain
title: Options strategy validation
description: Structural validation of multi-leg options strategies and OTC bond well-formedness.
tags: [options, multi-leg, spreads, straddle, bonds, validation]
resource: /src/Trading/OptionsStrategyValidator.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
---

# Overview

`OptionsStrategyValidator` is the `validate` stage. It dispatches on
`assetClass` (`OPTION` or `BOND`) and rejects structurally impossible orders
before they reach margin or pricing.

# Options strategies

The declared `strategy` must be internally consistent across its legs:

* **SINGLE** — one leg with a valid right (`CALL`/`PUT`) and positive strike.
* **VERTICAL_SPREAD** — two legs, **same right and expiry**, **different
  strikes**.
* **STRADDLE** — a call and a put at the **same strike and same expiry**.
* **STRANGLE** — a call and a put at the **same expiry** but **different
  strikes**.

Legs are supplied via `right1/right2`, `strike1/strike2`, and `expiry1/expiry2`
attributes.

# Bond well-formedness

Bond orders require a parseable `maturityDate`, a non-negative `couponRate`, and
a positive `faceValue` (default 1000). Malformed bonds are rejected here.

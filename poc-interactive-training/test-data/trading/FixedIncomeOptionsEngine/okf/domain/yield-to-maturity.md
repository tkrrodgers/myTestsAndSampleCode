---
type: Domain
title: Yield-to-maturity pricing
description: Newton-Raphson YTM solve, accrued interest, and dirty price for OTC bonds.
tags: [bonds, yield-to-maturity, newton-raphson, accrued-interest, pricing]
resource: /src/Trading/YieldToMaturityCalculator.cs
status: stable
generated: { by: human:architect, at: 2026-09-06T12:00:00Z }
verified: { by: human:architect, at: 2026-09-06T12:00:00Z }
---

# Overview

`YieldToMaturityCalculator` is the `execute` stage. For bonds it prices the leg;
for options it decomposes premium into intrinsic and extrinsic value.

# Bond pricing

Given the clean price, coupon rate, coupon frequency, and remaining periods, the
stage solves **yield-to-maturity** by **Newton-Raphson** iteration on the
present-value function (up to 100 iterations, convergence at 1e-8). It then
computes **accrued interest** on a 30E/360 basis from `daysSinceLastCoupon` and
returns the **dirty price** = clean price + accrued. Non-convergence is rejected.

# Option decomposition

For options, intrinsic value is `max(0, underlying − strike)` for calls and
`max(0, strike − underlying)` for puts; extrinsic (time) value is
`max(0, premium − intrinsic)`. Both are stamped onto the order state.

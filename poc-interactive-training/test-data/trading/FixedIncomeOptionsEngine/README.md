# Fixed-Income & Options Engine

A broker-dealer service that processes **multi-leg listed options** and **OTC
fixed-income (bond)** orders through an asynchronous execution pipeline.

## What it does (functional)

- **Validation** — multi-leg strategy consistency (vertical spreads, straddles, strangles) and bond well-formedness.
- **Risk** — **Reg T** initial margin for long/short options, defined-risk spreads, and bonds.
- **Execution** — **yield-to-maturity** (Newton-Raphson), accrued interest, dirty price; option intrinsic/extrinsic decomposition.
- **Clearing** — instrument-dependent: OCC options **T+1**, Treasury **T+1**, corporate bond **T+2**.

## Architecture (non-functional)

Spring-Boot-style bootstrap, asynchronous Kafka consumer (`orders.<stage>.v1`,
at-least-once), Prometheus-style per-stage latency metrics, and a pooled
PostgreSQL order-state store — identical to the platform's other asset-class
services.

## Run

```powershell
dotnet run
```

## Knowledge

See the OKF bundle in [`okf/`](okf/index.md).

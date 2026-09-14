# Equity Trading Pipeline

A broker-dealer service that processes exchange-listed **equity** orders
(market / limit / stop) through an asynchronous execution pipeline.

## What it does (functional)

- **Validation** — order-type, quantity, and Limit Up-Limit Down (LULD) price-band checks.
- **Routing** — venue selection with SEC **Rule 606** disclosure capture.
- **Execution** — **FIFO tax-lot** allocation with realized gain/loss and long-term/short-term split.
- **Clearing** — **T+1** DTCC/NSCC continuous net settlement, Section 31 fee.

## Architecture (non-functional)

Spring-Boot-style bootstrap, asynchronous Kafka consumer (`orders.<stage>.v1`,
at-least-once), Prometheus-style per-stage latency metrics, and a pooled
PostgreSQL order-state store. This technical footprint is intentionally
identical to the platform's other asset-class services.

## Run

```powershell
dotnet run
```

## Knowledge

See the OKF bundle in [`okf/`](okf/index.md).

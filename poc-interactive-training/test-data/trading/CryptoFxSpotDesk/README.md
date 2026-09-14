# Crypto & FX Spot Desk

A broker-dealer service that executes **spot cryptocurrency** and **foreign
exchange** trades around the clock.

## What it does (functional)

- **Validation** — trading-pair and minimum-notional checks for FX and crypto.
- **AML wallet screening** — sanctions/OFAC address screening, on-chain risk scoring, and enhanced-due-diligence escalation.
- **Liquidity aggregation** — composite top-of-book from multiple streaming liquidity providers with slippage estimation.
- **Instant settlement** — atomic, immediate dual-leg settlement (no T+N cycle).

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

> Note: this repository's OKF bundle is intentionally shallow relative to the
> code, and is used by the POC to demonstrate low context-authenticity scoring.

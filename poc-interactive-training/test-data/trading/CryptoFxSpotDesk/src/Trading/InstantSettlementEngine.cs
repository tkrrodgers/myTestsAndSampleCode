using System.Globalization;
using CryptoFxSpotDesk.Pipeline;

namespace CryptoFxSpotDesk.Trading;

/// <summary>
/// Settles spot FX and crypto trades instantly. There is no T+1/T+2 cycle: the
/// two currency legs are exchanged atomically at execution time. For crypto the
/// asset leg is moved on-chain (or booked against an omnibus hot-wallet ledger)
/// and the fiat/stable leg is debited in the same atomic step; for FX the two
/// currency ledgers are updated together. Settlement finality is immediate, so
/// this stage performs an atomic debit/credit against the internal ledger and
/// records the settlement as complete with a settlement timestamp equal to the
/// execution instant.
/// </summary>
public sealed class InstantSettlementEngine : IOrderStage
{
    private readonly object _ledgerGate = new();
    private readonly Dictionary<string, decimal> _ledger = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "settle";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var executionPrice = decimal.TryParse(
            context.State.GetValueOrDefault("executionPrice"),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var px)
            ? px
            : order.LimitPrice ?? order.AttrDecimal("indicativePrice");

        if (executionPrice <= 0)
        {
            return Task.FromResult(StageResult.Fail("no execution price to settle against"));
        }

        var baseCcy = context.State.GetValueOrDefault("baseCurrency", "BTC");
        var quoteCcy = context.State.GetValueOrDefault("quoteCurrency", "USD");
        var baseAmount = order.Quantity;
        var quoteAmount = order.Quantity * executionPrice;

        lock (_ledgerGate)
        {
            // Atomic dual-leg settlement — both legs move together or not at all.
            if (order.Side == OrderSide.Buy)
            {
                Credit(order.ClientId, baseCcy, baseAmount);
                Debit(order.ClientId, quoteCcy, quoteAmount);
            }
            else
            {
                Debit(order.ClientId, baseCcy, baseAmount);
                Credit(order.ClientId, quoteCcy, quoteAmount);
            }
        }

        var settledAt = DateTimeOffset.UtcNow;
        context.State["settlementType"] = "INSTANT_ATOMIC";
        context.State["settlementDate"] = settledAt.ToString("O");
        context.State["settledBase"] = $"{baseAmount:F8} {baseCcy}";
        context.State["settledQuote"] = $"{quoteAmount:F2} {quoteCcy}";
        context.Record("settle", $"instant atomic settlement {baseAmount:F6} {baseCcy} vs {quoteAmount:F2} {quoteCcy}");
        return Task.FromResult(StageResult.Ok());
    }

    private void Credit(string account, string currency, decimal amount)
    {
        var key = $"{account}:{currency}";
        _ledger[key] = _ledger.GetValueOrDefault(key) + amount;
    }

    private void Debit(string account, string currency, decimal amount)
    {
        var key = $"{account}:{currency}";
        _ledger[key] = _ledger.GetValueOrDefault(key) - amount;
    }
}

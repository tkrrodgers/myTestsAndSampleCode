using CryptoFxSpotDesk.Pipeline;

namespace CryptoFxSpotDesk.Trading;

/// <summary>
/// Validates spot FX and cryptocurrency orders. Unlike an equity venue there is
/// no market-session gate: the desk quotes and trades continuously, 24 hours a
/// day, 7 days a week, including weekends and holidays, so no calendar or
/// trading-hours check is applied. Validation enforces a well-formed
/// base/quote currency pair, a minimum notional per pair, and — for crypto
/// withdrawals — a syntactically valid destination wallet address.
/// </summary>
public sealed class SpotFxOrderValidator : IOrderStage
{
    private static readonly Dictionary<string, decimal> MinNotionalByQuote = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 10m,
        ["USDT"] = 10m,
        ["USDC"] = 10m,
        ["EUR"] = 10m,
        ["BTC"] = 0.0001m,
    };

    public string Name => "validate";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var parts = order.Symbol.Split(['/', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return Task.FromResult(StageResult.Fail($"invalid trading pair '{order.Symbol}' (expected BASE/QUOTE)"));
        }

        var (baseCcy, quoteCcy) = (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
        if (order.Quantity <= 0)
        {
            return Task.FromResult(StageResult.Fail("quantity must be positive"));
        }

        var price = order.LimitPrice ?? order.AttrDecimal("indicativePrice");
        var notional = price * order.Quantity;
        var minNotional = MinNotionalByQuote.GetValueOrDefault(quoteCcy, 1m);
        if (notional > 0 && notional < minNotional)
        {
            return Task.FromResult(StageResult.Fail(
                $"notional {notional} {quoteCcy} below minimum {minNotional} {quoteCcy}"));
        }

        var isCrypto = order.Attr("assetClass").Equals("CRYPTO", StringComparison.OrdinalIgnoreCase);
        if (isCrypto && order.Side == OrderSide.Sell)
        {
            var wallet = order.Attr("destinationWallet");
            if (wallet.Length is < 26 or > 62)
            {
                return Task.FromResult(StageResult.Fail("crypto withdrawal requires a valid destination wallet address"));
            }
        }

        context.State["baseCurrency"] = baseCcy;
        context.State["quoteCurrency"] = quoteCcy;
        context.State["tradingWindow"] = "24x7";
        context.Record("validate", $"validated {baseCcy}/{quoteCcy} (24x7 continuous session)");
        return Task.FromResult(StageResult.Ok());
    }
}

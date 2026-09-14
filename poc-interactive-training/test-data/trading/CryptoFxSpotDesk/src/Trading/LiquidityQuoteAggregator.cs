using System.Globalization;
using CryptoFxSpotDesk.Pipeline;

namespace CryptoFxSpotDesk.Trading;

/// <summary>
/// Aggregates streaming quotes from multiple liquidity providers and selects the
/// best executable price for the order's side. The desk maintains a continuous,
/// two-sided book from each LP; this stage builds a composite top-of-book, skips
/// stale quotes (older than the freshness window), applies the client spread
/// markup, and computes expected slippage against available depth. Because
/// liquidity is streamed continuously the chosen quote is a point-in-time
/// snapshot valid only for the immediate instant settlement that follows.
/// </summary>
public sealed class LiquidityQuoteAggregator : IOrderStage
{
    private sealed record ProviderQuote(string Provider, decimal Bid, decimal Ask, decimal Depth, TimeSpan Age);

    private const decimal ClientSpreadMarkupBps = 5m;   // 5 bps added to raw LP spread
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMilliseconds(750);

    public string Name => "route";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var mid = order.LimitPrice ?? order.AttrDecimal("indicativePrice");
        if (mid <= 0)
        {
            return Task.FromResult(StageResult.Fail("no indicative price to build a composite book"));
        }

        var quotes = SimulateProviderStream(mid)
            .Where(q => q.Age <= FreshnessWindow)
            .ToList();
        if (quotes.Count == 0)
        {
            return Task.FromResult(StageResult.Fail("no fresh liquidity available in freshness window"));
        }

        ProviderQuote best;
        decimal executionPrice;
        if (order.Side == OrderSide.Buy)
        {
            best = quotes.MinBy(q => q.Ask)!;
            executionPrice = best.Ask * (1 + ClientSpreadMarkupBps / 10_000m);
        }
        else
        {
            best = quotes.MaxBy(q => q.Bid)!;
            executionPrice = best.Bid * (1 - ClientSpreadMarkupBps / 10_000m);
        }

        var slippage = best.Depth >= order.Quantity
            ? 0m
            : (order.Quantity - best.Depth) / order.Quantity * (best.Ask - best.Bid);

        context.State["liquidityProvider"] = best.Provider;
        context.State["executionPrice"] = executionPrice.ToString("F8", CultureInfo.InvariantCulture);
        context.State["expectedSlippage"] = slippage.ToString("F8", CultureInfo.InvariantCulture);
        context.Record("route", $"best LP {best.Provider} @ {executionPrice:F6}, slippage {slippage:F6}");
        return Task.FromResult(StageResult.Ok());
    }

    private static IEnumerable<ProviderQuote> SimulateProviderStream(decimal mid)
    {
        // Deterministic synthetic composite book around the mid.
        yield return new ProviderQuote("LP-CUMBERLAND", mid * 0.9995m, mid * 1.0004m, 5m, TimeSpan.FromMilliseconds(120));
        yield return new ProviderQuote("LP-B2C2", mid * 0.9994m, mid * 1.0005m, 12m, TimeSpan.FromMilliseconds(300));
        yield return new ProviderQuote("LP-JUMP", mid * 0.9996m, mid * 1.0003m, 3m, TimeSpan.FromMilliseconds(90));
        yield return new ProviderQuote("LP-STALE", mid * 0.9990m, mid * 1.0001m, 50m, TimeSpan.FromMilliseconds(2000));
    }
}

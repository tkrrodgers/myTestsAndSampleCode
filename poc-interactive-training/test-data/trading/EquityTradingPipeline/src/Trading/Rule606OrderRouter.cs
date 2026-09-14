using EquityTradingPipeline.Pipeline;

namespace EquityTradingPipeline.Trading;

/// <summary>
/// Routes accepted equity orders to an execution venue and captures the routing
/// decision required for SEC Rule 606 order-routing disclosure. Marketable
/// orders are sent to the venue offering the best displayed price after adjusting
/// for access fees and rebates (a simplified price-improvement model); resting
/// limit orders are posted to the venue paying the highest maker rebate. Every
/// decision is journaled so the quarterly Rule 606 report can attribute order
/// flow and payment-for-order-flow by venue.
/// </summary>
public sealed class Rule606OrderRouter : IOrderStage
{
    private sealed record Venue(string Code, decimal TakerFeePerShare, decimal MakerRebatePerShare, decimal Liquidity);

    private static readonly Venue[] Venues =
    {
        new("ARCA", 0.0030m, 0.0020m, 0.35m),
        new("NSDQ", 0.0030m, 0.0025m, 0.30m),
        new("EDGX", 0.0029m, 0.0021m, 0.15m),
        new("IEX", 0.0009m, 0.0000m, 0.20m),
    };

    public string Name => "route";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var marketable = order.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase)
            || (context.State.TryGetValue("marketable", out var flag) && flag == "true");

        Venue chosen;
        string rationale;
        if (marketable)
        {
            // Minimize effective cost = taker fee net of any price improvement.
            chosen = Venues.MinBy(v => v.TakerFeePerShare - v.Liquidity * 0.0001m)!;
            rationale = "best-execution taker route";
        }
        else
        {
            // Resting liquidity: maximize maker rebate.
            chosen = Venues.MaxBy(v => v.MakerRebatePerShare)!;
            rationale = "maker-rebate posting route";
        }

        var estimatedFee = marketable
            ? chosen.TakerFeePerShare * order.Quantity
            : -chosen.MakerRebatePerShare * order.Quantity;

        context.State["venue"] = chosen.Code;
        context.State["routingRationale"] = rationale;
        context.State["estimatedVenueFee"] = estimatedFee.ToString("F4");
        context.Record(Name, $"routed to {chosen.Code} ({rationale}), est fee {estimatedFee:F4}");
        return Task.FromResult(StageResult.Ok());
    }
}

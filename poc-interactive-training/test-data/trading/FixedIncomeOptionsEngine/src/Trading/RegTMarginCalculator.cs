using FixedIncomeOptionsEngine.Pipeline;

namespace FixedIncomeOptionsEngine.Trading;

/// <summary>
/// Computes Regulation T initial margin and verifies the account has sufficient
/// buying power. Long options are paid for in full (no margin loan permitted on
/// listed long options). Short naked options use the standard 20%-of-underlying
/// less out-of-the-money amount, floored at 10% of underlying, plus premium.
/// Defined-risk vertical spreads margin at the maximum loss (strike width less
/// net credit). Bonds use the Reg T maintenance schedule: U.S. Treasuries are
/// treated as exempt (nominal haircut), corporates require a larger margin.
/// </summary>
public sealed class RegTMarginCalculator : IOrderStage
{
    public string Name => "risk";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var assetClass = context.State.GetValueOrDefault("assetClass", order.Attr("assetClass").ToUpperInvariant());
        var buyingPower = order.AttrDecimal("buyingPower", decimal.MaxValue);

        var margin = assetClass == "BOND"
            ? BondMargin(order)
            : OptionMargin(order, context);

        context.State["initialMargin"] = margin.ToString("F2");
        if (margin > buyingPower)
        {
            return Task.FromResult(StageResult.Fail(
                $"Reg T initial margin {margin:C} exceeds buying power {buyingPower:C}"));
        }

        context.Record("risk", $"Reg T initial margin {margin:C} within buying power");
        return Task.FromResult(StageResult.Ok());
    }

    private static decimal OptionMargin(OrderEvent order, OrderContext context)
    {
        const decimal contractMultiplier = 100m;
        var premium = order.LimitPrice ?? 0m;
        var underlying = order.AttrDecimal("underlyingPrice");
        var strike = order.AttrDecimal("strike1");
        var contracts = order.Quantity;
        var strategy = context.State.GetValueOrDefault("strategy", "SINGLE");

        if (order.Side == OrderSide.Buy)
        {
            // Long premium is paid in full.
            return premium * contracts * contractMultiplier;
        }

        if (strategy == "VERTICAL_SPREAD")
        {
            var width = Math.Abs(order.AttrDecimal("strike1") - order.AttrDecimal("strike2"));
            var netCredit = premium;
            var maxLoss = Math.Max(0m, width - netCredit);
            return maxLoss * contracts * contractMultiplier;
        }

        // Short naked: max(20% underlying - OTM, 10% underlying) + premium.
        var otm = Math.Max(0m, strike - underlying);
        var method1 = underlying * 0.20m - otm + premium;
        var method2 = underlying * 0.10m + premium;
        return Math.Max(method1, method2) * contracts * contractMultiplier;
    }

    private static decimal BondMargin(OrderEvent order)
    {
        var price = order.LimitPrice ?? 100m; // clean price per 100 face
        var face = order.AttrDecimal("faceValue", 1000m);
        var notional = price / 100m * face * order.Quantity;
        var isTreasury = order.Attr("issuerType").Equals("TREASURY", StringComparison.OrdinalIgnoreCase);
        var marginRate = isTreasury ? 0.01m : 0.10m;
        return notional * marginRate;
    }
}

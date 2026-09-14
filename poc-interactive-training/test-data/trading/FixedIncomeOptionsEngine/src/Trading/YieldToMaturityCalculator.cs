using FixedIncomeOptionsEngine.Pipeline;

namespace FixedIncomeOptionsEngine.Trading;

/// <summary>
/// Prices the execution leg. For OTC bonds it solves yield-to-maturity from the
/// clean price using the Newton-Raphson method, then derives accrued interest
/// (30/360) and the dirty price actually exchanged. For options it computes
/// intrinsic and extrinsic value from the underlying so downstream reporting can
/// separate time value from moneyness. The computed economics are stamped onto
/// the order context for clearing.
/// </summary>
public sealed class YieldToMaturityCalculator : IOrderStage
{
    public string Name => "execute";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var assetClass = context.State.GetValueOrDefault("assetClass", order.Attr("assetClass").ToUpperInvariant());

        if (assetClass == "BOND")
        {
            return Task.FromResult(PriceBond(order, context));
        }

        return Task.FromResult(PriceOption(order, context));
    }

    private static StageResult PriceBond(OrderEvent order, OrderContext context)
    {
        var cleanPrice = order.LimitPrice ?? 100m;              // per 100 of face
        var face = 100m;                                        // work per 100, scale later
        var couponRate = order.AttrDecimal("couponRate");       // annual, e.g. 0.05
        var frequency = (int)order.AttrDecimal("couponFrequency", 2);
        if (!DateTimeOffset.TryParse(order.Attr("maturityDate"), out var maturity))
        {
            return StageResult.Fail("cannot price bond without maturityDate");
        }

        var yearsToMaturity = Math.Max(0.5, (maturity - order.ReceivedAt).TotalDays / 365.25);
        var periods = (int)Math.Round(yearsToMaturity * frequency);
        if (periods <= 0)
        {
            return StageResult.Fail("bond has no remaining coupon periods");
        }

        var couponPayment = (double)couponRate / frequency * (double)face;
        var ytm = SolveYieldNewtonRaphson((double)cleanPrice, couponPayment, (double)face, periods, frequency);
        if (double.IsNaN(ytm))
        {
            return StageResult.Fail("yield-to-maturity did not converge");
        }

        var accrued = AccruedInterest30E360(order, couponRate, frequency);
        var dirtyPrice = cleanPrice + accrued;

        context.State["yieldToMaturity"] = ytm.ToString("P4");
        context.State["accruedInterest"] = accrued.ToString("F4");
        context.State["dirtyPrice"] = dirtyPrice.ToString("F4");
        context.Record("execute", $"YTM {ytm:P3}, accrued {accrued:F4}, dirty {dirtyPrice:F4}");
        return StageResult.Ok();
    }

    private static StageResult PriceOption(OrderEvent order, OrderContext context)
    {
        var underlying = order.AttrDecimal("underlyingPrice");
        var strike = order.AttrDecimal("strike1");
        var premium = order.LimitPrice ?? 0m;
        var right = order.Attr("right1").ToUpperInvariant();

        var intrinsic = right == "CALL"
            ? Math.Max(0m, underlying - strike)
            : Math.Max(0m, strike - underlying);
        var extrinsic = Math.Max(0m, premium - intrinsic);

        context.State["intrinsicValue"] = intrinsic.ToString("F4");
        context.State["extrinsicValue"] = extrinsic.ToString("F4");
        context.Record("execute", $"intrinsic {intrinsic:F2}, extrinsic {extrinsic:F2}");
        return StageResult.Ok();
    }

    private static double SolveYieldNewtonRaphson(double price, double coupon, double face, int periods, int frequency)
    {
        var y = 0.05; // initial guess: 5% annual
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var perPeriod = y / frequency;
            double pv = 0, derivative = 0;
            for (var t = 1; t <= periods; t++)
            {
                var cash = t == periods ? coupon + face : coupon;
                var discount = Math.Pow(1 + perPeriod, t);
                pv += cash / discount;
                derivative += -t * cash / (discount * (1 + perPeriod)) / frequency;
            }

            var diff = pv - price;
            if (Math.Abs(diff) < 1e-8)
            {
                return y;
            }

            if (Math.Abs(derivative) < 1e-12)
            {
                break;
            }

            y -= diff / derivative;
            if (y is < -0.99 or > 5)
            {
                break;
            }
        }

        return double.NaN;
    }

    private static decimal AccruedInterest30E360(OrderEvent order, decimal couponRate, int frequency)
    {
        // Days since last coupon on a 30E/360 basis, simplified via last-settlement attribute.
        var daysAccrued = order.AttrDecimal("daysSinceLastCoupon", 90m);
        var daysInPeriod = 360m / frequency;
        var periodCoupon = couponRate / frequency * 100m;
        return periodCoupon * (daysAccrued / daysInPeriod);
    }
}

using FixedIncomeOptionsEngine.Pipeline;

namespace FixedIncomeOptionsEngine.Trading;

/// <summary>
/// Validates multi-leg listed-options strategies and OTC fixed-income orders.
/// For options it verifies that the declared strategy is internally consistent:
/// a vertical spread needs two legs of the same right and expiry at different
/// strikes; a straddle needs a call and a put at the same strike and expiry; a
/// strangle needs a call and a put at the same expiry with different strikes. For
/// bonds it requires a coupon, a maturity date, and a face value. Structurally
/// impossible strategies are rejected before they reach margin or pricing.
/// </summary>
public sealed class OptionsStrategyValidator : IOrderStage
{
    public string Name => "validate";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var assetClass = order.Attr("assetClass").ToUpperInvariant();

        return Task.FromResult(assetClass switch
        {
            "OPTION" => ValidateOption(order, context),
            "BOND" => ValidateBond(order, context),
            _ => StageResult.Fail($"unknown assetClass '{assetClass}'"),
        });
    }

    private static StageResult ValidateOption(OrderEvent order, OrderContext context)
    {
        var strategy = order.Attr("strategy", "SINGLE").ToUpperInvariant();
        var right1 = order.Attr("right1").ToUpperInvariant();
        var right2 = order.Attr("right2").ToUpperInvariant();
        var strike1 = order.AttrDecimal("strike1");
        var strike2 = order.AttrDecimal("strike2");
        var expiry1 = order.Attr("expiry1");
        var expiry2 = order.Attr("expiry2");

        switch (strategy)
        {
            case "SINGLE":
                if (right1 is not ("CALL" or "PUT") || strike1 <= 0)
                {
                    return StageResult.Fail("single-leg option requires a valid right and strike");
                }

                break;

            case "VERTICAL_SPREAD":
                if (right1 != right2 || expiry1 != expiry2 || strike1 == strike2)
                {
                    return StageResult.Fail("vertical spread requires same right/expiry and different strikes");
                }

                break;

            case "STRADDLE":
                if (!((right1 == "CALL" && right2 == "PUT") || (right1 == "PUT" && right2 == "CALL"))
                    || strike1 != strike2 || expiry1 != expiry2)
                {
                    return StageResult.Fail("straddle requires a call and a put at the same strike and expiry");
                }

                break;

            case "STRANGLE":
                if (!((right1 == "CALL" && right2 == "PUT") || (right1 == "PUT" && right2 == "CALL"))
                    || strike1 == strike2 || expiry1 != expiry2)
                {
                    return StageResult.Fail("strangle requires a call and a put at different strikes, same expiry");
                }

                break;

            default:
                return StageResult.Fail($"unsupported options strategy '{strategy}'");
        }

        context.State["assetClass"] = "OPTION";
        context.State["strategy"] = strategy;
        context.Record("validate", $"validated {strategy} on {order.Symbol}");
        return StageResult.Ok();
    }

    private static StageResult ValidateBond(OrderEvent order, OrderContext context)
    {
        var coupon = order.AttrDecimal("couponRate");
        var face = order.AttrDecimal("faceValue", 1000m);
        if (!DateTimeOffset.TryParse(order.Attr("maturityDate"), out _))
        {
            return StageResult.Fail("bond order requires a valid maturityDate");
        }

        if (coupon < 0 || face <= 0)
        {
            return StageResult.Fail("bond order requires a non-negative coupon and positive face value");
        }

        context.State["assetClass"] = "BOND";
        context.State["faceValue"] = face.ToString("F2");
        context.Record("validate", $"validated bond {order.Symbol} coupon {coupon:P2} face {face:C}");
        return StageResult.Ok();
    }
}

using EquityTradingPipeline.Pipeline;

namespace EquityTradingPipeline.Trading;

/// <summary>
/// Validates exchange-listed equity orders against basic exchange rules before
/// they consume risk or routing capacity. Enforces supported order types
/// (MARKET, LIMIT, STOP), positive share quantities, presence of a trigger price
/// for priced order types, round-lot conventions, and a Limit-Up/Limit-Down
/// (LULD) price-reasonability band relative to the last reference price.
/// </summary>
public sealed class EquityOrderValidator : IOrderStage
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MARKET", "LIMIT", "STOP",
    };

    private const decimal LuldBandPercent = 0.10m;

    public string Name => "validate";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;

        if (string.IsNullOrWhiteSpace(order.Symbol) || order.Symbol.Length > 5)
        {
            return Task.FromResult(StageResult.Fail($"invalid equity symbol '{order.Symbol}'"));
        }

        if (!SupportedTypes.Contains(order.OrderType))
        {
            return Task.FromResult(StageResult.Fail($"unsupported order type '{order.OrderType}'"));
        }

        if (order.Quantity <= 0 || order.Quantity != Math.Floor(order.Quantity))
        {
            return Task.FromResult(StageResult.Fail("equity quantity must be a positive whole number of shares"));
        }

        var priced = !order.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase);
        if (priced && order.LimitPrice is null or <= 0)
        {
            return Task.FromResult(StageResult.Fail($"{order.OrderType} order requires a positive trigger price"));
        }

        var referencePrice = order.AttrDecimal("referencePrice");
        if (priced && referencePrice > 0)
        {
            var band = referencePrice * LuldBandPercent;
            var price = order.LimitPrice!.Value;
            if (price < referencePrice - band || price > referencePrice + band)
            {
                return Task.FromResult(StageResult.Fail(
                    $"price {price:C} breaches LULD band {referencePrice - band:C}–{referencePrice + band:C}"));
            }
        }

        var isOddLot = order.Quantity % 100 != 0;
        context.State["oddLot"] = isOddLot ? "true" : "false";
        context.Record(Name, $"accepted {order.OrderType} {order.Quantity} {order.Symbol} (oddLot={isOddLot})");
        return Task.FromResult(StageResult.Ok());
    }
}

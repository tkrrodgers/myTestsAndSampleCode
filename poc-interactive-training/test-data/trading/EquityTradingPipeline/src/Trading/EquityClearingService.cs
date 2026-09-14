using EquityTradingPipeline.Pipeline;

namespace EquityTradingPipeline.Trading;

/// <summary>
/// Clears and settles executed equity trades. Computes the standard T+1
/// settlement date (skipping weekends), the net money to move at DTCC, and the
/// continuous-net-settlement (CNS) obligation. Equities in the U.S. settle one
/// business day after trade date; this stage finalizes the money and security
/// legs and stamps the settlement date on the order.
/// </summary>
public sealed class EquityClearingService : IOrderStage
{
    private const decimal SecFeePerDollar = 0.0000278m; // Section 31 fee, sell side.

    public string Name => "settle";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var price = order.LimitPrice ?? order.AttrDecimal("referencePrice");
        var principal = price * order.Quantity;
        var settlementDate = AddBusinessDays(order.ReceivedAt.Date, 1);

        var secFee = order.Side == OrderSide.Sell ? Math.Round(principal * SecFeePerDollar, 2) : 0m;
        var venueFee = decimal.TryParse(context.State.GetValueOrDefault("estimatedVenueFee"), out var vf) ? vf : 0m;
        var netMoney = order.Side == OrderSide.Buy
            ? principal + venueFee
            : principal - secFee + venueFee;

        context.State["settlementDate"] = settlementDate.ToString("yyyy-MM-dd");
        context.State["principal"] = principal.ToString("F2");
        context.State["secFee"] = secFee.ToString("F2");
        context.State["netMoney"] = netMoney.ToString("F2");
        context.State["clearingCorp"] = "DTCC/NSCC-CNS";
        context.Record(Name, $"cleared via CNS, settles {settlementDate:yyyy-MM-dd}, net {netMoney:C}");
        return Task.FromResult(StageResult.Ok());
    }

    private static DateTime AddBusinessDays(DateTime start, int businessDays)
    {
        var date = start;
        while (businessDays > 0)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                businessDays--;
            }
        }

        return date;
    }
}

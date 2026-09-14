using FixedIncomeOptionsEngine.Pipeline;

namespace FixedIncomeOptionsEngine.Trading;

/// <summary>
/// Clears and settles derivatives and fixed-income trades, where the clearing
/// venue and settlement cycle vary by instrument type. Listed options clear
/// through the OCC and settle T+1; U.S. Treasuries settle T+1 through Fedwire;
/// corporate bonds settle T+2 through DTCC. This stage selects the correct
/// clearing corporation and computes the settlement date accordingly, then
/// stamps the net money to be exchanged.
/// </summary>
public sealed class DerivativesClearingService : IOrderStage
{
    public string Name => "settle";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var assetClass = context.State.GetValueOrDefault("assetClass", order.Attr("assetClass").ToUpperInvariant());

        string clearer;
        int settlementDays;
        decimal netMoney;

        if (assetClass == "OPTION")
        {
            clearer = "OCC";
            settlementDays = 1;
            var premium = order.LimitPrice ?? 0m;
            netMoney = premium * order.Quantity * 100m;
        }
        else
        {
            var isTreasury = order.Attr("issuerType").Equals("TREASURY", StringComparison.OrdinalIgnoreCase);
            clearer = isTreasury ? "Fedwire" : "DTCC";
            settlementDays = isTreasury ? 1 : 2;
            var dirty = decimal.TryParse(context.State.GetValueOrDefault("dirtyPrice"), out var dp)
                ? dp
                : order.LimitPrice ?? 100m;
            var face = order.AttrDecimal("faceValue", 1000m);
            netMoney = dirty / 100m * face * order.Quantity;
        }

        var settlementDate = AddBusinessDays(order.ReceivedAt.Date, settlementDays);
        context.State["clearingCorp"] = clearer;
        context.State["settlementDate"] = settlementDate.ToString("yyyy-MM-dd");
        context.State["netMoney"] = netMoney.ToString("F2");
        context.Record("settle", $"cleared via {clearer}, T+{settlementDays} settles {settlementDate:yyyy-MM-dd}, net {netMoney:C}");
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

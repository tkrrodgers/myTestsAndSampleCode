using System.Collections.Concurrent;
using EquityTradingPipeline.Pipeline;

namespace EquityTradingPipeline.Trading;

/// <summary>
/// Executes fills and maintains per-client, per-symbol tax lots using First-In,
/// First-Out (FIFO) allocation. A buy opens a new lot at the execution price; a
/// sell is allocated against the oldest open lots first, and the realized
/// short-/long-term gain or loss is computed per lot (long-term when the lot has
/// been held for at least 365 days). This is the default IRS cost-basis method
/// for equities and drives the client's year-end 1099-B.
/// </summary>
public sealed class FifoTaxLotAllocator : IOrderStage
{
    private sealed record TaxLot(decimal Quantity, decimal CostBasisPerShare, DateTimeOffset OpenedAt);

    private readonly ConcurrentDictionary<string, Queue<TaxLot>> _lots = new();

    public string Name => "execute";

    public Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken)
    {
        var order = context.Order;
        var executionPrice = order.LimitPrice ?? order.AttrDecimal("referencePrice");
        if (executionPrice <= 0)
        {
            return Task.FromResult(StageResult.Fail("no executable price available for fill"));
        }

        var key = $"{order.ClientId}:{order.Symbol}";
        var lots = _lots.GetOrAdd(key, _ => new Queue<TaxLot>());

        lock (lots)
        {
            if (order.Side == OrderSide.Buy)
            {
                lots.Enqueue(new TaxLot(order.Quantity, executionPrice, order.ReceivedAt));
                context.State["openLots"] = lots.Count.ToString();
                context.Record(Name, $"opened lot {order.Quantity}@{executionPrice:C} (FIFO position depth {lots.Count})");
                return Task.FromResult(StageResult.Ok());
            }

            var remaining = order.Quantity;
            decimal realized = 0m;
            var longTermQty = 0m;
            while (remaining > 0 && lots.Count > 0)
            {
                var lot = lots.Peek();
                var matched = Math.Min(remaining, lot.Quantity);
                realized += (executionPrice - lot.CostBasisPerShare) * matched;
                if ((order.ReceivedAt - lot.OpenedAt).TotalDays >= 365)
                {
                    longTermQty += matched;
                }

                remaining -= matched;
                if (matched == lot.Quantity)
                {
                    lots.Dequeue();
                }
                else
                {
                    lots.Dequeue();
                    lots.Enqueue(lot with { Quantity = lot.Quantity - matched });
                    // Re-order so the partially consumed lot stays at the front (FIFO).
                    var rotated = new Queue<TaxLot>();
                    rotated.Enqueue(lots.Last());
                    foreach (var l in lots.Take(lots.Count - 1))
                    {
                        rotated.Enqueue(l);
                    }

                    lots.Clear();
                    foreach (var l in rotated)
                    {
                        lots.Enqueue(l);
                    }
                }
            }

            if (remaining > 0)
            {
                return Task.FromResult(StageResult.Fail(
                    $"insufficient tax lots to cover sell of {order.Quantity} {order.Symbol} (short {remaining})"));
            }

            context.State["realizedPnl"] = realized.ToString("F2");
            context.State["longTermShares"] = longTermQty.ToString();
            context.Record(Name, $"sold {order.Quantity}@{executionPrice:C}, realized {realized:C} ({longTermQty} long-term)");
            return Task.FromResult(StageResult.Ok());
        }
    }
}

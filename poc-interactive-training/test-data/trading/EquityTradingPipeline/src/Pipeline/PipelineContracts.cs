namespace EquityTradingPipeline.Pipeline;

public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>
/// Immutable order event as it arrives off the Kafka pipeline. The envelope is
/// deliberately asset-class agnostic: fixed fields cover every listed
/// instrument, while asset-specific detail travels in <see cref="Attributes"/>.
/// </summary>
public sealed record OrderEvent(
    string OrderId,
    string ClientId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal? LimitPrice,
    string OrderType,
    DateTimeOffset ReceivedAt,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string Attr(string key, string fallback = "") =>
        Attributes.TryGetValue(key, out var value) ? value : fallback;

    public decimal AttrDecimal(string key, decimal fallback = 0m) =>
        Attributes.TryGetValue(key, out var value) && decimal.TryParse(value, out var parsed)
            ? parsed
            : fallback;
}

/// <summary>
/// Mutable per-order working state threaded through every pipeline stage. Stages
/// enrich <see cref="State"/>, append to the <see cref="Journal"/>, and may
/// reject the order, short-circuiting the remaining stages.
/// </summary>
public sealed class OrderContext
{
    public OrderContext(OrderEvent order) => Order = order;

    public OrderEvent Order { get; }

    public Dictionary<string, string> State { get; } = new();

    public List<string> Journal { get; } = new();

    public bool Rejected { get; private set; }

    public string? RejectReason { get; private set; }

    public void Reject(string reason)
    {
        Rejected = true;
        RejectReason = reason;
    }

    public void Record(string stage, string message) => Journal.Add($"{stage}: {message}");
}

public readonly record struct StageResult(bool Success, string? Reason = null)
{
    public static StageResult Ok() => new(true);

    public static StageResult Fail(string reason) => new(false, reason);
}

/// <summary>Contract implemented by every asset-specific processing stage.</summary>
public interface IOrderStage
{
    string Name { get; }

    Task<StageResult> ExecuteAsync(OrderContext context, CancellationToken cancellationToken);
}

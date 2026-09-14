using System.Collections.Concurrent;
using EquityTradingPipeline.Configuration;
using EquityTradingPipeline.Pipeline;

namespace EquityTradingPipeline.Persistence;

/// <summary>
/// PostgreSQL-backed order-state store. In production this writes to the
/// normalized <c>orders</c> / <c>order_events</c> tables through a pooled
/// connection; here the pool and tables are simulated in-memory while preserving
/// the same connection-pool sizing, upsert-by-order-id semantics, and optimistic
/// write path used against Postgres.
/// </summary>
public sealed class OrderRepository
{
    private readonly PostgresConfiguration _config;
    private readonly SemaphoreSlim _connectionPool;
    private readonly ConcurrentDictionary<string, OrderRecord> _orders = new();

    public OrderRepository(PostgresConfiguration config)
    {
        _config = config;
        _connectionPool = new SemaphoreSlim(config.MaxPoolSize, config.MaxPoolSize);
    }

    public async Task SaveAsync(OrderContext context, CancellationToken cancellationToken = default)
    {
        await _connectionPool.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Simulated round-trip + fsync latency against the primary.
            await Task.Yield();
            var record = new OrderRecord(
                context.Order.OrderId,
                context.Order.ClientId,
                context.Order.Symbol,
                context.Rejected ? "REJECTED" : "FILLED",
                context.RejectReason,
                string.Join(" | ", context.Journal),
                DateTimeOffset.UtcNow);
            _orders[record.OrderId] = record;
        }
        finally
        {
            _connectionPool.Release();
        }
    }

    public OrderRecord? Find(string orderId) => _orders.TryGetValue(orderId, out var record) ? record : null;

    public int Count => _orders.Count;

    public IReadOnlyCollection<OrderRecord> All => _orders.Values.ToList();

    public sealed record OrderRecord(
        string OrderId,
        string ClientId,
        string Symbol,
        string Status,
        string? RejectReason,
        string Journal,
        DateTimeOffset UpdatedAt);
}

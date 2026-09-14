using FixedIncomeOptionsEngine.Configuration;
using FixedIncomeOptionsEngine.Messaging;
using FixedIncomeOptionsEngine.Observability;
using FixedIncomeOptionsEngine.Persistence;
using FixedIncomeOptionsEngine.Pipeline;
using FixedIncomeOptionsEngine.Trading;

var config = PipelineConfiguration.Default;
var metrics = new MetricsCollector();
var repository = new OrderRepository(config.Postgres);

// Wire the asset-specific stages onto the shared async pipeline.
var stages = new IOrderStage[]
{
    new OptionsStrategyValidator(),
    new RegTMarginCalculator(),
    new YieldToMaturityCalculator(),
    new DerivativesClearingService(),
};

var pipeline = new OrderProcessingPipeline(stages, metrics, repository);
await using var consumer = new KafkaOrderConsumer(config.Kafka, pipeline, metrics);
consumer.Start();

Console.WriteLine("=== Fixed-Income & Options Engine ===");
Console.WriteLine($"Kafka group '{consumer.ConsumerGroup}', topic '{KafkaOrderConsumer.TopicFor("execute")}'");
Console.WriteLine($"Postgres pool size {config.Postgres.MaxPoolSize} -> {config.Postgres.Host}:{config.Postgres.Port}\n");

foreach (var order in SampleOrders())
{
    await consumer.PublishAsync(order);
}

await consumer.DrainAsync(TimeSpan.FromSeconds(5));

foreach (var record in repository.All.OrderBy(r => r.OrderId))
{
    Console.WriteLine($"[{record.Status,-8}] {record.OrderId} {record.Symbol,-6} :: {record.Journal}");
    if (record.RejectReason is not null)
    {
        Console.WriteLine($"           reason: {record.RejectReason}");
    }
}

Console.WriteLine($"\nProcessed {repository.Count} orders, committed offset {consumer.CommittedOffset}.");
Console.WriteLine("\n--- /metrics (excerpt) ---");
Console.WriteLine(metrics.Scrape());

static IEnumerable<OrderEvent> SampleOrders()
{
    var now = DateTimeOffset.UtcNow;
    IReadOnlyDictionary<string, string> Attr(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    // Long call vertical spread (defined risk).
    yield return new OrderEvent("FO-2001", "CLIENT-D", "SPY", OrderSide.Buy, 5, 2.10m, "LIMIT", now,
        Attr(("assetClass", "OPTION"), ("strategy", "VERTICAL_SPREAD"),
             ("right1", "CALL"), ("right2", "CALL"), ("strike1", "500"), ("strike2", "505"),
             ("expiry1", "2026-12-18"), ("expiry2", "2026-12-18"),
             ("underlyingPrice", "498"), ("buyingPower", "100000")));

    // Long straddle.
    yield return new OrderEvent("FO-2002", "CLIENT-D", "AAPL", OrderSide.Buy, 3, 6.50m, "LIMIT", now,
        Attr(("assetClass", "OPTION"), ("strategy", "STRADDLE"),
             ("right1", "CALL"), ("right2", "PUT"), ("strike1", "195"), ("strike2", "195"),
             ("expiry1", "2026-11-20"), ("expiry2", "2026-11-20"),
             ("underlyingPrice", "196"), ("buyingPower", "50000")));

    // Corporate bond purchase.
    yield return new OrderEvent("FO-2003", "CLIENT-E", "AAPL 4.5 2032", OrderSide.Buy, 100, 98.75m, "LIMIT", now,
        Attr(("assetClass", "BOND"), ("issuerType", "CORPORATE"), ("couponRate", "0.045"),
             ("couponFrequency", "2"), ("faceValue", "1000"), ("maturityDate", "2032-05-15"),
             ("daysSinceLastCoupon", "60"), ("buyingPower", "200000")));

    // Treasury bond purchase.
    yield return new OrderEvent("FO-2004", "CLIENT-E", "T 3.875 2034", OrderSide.Buy, 50, 101.20m, "LIMIT", now,
        Attr(("assetClass", "BOND"), ("issuerType", "TREASURY"), ("couponRate", "0.03875"),
             ("couponFrequency", "2"), ("faceValue", "1000"), ("maturityDate", "2034-08-15"),
             ("daysSinceLastCoupon", "30"), ("buyingPower", "200000")));

    // Malformed strangle (same strike) -> rejected at validation.
    yield return new OrderEvent("FO-2005", "CLIENT-F", "NVDA", OrderSide.Buy, 2, 3.00m, "LIMIT", now,
        Attr(("assetClass", "OPTION"), ("strategy", "STRANGLE"),
             ("right1", "CALL"), ("right2", "PUT"), ("strike1", "120"), ("strike2", "120"),
             ("expiry1", "2026-10-16"), ("expiry2", "2026-10-16"),
             ("underlyingPrice", "121"), ("buyingPower", "50000")));
}

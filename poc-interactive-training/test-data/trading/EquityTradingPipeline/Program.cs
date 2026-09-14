using EquityTradingPipeline.Configuration;
using EquityTradingPipeline.Messaging;
using EquityTradingPipeline.Observability;
using EquityTradingPipeline.Persistence;
using EquityTradingPipeline.Pipeline;
using EquityTradingPipeline.Trading;

var config = PipelineConfiguration.Default;
var metrics = new MetricsCollector();
var repository = new OrderRepository(config.Postgres);

// Wire the asset-specific stages onto the shared async pipeline.
var stages = new IOrderStage[]
{
    new EquityOrderValidator(),
    new Rule606OrderRouter(),
    new FifoTaxLotAllocator(),
    new EquityClearingService(),
};

var pipeline = new OrderProcessingPipeline(stages, metrics, repository);
await using var consumer = new KafkaOrderConsumer(config.Kafka, pipeline, metrics);
consumer.Start();

Console.WriteLine("=== Equity Trading Pipeline ===");
Console.WriteLine($"Kafka group '{consumer.ConsumerGroup}', topic '{KafkaOrderConsumer.TopicFor("execute")}'");
Console.WriteLine($"Postgres pool size {config.Postgres.MaxPoolSize} -> {config.Postgres.Host}:{config.Postgres.Port}\n");

foreach (var order in SampleOrders())
{
    await consumer.PublishAsync(order);
}

await consumer.DrainAsync(TimeSpan.FromSeconds(5));

foreach (var record in repository.All.OrderBy(r => r.OrderId))
{
    Console.WriteLine($"[{record.Status,-8}] {record.OrderId} {record.Symbol,-5} :: {record.Journal}");
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

    yield return new OrderEvent("EQ-1001", "CLIENT-A", "AAPL", OrderSide.Buy, 100, 190.00m, "LIMIT", now,
        Attr(("referencePrice", "191.20")));
    yield return new OrderEvent("EQ-1002", "CLIENT-A", "AAPL", OrderSide.Sell, 60, 205.00m, "LIMIT",
        now.AddDays(400), Attr(("referencePrice", "204.10")));
    yield return new OrderEvent("EQ-1003", "CLIENT-B", "MSFT", OrderSide.Buy, 50, null, "MARKET", now,
        Attr(("referencePrice", "410.00")));
    yield return new OrderEvent("EQ-1004", "CLIENT-C", "TSLA", OrderSide.Buy, 25, 999.00m, "LIMIT", now,
        Attr(("referencePrice", "250.00"))); // breaches LULD band -> rejected
    yield return new OrderEvent("EQ-1005", "CLIENT-A", "AAPL", OrderSide.Sell, 1000, 205.00m, "LIMIT", now,
        Attr(("referencePrice", "204.10"))); // insufficient tax lots -> rejected
}

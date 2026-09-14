using CryptoFxSpotDesk.Configuration;
using CryptoFxSpotDesk.Messaging;
using CryptoFxSpotDesk.Observability;
using CryptoFxSpotDesk.Persistence;
using CryptoFxSpotDesk.Pipeline;
using CryptoFxSpotDesk.Trading;

var config = PipelineConfiguration.Default;
var metrics = new MetricsCollector();
var repository = new OrderRepository(config.Postgres);

// Wire the asset-specific stages onto the shared async pipeline.
var stages = new IOrderStage[]
{
    new SpotFxOrderValidator(),
    new AmlWalletScreeningService(),
    new LiquidityQuoteAggregator(),
    new InstantSettlementEngine(),
};

var pipeline = new OrderProcessingPipeline(stages, metrics, repository);
await using var consumer = new KafkaOrderConsumer(config.Kafka, pipeline, metrics);
consumer.Start();

Console.WriteLine("=== Crypto & FX Spot Desk (24x7) ===");
Console.WriteLine($"Kafka group '{consumer.ConsumerGroup}', topic '{KafkaOrderConsumer.TopicFor("execute")}'");
Console.WriteLine($"Postgres pool size {config.Postgres.MaxPoolSize} -> {config.Postgres.Host}:{config.Postgres.Port}\n");

foreach (var order in SampleOrders())
{
    await consumer.PublishAsync(order);
}

await consumer.DrainAsync(TimeSpan.FromSeconds(5));

foreach (var record in repository.All.OrderBy(r => r.OrderId))
{
    Console.WriteLine($"[{record.Status,-8}] {record.OrderId} {record.Symbol,-10} :: {record.Journal}");
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

    // Crypto buy, clean wallet.
    yield return new OrderEvent("CX-3001", "CLIENT-G", "BTC/USD", OrderSide.Buy, 0.5m, 63000m, "LIMIT", now,
        Attr(("assetClass", "CRYPTO"), ("counterpartyWallet", "1CleanCustomerWalletAddrABCDEFG12345"),
             ("mixerExposure", "0.05"), ("hopsToFlaggedCluster", "9")));

    // Spot FX sell.
    yield return new OrderEvent("CX-3002", "CLIENT-H", "EUR/USD", OrderSide.Sell, 100000m, 1.0850m, "LIMIT", now,
        Attr(("assetClass", "FX"), ("counterpartyJurisdiction", "DE")));

    // Crypto withdrawal to a sanctioned wallet -> blocked.
    yield return new OrderEvent("CX-3003", "CLIENT-I", "ETH/USDT", OrderSide.Sell, 10m, 3400m, "LIMIT", now,
        Attr(("assetClass", "CRYPTO"), ("destinationWallet", "0xdeadbeefsanctionedwalletaddress0001"),
             ("counterpartyWallet", "0xdeadbeefsanctionedwalletaddress0001")));

    // Crypto buy, high mixer exposure -> escalated (EDD) -> rejected in this demo.
    yield return new OrderEvent("CX-3004", "CLIENT-J", "BTC/USDT", OrderSide.Buy, 2m, 63000m, "LIMIT", now,
        Attr(("assetClass", "CRYPTO"), ("counterpartyWallet", "1MixerHeavyWalletAddrHJKLMNOP987654"),
             ("mixerExposure", "0.9"), ("hopsToFlaggedCluster", "1")));

    // Below-minimum notional -> rejected at validation.
    yield return new OrderEvent("CX-3005", "CLIENT-K", "BTC/USD", OrderSide.Buy, 0.00001m, 63000m, "LIMIT", now,
        Attr(("assetClass", "CRYPTO"), ("counterpartyWallet", "1SmallOrderWalletAddrQRSTUVWX456789")));
}

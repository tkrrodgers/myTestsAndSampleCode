namespace FixedIncomeOptionsEngine.Configuration;

/// <summary>
/// Spring-Boot-style externalized configuration. Values mirror the shared
/// broker-dealer platform defaults so every asset-class service deploys with an
/// identical technical footprint (same Kafka topics, same Postgres pool sizing).
/// </summary>
public sealed record PipelineConfiguration
{
    public KafkaConfiguration Kafka { get; init; } = new();

    public PostgresConfiguration Postgres { get; init; } = new();

    public static PipelineConfiguration Default => new();
}

public sealed record KafkaConfiguration
{
    public string BootstrapServers { get; init; } = "kafka-0.broker.svc:9092,kafka-1.broker.svc:9092";

    public string ConsumerGroup { get; init; } = "order-execution-pipeline";

    public int MaxPollRecords { get; init; } = 500;

    public bool EnableAutoCommit { get; init; } = false;

    public string AutoOffsetReset { get; init; } = "earliest";
}

public sealed record PostgresConfiguration
{
    public string Host { get; init; } = "orders-primary.db.svc";

    public int Port { get; init; } = 5432;

    public string Database { get; init; } = "orders";

    public int MaxPoolSize { get; init; } = 20;

    public int ConnectionTimeoutSeconds { get; init; } = 5;
}

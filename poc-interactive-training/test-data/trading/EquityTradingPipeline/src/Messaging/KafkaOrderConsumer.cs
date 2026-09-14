using System.Diagnostics;
using System.Threading.Channels;
using EquityTradingPipeline.Configuration;
using EquityTradingPipeline.Observability;
using EquityTradingPipeline.Pipeline;

namespace EquityTradingPipeline.Messaging;

/// <summary>
/// Simulated Kafka consumer. Subscribes to the ordered pipeline topics using the
/// house naming convention <c>orders.&lt;stage&gt;.v1</c>, polls in a background
/// loop backed by a bounded in-memory partition, and commits offsets only after
/// the pipeline acknowledges (at-least-once). The transport is simulated so the
/// service runs without a live broker, but the topic conventions, consumer-group
/// semantics, and manual-commit flow mirror the production deployment.
/// </summary>
public sealed class KafkaOrderConsumer : IAsyncDisposable
{
    public const string TopicPrefix = "orders";
    public const string TopicVersion = "v1";

    private readonly KafkaConfiguration _config;
    private readonly OrderProcessingPipeline _pipeline;
    private readonly MetricsCollector _metrics;
    private readonly Channel<OrderEvent> _partition;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private long _committedOffset;

    public KafkaOrderConsumer(
        KafkaConfiguration config,
        OrderProcessingPipeline pipeline,
        MetricsCollector metrics)
    {
        _config = config;
        _pipeline = pipeline;
        _metrics = metrics;
        _partition = Channel.CreateBounded<OrderEvent>(new BoundedChannelOptions(config.MaxPollRecords)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public static string TopicFor(string stage) => $"{TopicPrefix}.{stage}.{TopicVersion}";

    public string ConsumerGroup => _config.ConsumerGroup;

    public long CommittedOffset => Interlocked.Read(ref _committedOffset);

    public async ValueTask PublishAsync(OrderEvent order, CancellationToken cancellationToken = default)
    {
        await _partition.Writer.WriteAsync(order, cancellationToken).ConfigureAwait(false);
        _metrics.IncrementCounter("kafka_records_produced_total");
    }

    public void Start() => _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _partition.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var order))
                {
                    _metrics.SetGauge("kafka_consumer_lag", reader.Count);
                    await _pipeline.ProcessAsync(order, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _committedOffset);
                    _metrics.IncrementCounter("kafka_records_committed_total");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown requested; commit progress is already durable.
        }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        _partition.Writer.TryComplete();
        var stopwatch = Stopwatch.StartNew();
        while (_partition.Reader.Count > 0 && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}

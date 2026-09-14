using System.Diagnostics;
using CryptoFxSpotDesk.Observability;
using CryptoFxSpotDesk.Persistence;

namespace CryptoFxSpotDesk.Pipeline;

/// <summary>
/// Asynchronous order-processing pipeline. Every order flows through an
/// identical, ordered set of stages (validate → risk → route → execute →
/// settle), latency is observed per stage, and terminal state is persisted. The
/// orchestration itself is asset-class agnostic; all asset-specific behaviour is
/// injected as <see cref="IOrderStage"/> implementations.
/// </summary>
public sealed class OrderProcessingPipeline
{
    private readonly IReadOnlyList<IOrderStage> _stages;
    private readonly MetricsCollector _metrics;
    private readonly OrderRepository _repository;

    public OrderProcessingPipeline(
        IReadOnlyList<IOrderStage> stages,
        MetricsCollector metrics,
        OrderRepository repository)
    {
        _stages = stages;
        _metrics = metrics;
        _repository = repository;
    }

    public async Task<OrderContext> ProcessAsync(OrderEvent order, CancellationToken cancellationToken = default)
    {
        var context = new OrderContext(order);
        var pipelineStart = Stopwatch.GetTimestamp();
        _metrics.IncrementCounter("orders_received_total");

        foreach (var stage in _stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageStart = Stopwatch.GetTimestamp();
            StageResult result;
            try
            {
                result = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _metrics.IncrementCounter("stage_exceptions_total", stage.Name);
                context.Reject($"stage '{stage.Name}' threw: {ex.Message}");
                break;
            }
            finally
            {
                _metrics.ObserveLatency(stage.Name, Stopwatch.GetElapsedTime(stageStart));
            }

            if (!result.Success)
            {
                _metrics.IncrementCounter("orders_rejected_total", stage.Name);
                context.Reject(result.Reason ?? $"rejected at {stage.Name}");
                break;
            }
        }

        await _repository.SaveAsync(context, cancellationToken).ConfigureAwait(false);
        _metrics.ObserveLatency("pipeline_total", Stopwatch.GetElapsedTime(pipelineStart));
        _metrics.IncrementCounter(context.Rejected
            ? "orders_terminal_rejected_total"
            : "orders_terminal_filled_total");
        return context;
    }
}

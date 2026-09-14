using System.Collections.Concurrent;
using System.Text;

namespace CryptoFxSpotDesk.Observability;

/// <summary>
/// Prometheus-style in-process metrics registry. Tracks monotonic counters,
/// point-in-time gauges, and fixed-bucket latency histograms so per-stage
/// pipeline latency can be scraped at <c>/metrics</c>. Thread-safe for the
/// concurrent consumer / pipeline access pattern.
/// </summary>
public sealed class MetricsCollector
{
    private static readonly double[] BucketBoundsMs =
    {
        0.5, 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000,
    };

    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, Histogram> _histograms = new();

    public void IncrementCounter(string name, string? label = null, long by = 1)
        => _counters.AddOrUpdate(Key(name, label), by, (_, value) => value + by);

    public void SetGauge(string name, double value) => _gauges[name] = value;

    public void ObserveLatency(string stage, TimeSpan elapsed)
        => _histograms.GetOrAdd(stage, _ => new Histogram(BucketBoundsMs)).Observe(elapsed.TotalMilliseconds);

    public long CounterValue(string name, string? label = null)
        => _counters.TryGetValue(Key(name, label), out var value) ? value : 0;

    public string Scrape()
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in _counters)
        {
            sb.Append(key).Append(' ').Append(value).Append('\n');
        }

        foreach (var (key, value) in _gauges)
        {
            sb.Append(key).Append(' ').Append(value).Append('\n');
        }

        foreach (var (stage, histogram) in _histograms)
        {
            sb.Append(histogram.Render(stage));
        }

        return sb.ToString();
    }

    private static string Key(string name, string? label)
        => label is null ? name : $"{name}{{stage=\"{label}\"}}";

    private sealed class Histogram
    {
        private readonly double[] _bounds;
        private readonly long[] _counts;
        private long _total;
        private double _sum;

        public Histogram(double[] bounds)
        {
            _bounds = bounds;
            _counts = new long[bounds.Length + 1];
        }

        public void Observe(double valueMs)
        {
            lock (_counts)
            {
                _total++;
                _sum += valueMs;
                var index = 0;
                while (index < _bounds.Length && valueMs > _bounds[index])
                {
                    index++;
                }

                _counts[index]++;
            }
        }

        public string Render(string stage)
        {
            var sb = new StringBuilder();
            lock (_counts)
            {
                long cumulative = 0;
                for (var i = 0; i < _bounds.Length; i++)
                {
                    cumulative += _counts[i];
                    sb.Append($"stage_latency_ms_bucket{{stage=\"{stage}\",le=\"{_bounds[i]}\"}} {cumulative}\n");
                }

                sb.Append($"stage_latency_ms_sum{{stage=\"{stage}\"}} {_sum}\n");
                sb.Append($"stage_latency_ms_count{{stage=\"{stage}\"}} {_total}\n");
            }

            return sb.ToString();
        }
    }
}

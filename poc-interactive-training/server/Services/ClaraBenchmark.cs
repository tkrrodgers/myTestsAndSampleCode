using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Measures the compiled CLARA policy against the same rules hand-written in C#.
// Both engines run the identical workload in identically shaped loops, best-of-N rounds, after a warm-up,
// so the number reported is the steady-state cost of the decision table itself rather than JIT or GC noise.
public sealed class ClaraBenchmark
{
    private const string ReferenceEngine = "C# (hand-written)";

    private readonly object _gate = new();
    private ClaraBenchmarkResult? _cached;

    // Balance/days pairs that exercise every branch of the late-fee decision table.
    private static readonly (decimal Balance, decimal DaysOverdue)[] Workload =
    [
        (0m, 10m),
        (-50m, 10m),
        (500m, 0m),
        (500m, 3m),
        (1000m, 10m),
        (100000m, 30m),
        (2500.55m, 7m),
        (99.99m, 4m)
    ];

    public ClaraBenchmarkResult? Cached => _cached;

    public ClaraBenchmarkResult Run(int iterations = 1_000_000, int rounds = 5)
    {
        iterations = Math.Clamp(iterations, 10_000, 20_000_000);
        rounds = Math.Clamp(rounds, 1, 15);

        lock (_gate)
        {
            var compilation = ClaraCompiler.Compile(RoundTripSamples.ClaraSample);
            if (compilation.Policy is not { } policy)
            {
                var reasons = string.Join("; ", compilation.Diagnostics.Where(d => d.Severity == ClaraSeverity.Error).Select(d => d.Format()));
                return _cached = new ClaraBenchmarkResult("LateFee", 0, 0, compilation.CompileMilliseconds, [], 0, 0,
                    RuntimeDescription(), $"The reference policy did not compile: {reasons}");
            }

            var balanceSlot = policy.Inputs.First(symbol => symbol.Name == "balance").Slot;
            var daysSlot = policy.Inputs.First(symbol => symbol.Name == "daysOverdue").Slot;
            var feeSlot = policy.Outputs.First(symbol => symbol.Name == "fee").Slot;

            var (checks, mismatches) = CheckParity(policy, balanceSlot, daysSlot, feeSlot);

            // Warm-up: force tiering-up of both delegates before any measurement.
            _ = MeasureClara(policy, balanceSlot, daysSlot, feeSlot, 50_000);
            _ = MeasureCSharp(50_000);
            _ = MeasureDictionary(policy, 5_000);

            // Rounds are interleaved so a thermal or scheduling drift penalises both engines equally.
            var dictionaryIterations = Math.Max(iterations / 10, 10_000);
            Measurement? clara = null;
            Measurement? csharp = null;
            Measurement? dictionary = null;
            for (var round = 0; round < rounds; round++)
            {
                Keep(ref clara, MeasureClara(policy, balanceSlot, daysSlot, feeSlot, iterations));
                Keep(ref csharp, MeasureCSharp(iterations));
                Keep(ref dictionary, MeasureDictionary(policy, dictionaryIterations));
            }

            var rows = new List<ClaraBenchmarkRow>
            {
                Row("CLARA (compiled, hot path)", clara!.Value, csharp!.Value,
                    "Expression tree JIT-compiled to machine code; caller-owned decimal frame."),
                Row(ReferenceEngine, csharp!.Value, csharp!.Value,
                    "The same three rules written directly as C# with decimal arithmetic."),
                Row("CLARA (dictionary API)", dictionary!.Value, csharp!.Value,
                    "The convenience entry point that binds inputs by name and returns a dictionary.")
            };

            var claraNanos = rows[0].NanosecondsPerCall;
            var csharpNanos = rows[1].NanosecondsPerCall;
            var ratio = csharpNanos > 0 ? claraNanos / csharpNanos : 0;
            var caveat = JitOptimized
                ? ""
                : " Measured on an unoptimised Debug build — rerun in Release before quoting these numbers.";
            var summary = mismatches > 0
                ? $"PARITY FAILURE: {mismatches} of {checks} inputs disagreed with the C# reference."
                : $"Identical results on all {checks:N0} probe inputs. CLARA costs {claraNanos:N1} ns per decision against " +
                  $"{csharpNanos:N1} ns for hand-written C# ({ratio:N2}x), or {rows[0].MillionCallsPerSecond:N1} million decisions per second per core.{caveat}";

            return _cached = new ClaraBenchmarkResult(
                policy.Name,
                iterations,
                rounds,
                compilation.CompileMilliseconds,
                rows,
                checks,
                mismatches,
                RuntimeDescription(),
                summary);
        }
    }

    private static ClaraBenchmarkRow Row(string engine, Measurement measurement, Measurement reference, string detail)
    {
        var nanos = measurement.Nanoseconds / measurement.Iterations;
        var referenceNanos = reference.Nanoseconds / reference.Iterations;
        var bytes = measurement.AllocatedBytes / (double)measurement.Iterations;
        return new ClaraBenchmarkRow(
            engine,
            nanos,
            nanos > 0 ? 1000d / nanos : 0d,
            referenceNanos > 0 ? nanos / referenceNanos : 0d,
            $"{detail} {bytes:N0} bytes allocated per call.");
    }

    private readonly record struct Measurement(double Nanoseconds, long AllocatedBytes, long Iterations, decimal Checksum);

    private static void Keep(ref Measurement? best, Measurement candidate)
    {
        if (best is null || candidate.Nanoseconds / candidate.Iterations < best.Value.Nanoseconds / best.Value.Iterations)
        {
            best = candidate;
        }
    }

    private static Measurement MeasureClara(ClaraPolicy policy, int balanceSlot, int daysSlot, int feeSlot, int iterations)
    {
        var frame = policy.CreateFrame();
        var workload = Workload;
        decimal checksum = 0m;

        Settle();
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        for (var index = 0; index < iterations; index++)
        {
            var (balance, days) = workload[index & 7];
            frame[balanceSlot] = balance;
            frame[daysSlot] = days;
            policy.Execute(frame);
            checksum += frame[feeSlot];
        }

        return Complete(start, startBytes, iterations, checksum);
    }

    private static Measurement MeasureCSharp(int iterations)
    {
        var workload = Workload;
        decimal checksum = 0m;

        Settle();
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        for (var index = 0; index < iterations; index++)
        {
            var (balance, days) = workload[index & 7];
            checksum += LateFeeReference(balance, days);
        }

        return Complete(start, startBytes, iterations, checksum);
    }

    private static Measurement MeasureDictionary(ClaraPolicy policy, int iterations)
    {
        var workload = Workload;
        decimal checksum = 0m;
        var arguments = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["balance"] = 0m, ["daysOverdue"] = 0m };

        Settle();
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        for (var index = 0; index < iterations; index++)
        {
            var (balance, days) = workload[index & 7];
            arguments["balance"] = balance;
            arguments["daysOverdue"] = days;
            checksum += policy.Evaluate(arguments).Outputs["fee"];
        }

        return Complete(start, startBytes, iterations, checksum);
    }

    private static Measurement Complete(long start, long startBytes, int iterations, decimal checksum)
    {
        var elapsed = Stopwatch.GetTimestamp() - start;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        var nanoseconds = elapsed * (1_000_000_000d / Stopwatch.Frequency);
        Consume(checksum);
        return new Measurement(nanoseconds, bytes, iterations, checksum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(decimal value) => _ = value;

    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // The identical decision table written by hand — the baseline CLARA has to justify itself against.
    private static decimal LateFeeReference(decimal balance, decimal daysOverdue)
    {
        if (balance <= 0m || daysOverdue <= 0m)
        {
            return 0m;
        }

        if (daysOverdue <= 3m)
        {
            return 0m;
        }

        return Math.Min(Math.Round(balance * 0.015m * daysOverdue, 2, MidpointRounding.AwayFromZero), 250.00m);
    }

    private static (int Checks, int Mismatches) CheckParity(ClaraPolicy policy, int balanceSlot, int daysSlot, int feeSlot)
    {
        var frame = policy.CreateFrame();
        var checks = 0;
        var mismatches = 0;

        for (var balanceStep = -2; balanceStep <= 60; balanceStep++)
        {
            var balance = balanceStep * 250.75m;
            for (var days = -1; days <= 45; days++)
            {
                frame[balanceSlot] = balance;
                frame[daysSlot] = days;
                policy.Execute(frame);
                checks++;
                if (frame[feeSlot] != LateFeeReference(balance, days))
                {
                    mismatches++;
                }
            }
        }

        return (checks, mismatches);
    }

    private static readonly bool JitOptimized =
        typeof(ClaraBenchmark).Assembly
            .GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .OfType<System.Diagnostics.DebuggableAttribute>()
            .FirstOrDefault() is not { IsJITTrackingEnabled: true };

    private static string RuntimeDescription() =>
        $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture} · " +
        $"{(System.Runtime.GCSettings.IsServerGC ? "server GC" : "workstation GC")} · {Environment.ProcessorCount} logical cores · " +
        $"{(JitOptimized ? "optimised build" : "DEBUG build, JIT optimisations disabled")}";
}

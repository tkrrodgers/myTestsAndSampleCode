using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// One golden case for the drift scorecard. The overview is explicit that a lesson needs one case, not
// the 30-50 case registry the source material calls for - that is production QA work, not a lesson.
public static class DriftSamples
{
    public const string GoldenTicket = """
        FUL-2210: Cap the retry backoff

        The delivery-estimate refresh job retries a failing carrier feed with exponential backoff and no
        ceiling, so a sustained outage produces multi-hour sleeps and the job appears hung.

        Acceptance criteria:
        1. The backoff delay never exceeds 60 seconds.
        2. Existing retry count behaviour is unchanged.
        3. The cap is a named constant, not a literal at the call site.

        Out of scope: do not change the retry count or the jitter strategy.
        Owner: confirm with R. Okafor (Fulfilment) if the cap needs to be configurable.
        """;

    public const string FrozenCode = """
        public sealed class CarrierFeedRetry
        {
            private const int MaxAttempts = 6;

            public TimeSpan DelayFor(int attempt)
            {
                var seconds = Math.Pow(2, attempt);
                return TimeSpan.FromSeconds(seconds);
            }

            public bool ShouldRetry(int attempt) => attempt < MaxAttempts;
        }
        """;

    // The patch a competent engineer wrote for this ticket. The structural layer diffs against this.
    public const string HumanPatch = """
        public sealed class CarrierFeedRetry
        {
            private const int MaxAttempts = 6;
            private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

            public TimeSpan DelayFor(int attempt)
            {
                var seconds = Math.Pow(2, attempt);
                var delay = TimeSpan.FromSeconds(seconds);
                return delay > MaxDelay ? MaxDelay : delay;
            }

            public bool ShouldRetry(int attempt) => attempt < MaxAttempts;
        }
        """;

    // A patch from a drifted agent: correct-ish behaviour, but it ignored the scope boundary and the
    // "named constant" criterion, and rewrote code the ticket told it not to touch.
    public const string DriftedPatch = """
        public sealed class CarrierFeedRetry
        {
            private const int MaxAttempts = 10;

            public TimeSpan DelayFor(int attempt)
            {
                var seconds = Math.Min(Math.Pow(2, attempt), 60);
                var jitter = Random.Shared.NextDouble() * 5;
                return TimeSpan.FromSeconds(seconds + jitter);
            }

            public bool ShouldRetry(int attempt) => attempt < MaxAttempts;

            public void Reset() { }
        }
        """;

    // The verification oracle: deterministic assertions a correct patch must satisfy.
    public static readonly DriftCheck[] Oracle =
    [
        new("Backoff is capped at 60 seconds", "DelayFor(20) <= 60s"),
        new("Retry count is unchanged", "MaxAttempts is still 6"),
        new("Cap is a named constant", "no bare 60 at the call site"),
        new("Jitter strategy untouched", "no randomness introduced"),
        new("No unrequested public surface", "no new public members")
    ];

    // Deterministic scoring of the structural layer. No model involved.
    public static DriftStructural ScoreStructure(string patch)
    {
        var text = patch ?? "";
        var findings = new List<ContextStructureSignal>
        {
            new("Retry count unchanged",
                text.Contains("MaxAttempts = 6", StringComparison.Ordinal),
                text.Contains("MaxAttempts = 6", StringComparison.Ordinal) ? "MaxAttempts is still 6" : "MaxAttempts was modified"),
            new("Cap is a named constant",
                text.Contains("MaxDelay", StringComparison.Ordinal),
                text.Contains("MaxDelay", StringComparison.Ordinal) ? "a named cap is declared" : "no named cap; the value is inline"),
            new("Jitter strategy untouched",
                !text.Contains("Random", StringComparison.OrdinalIgnoreCase) && !text.Contains("jitter", StringComparison.OrdinalIgnoreCase),
                text.Contains("Random", StringComparison.OrdinalIgnoreCase) ? "randomness was introduced" : "no randomness"),
            new("No unrequested public surface",
                CountPublicMembers(text) <= 2,
                $"{CountPublicMembers(text)} public member(s); the frozen class had 2")
        };

        var met = findings.Count(finding => finding.Present);
        return new DriftStructural((int)Math.Round(met * 100.0 / findings.Count), findings);
    }

    private static int CountPublicMembers(string text) =>
        text.Split('\n').Count(line => line.TrimStart().StartsWith("public ", StringComparison.Ordinal) && !line.Contains("class ", StringComparison.Ordinal));
}

public sealed record DriftCheck(string Name, string Assertion);

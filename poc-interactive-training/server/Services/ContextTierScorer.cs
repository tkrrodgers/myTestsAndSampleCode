using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Deterministic scoring, implementing correction C3: the degradation curve must not be a model grading
// a model. Plans are scored by sealed-criterion detection, so the same plan always yields the same
// score and the result can be trended across model releases. The judge model adds narrative on top of
// this number — it never produces it.
public static class ContextTierScorer
{
    public static (int Score, List<string> Met, List<string> Missed) Score(string plan)
    {
        var text = (plan ?? string.Empty).ToLowerInvariant();
        var met = new List<string>();
        var missed = new List<string>();

        foreach (var criterion in ContextTierSamples.Criteria)
        {
            if (criterion.Signals.Any(signal => text.Contains(signal, StringComparison.Ordinal)))
            {
                met.Add(criterion.Name);
            }
            else
            {
                missed.Add(criterion.Name);
            }
        }

        var total = ContextTierSamples.Criteria.Count;
        var score = total == 0 ? 0 : (int)Math.Round(100.0 * met.Count / total);
        return (score, met, missed);
    }

    public static TierDegradation? Analyse(IReadOnlyList<ContextTierResult> tiers)
    {
        var scored = tiers.Where(tier => tier.Runs.Any(run => run.Status == "completed")).ToList();
        if (scored.Count < 2)
        {
            return null;
        }

        var best = scored.MaxBy(tier => tier.MedianScore)!;
        var worst = scored.MinBy(tier => tier.MedianScore)!;

        // Criteria the richest tier found and the poorest did not — the measured cost of the gap.
        var lost = best.MedianMet.Except(worst.MedianMet, StringComparer.Ordinal).ToList();

        var drop = best.MedianScore - worst.MedianScore;
        var note = drop <= 0
            ? "No degradation was measured. Either the ticket does not require the documented facts, or the model already knew them — do not present this as evidence that context is unnecessary until you have checked which."
            : $"Removing the documentation cost {drop} points and {lost.Count} of {ContextTierSamples.Criteria.Count} criteria. Everything else was held constant, so the gap is attributable to context.";

        var floor = scored
            .OrderByDescending(tier => tier.Index)
            .FirstOrDefault(tier => tier.MedianScore >= best.MedianScore * 0.8);

        return new TierDegradation(
            best.Id,
            worst.Id,
            drop,
            lost,
            floor?.Id ?? best.Id,
            note);
    }

    public static int Median(IEnumerable<int> values)
    {
        var ordered = values.OrderBy(value => value).ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
    }
}

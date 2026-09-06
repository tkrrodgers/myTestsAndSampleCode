using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Objective 3 + correction C1. The point of this scene is that "free" is the wrong word and "cost per
// token" is the wrong unit. An open-weight model has no per-token vendor invoice but does have fixed
// infrastructure cost, and a cheap model that fails a third of the time is not cheap — the honest unit
// is cost per SUCCESSFUL outcome, per doc 11 §7.
public sealed class TokenEconomicsService
{
    // Illustrative rates only. Replace with contract rates and measured GPU cost before quoting these.
    private const decimal VendorInputPerMillion = 3.00m;
    private const decimal VendorOutputPerMillion = 15.00m;
    private const decimal MidInputPerMillion = 0.50m;
    private const decimal MidOutputPerMillion = 1.50m;
    private const decimal GpuHourlyRate = 1.20m;
    private const int OpenWeightRequestsPerHour = 900;

    public EconomicsReport Build(int promptTokens, int outputTokens, IReadOnlyList<(string Rung, int SuccessPercent)> observed)
    {
        var rows = new List<EconomicsRow>();

        foreach (var (rung, successPercent) in observed)
        {
            var (model, kind, costPerAttempt, note) = rung switch
            {
                "open-weight" => (
                    "Gemma (self-hosted)",
                    "Open weight",
                    Math.Round(GpuHourlyRate / OpenWeightRequestsPerHour, 6),
                    $"No per-token vendor invoice. Cost is {GpuHourlyRate:C}/GPU-hour ÷ ~{OpenWeightRequestsPerHour} requests/hour — fixed, not zero."),
                "open-weight-grounded" => (
                    "Gemma + OKF grounding",
                    "Open weight",
                    Math.Round(GpuHourlyRate / OpenWeightRequestsPerHour, 6),
                    "Same infrastructure cost; grounding raises the success rate without raising the rate card."),
                "mid" => (
                    "Mid-tier vendor model",
                    "Vendor",
                    Math.Round(promptTokens / 1_000_000m * MidInputPerMillion + outputTokens / 1_000_000m * MidOutputPerMillion, 6),
                    "Per-token vendor billing at mid-tier rates."),
                _ => (
                    "Frontier vendor model",
                    "Vendor",
                    Math.Round(promptTokens / 1_000_000m * VendorInputPerMillion + outputTokens / 1_000_000m * VendorOutputPerMillion, 6),
                    "Per-token vendor billing at frontier rates.")
            };

            // The number that actually matters: a failed attempt still costs money and still has to be redone.
            var attemptsPerSuccess = successPercent <= 0 ? 0m : 100m / successPercent;
            var costPerSuccess = attemptsPerSuccess == 0m ? 0m : Math.Round(costPerAttempt * attemptsPerSuccess, 6);

            rows.Add(new EconomicsRow(
                rung,
                model,
                kind,
                successPercent,
                costPerAttempt,
                attemptsPerSuccess == 0m ? 0m : Math.Round(attemptsPerSuccess, 2),
                costPerSuccess,
                successPercent <= 0 ? "Never succeeds at this task — any cost is wasted." : note));
        }

        var viable = rows.Where(row => row.SuccessPercent > 0).ToList();
        var cheapest = viable.OrderBy(row => row.CostPerSuccess).FirstOrDefault();
        var naiveCheapest = rows.OrderBy(row => row.CostPerAttempt).First();

        var policy = BuildPolicy(rows, cheapest, naiveCheapest);

        return new EconomicsReport(
            promptTokens,
            outputTokens,
            rows,
            cheapest?.Rung ?? "none",
            naiveCheapest.Rung,
            cheapest is not null && cheapest.Rung != naiveCheapest.Rung,
            policy);
    }

    private static List<string> BuildPolicy(List<EconomicsRow> rows, EconomicsRow? cheapest, EconomicsRow naiveCheapest)
    {
        var policy = new List<string>
        {
            "Say \"no per-token vendor cost\", not \"free\". Open weight moves cost from variable to fixed; it does not remove it.",
            "Budget in cost per merged outcome, not cost per token. A model that fails is billed for every failure."
        };

        if (cheapest is not null && cheapest.Rung != naiveCheapest.Rung)
        {
            policy.Add($"The cheapest per attempt ({naiveCheapest.Model}) is not the cheapest per success ({cheapest.Model}). Routing on rate card alone would pick the wrong one.");
        }

        var grounded = rows.FirstOrDefault(row => row.Rung == "open-weight-grounded");
        var bare = rows.FirstOrDefault(row => row.Rung == "open-weight");
        if (grounded is not null && bare is not null && grounded.SuccessPercent > bare.SuccessPercent)
        {
            policy.Add($"Grounding raised the open-weight success rate from {bare.SuccessPercent}% to {grounded.SuccessPercent}% at identical infrastructure cost. Context is the cheapest quality lever available.");
        }

        policy.Add("Escalate on measured failure, not on preference: try prompt, then grounding, then a larger model. Record which rung succeeded so the routing policy stays evidence-based.");
        policy.Add("These rates are illustrative. Substitute contract pricing and measured GPU throughput before presenting any figure to Finance.");
        return policy;
    }
}

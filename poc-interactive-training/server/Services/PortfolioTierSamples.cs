namespace PocInteractiveTraining.Server.Services;

// Tier 1 and Tier 2 context for the three real trading repositories. Tier 3 is not here — it is read
// from each repository's own /okf directory by TradingCorpus, because that is where it actually lives.
//
// The ecosystem deliberately contains a trap: all three trading services share platform-order-pipeline.
// A reader who stops at Tier 1 will propose changing the shared library, which would apply fraud checks
// to the crypto desk as well. Only Tier 2 shows why that is wrong.
public static class PortfolioTierSamples
{
    public const string DesignBrief = """
        DESIGN BRIEF — FRAUD-2140: centralised pre-trade fraud screening

        Every trading service that handles client orders must submit each order to the new centralised
        Fraud Decision Service before the order is routed or executed.

        Scope: all trading services EXCEPT the crypto/digital-asset desk. Digital assets already run
        AML wallet screening under a separate regulatory perimeter and are explicitly out of scope for
        this programme.

        Requirements:
        1. Orders are screened after validation and before routing or execution.
        2. A DENY decision rejects the order with the fraud reference code.
        3. A REVIEW decision holds the order; it must not be silently allowed through.
        4. Screening must not change existing risk, margin, pricing or settlement behaviour.
        5. The fraud decision and its reference must be recorded on the order journal.
        6. A screening timeout must fail closed, not open.

        Deliverable: the change required in each in-scope service. Do not write the Fraud Decision
        Service itself — it is owned by another team.
        """;

    // --- Tier 1: Cortex global ecosystem ---

    public sealed record EcosystemService(
        string Name,
        string AmpId,
        string Domain,
        bool HandlesClientOrders,
        string Health,
        string Sla,
        IReadOnlyList<string> DependsOn);

    public static IReadOnlyList<EcosystemService> Tier1Services { get; } =
    [
        new("equity-trading-pipeline", "APM-1042", "Trading — listed equities", true, "Healthy", "Market hours 09:30–16:00 ET",
            ["platform-order-pipeline", "market-data-gateway", "settlement-ledger"]),
        new("fixed-income-options-engine", "APM-1042", "Trading — options and OTC fixed income", true, "Healthy", "Market hours 09:30–16:00 ET",
            ["platform-order-pipeline", "market-data-gateway", "settlement-ledger"]),
        new("crypto-fx-spot-desk", "APM-1187", "Trading — spot FX and digital assets", true, "Degraded — elevated latency", "24/7",
            ["platform-order-pipeline", "market-data-gateway"]),
        new("platform-order-pipeline", "APM-1044", "Shared library — order stage orchestration", false, "Healthy", "Library, no runtime SLA",
            []),
        new("market-data-gateway", "APM-1044", "Platform — price and reference data feeds", false, "Healthy", "24/7", []),
        new("settlement-ledger", "APM-1102", "Post-trade — settlement and books", false, "Healthy", "T+1 batch",
            ["platform-order-pipeline"]),
        new("client-onboarding-api", "APM-1301", "Client lifecycle — KYC and account opening", false, "Healthy", "Business hours", []),
        new("fraud-decision-service", "APM-1355", "Financial crime — centralised pre-trade screening", false, "New — not yet released", "99.95%, 150 ms p99", [])
    ];

    // --- Tier 2: APM ID level, team ownership bounds ---

    public sealed record AmpRecord(
        string AmpId,
        string Name,
        string Team,
        string RegulatoryPerimeter,
        IReadOnlyList<string> AssetClasses,
        IReadOnlyList<string> Repositories,
        IReadOnlyList<string> SharedLibraries,
        string ChangeConvention,
        string ScopeNote);

    public static IReadOnlyList<AmpRecord> Tier2Amps { get; } =
    [
        new("APM-1042", "Listed & OTC Trading", "Trading Platform Engineering",
            "Broker-dealer order flow — FINRA, Reg T, SEC Rule 606",
            ["Listed equities", "Listed options", "OTC corporate and government bonds"],
            ["equity-trading-pipeline", "fixed-income-options-engine"],
            ["platform-order-pipeline"],
            "New pipeline stages are added as IOrderStage implementations and registered in the service's own configuration. The shared library is never modified to add domain behaviour.",
            "Digital assets are explicitly NOT part of this APM. They were moved to APM-1187 in 2025 when the digital-asset perimeter was separated."),
        new("APM-1187", "Digital Assets Desk", "Digital Assets Engineering",
            "Money-services business — AML/BSA, OFAC wallet screening. Not broker-dealer order flow.",
            ["Spot FX", "Spot crypto"],
            ["crypto-fx-spot-desk"],
            ["platform-order-pipeline"],
            "Compliance controls are implemented in-desk and reviewed by the Financial Crime team under the MSB perimeter.",
            "Already performs AML wallet screening on every order. Subject to a separate financial-crime control set from the broker-dealer estate."),
        new("APM-1044", "Trading Platform Shared Services", "Platform Engineering",
            "Platform — no direct regulatory obligation",
            ["Asset-class agnostic"],
            ["platform-order-pipeline", "market-data-gateway"],
            [],
            "Shared libraries stay asset-class agnostic. Domain or compliance behaviour added here would be inherited by every consumer, including consumers outside the requesting perimeter.",
            "Consumed by APM-1042 and APM-1187 alike. That is precisely why domain rules must not be added here."),
        new("APM-1102", "Post-Trade & Settlement", "Post-Trade Engineering",
            "Books and records — SEC 17a-4",
            ["Asset-class agnostic"],
            ["settlement-ledger"], ["platform-order-pipeline"],
            "Changes require a books-and-records impact assessment.",
            "Downstream of execution; does not handle client order entry."),
        new("APM-1301", "Client Lifecycle", "Client Platform Engineering",
            "KYC/CIP",
            ["Not applicable"],
            ["client-onboarding-api"], [],
            "Standard change process.",
            "Does not handle orders."),
        new("APM-1355", "Financial Crime Platform", "Financial Crime Engineering",
            "AML/BSA and fraud",
            ["Asset-class agnostic"],
            ["fraud-decision-service"], [],
            "Owns the fraud decision contract. Consumers integrate; they do not extend it.",
            "Owner of the new service. Consuming teams implement their own call sites.")
    ];

    public static string Tier1Context()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("CORTEX — GLOBAL ECOSYSTEM MAP (Tier 1)");
        text.AppendLine("Every service in the estate, its owning APM ID, whether it handles client orders, its health and its dependencies.");
        text.AppendLine();
        foreach (var service in Tier1Services)
        {
            text.AppendLine($"- {service.Name} [{service.AmpId}] — {service.Domain}");
            text.AppendLine($"    handles client orders: {(service.HandlesClientOrders ? "yes" : "no")} | health: {service.Health} | SLA: {service.Sla}");
            text.AppendLine($"    depends on: {(service.DependsOn.Count == 0 ? "(none)" : string.Join(", ", service.DependsOn))}");
        }

        text.AppendLine();
        text.AppendLine("Tier 1 records structure, ownership and health. It does NOT record regulatory perimeter, asset-class scope, or change conventions.");
        return text.ToString();
    }

    public static string Tier2Context(IEnumerable<string> ampIds)
    {
        var wanted = ampIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = new System.Text.StringBuilder();
        text.AppendLine("APM RECORDS (Tier 2)");
        text.AppendLine("Team ownership bounds: what each APM is responsible for, under which regulatory perimeter, and how it accepts change.");
        text.AppendLine();
        foreach (var amp in Tier2Amps.Where(amp => wanted.Count == 0 || wanted.Contains(amp.AmpId)))
        {
            text.AppendLine($"{amp.AmpId} — {amp.Name} (team: {amp.Team})");
            text.AppendLine($"  Regulatory perimeter: {amp.RegulatoryPerimeter}");
            text.AppendLine($"  Asset classes: {string.Join(", ", amp.AssetClasses)}");
            text.AppendLine($"  Repositories: {string.Join(", ", amp.Repositories)}");
            text.AppendLine($"  Shared libraries consumed: {(amp.SharedLibraries.Count == 0 ? "(none)" : string.Join(", ", amp.SharedLibraries))}");
            text.AppendLine($"  Change convention: {amp.ChangeConvention}");
            text.AppendLine($"  Scope note: {amp.ScopeNote}");
            text.AppendLine();
        }

        return text.ToString();
    }

    // Cortex names services; the repositories on disk are named differently. Tier 3 is the atomic truth,
    // so the mapping is recorded rather than inferred by string similarity.
    public static readonly IReadOnlyDictionary<string, string> ServiceToRepository =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["equity-trading-pipeline"] = "EquityTradingPipeline",
            ["fixed-income-options-engine"] = "FixedIncomeOptionsEngine",
            ["crypto-fx-spot-desk"] = "CryptoFxSpotDesk"
        };
}

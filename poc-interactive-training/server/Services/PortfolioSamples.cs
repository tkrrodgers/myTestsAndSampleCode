namespace PocInteractiveTraining.Server.Services;

// Objective 6 fixture: a synthetic portfolio arranged Cortex (all repos) → AMP ID (department group)
// → repo. Two disagreements and one staleness cascade are planted deliberately, because a tier
// hierarchy only earns its keep when you can show what happens if the tiers contradict each other.
public static class PortfolioSamples
{
    public sealed record RepoEntry(
        string Name,
        string AmpId,
        string RepoPurpose,
        string RepoOwner,
        bool HasOkf,
        bool HasReadme,
        bool HasAdr,
        bool HasCodeMap,
        bool HasTests,
        DateTimeOffset StaleAfter);

    public sealed record AmpEntry(
        string AmpId,
        string Name,
        string Convention,
        string ClaimedOwner,
        DateTimeOffset StaleAfter);

    public sealed record CortexClaim(
        string Repo,
        string ClaimedPurpose,
        string ClaimedOwner);

    public const string CortexName = "CES portfolio";
    public static DateTimeOffset CortexStaleAfter { get; } = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<AmpEntry> Amps { get; } =
    [
        new("AMP-101", "Fulfillment", "OKF bundle required; ADRs in decisions/", "Fulfillment platform team", new(2027, 3, 31, 0, 0, 0, TimeSpan.Zero)),
        new("AMP-204", "Billing", "README + ADR required; OKF encouraged", "Billing team", new(2027, 1, 31, 0, 0, 0, TimeSpan.Zero)),
        new("AMP-330", "Customer web", "No documented convention", "Web team", new(2026, 5, 31, 0, 0, 0, TimeSpan.Zero))
    ];

    public static IReadOnlyList<RepoEntry> Repos { get; } =
    [
        new("fulfillment-api", "AMP-101", "Owns the customer-facing delivery estimate", "Fulfillment platform team",
            true, true, true, true, true, new(2027, 3, 31, 0, 0, 0, TimeSpan.Zero)),
        new("shipping-infra", "AMP-101", "Ingests and normalises carrier events", "Fulfillment platform team",
            true, true, true, false, true, new(2027, 2, 28, 0, 0, 0, TimeSpan.Zero)),
        new("orders-legacy", "AMP-101", "Legacy order lifecycle; being strangled", "Fulfillment platform team",
            false, true, false, false, false, new(2026, 4, 30, 0, 0, 0, TimeSpan.Zero)),
        new("billing-core", "AMP-204", "Invoice, tax and late-fee calculation", "Billing team",
            true, true, true, true, true, new(2027, 1, 31, 0, 0, 0, TimeSpan.Zero)),
        // Planted disagreement: Cortex still records the old owner after a transfer.
        new("legacy-invoicing", "AMP-204", "Historic invoicing batch jobs", "Billing team",
            false, true, false, false, true, new(2026, 3, 31, 0, 0, 0, TimeSpan.Zero)),
        new("checkout-web", "AMP-330", "Displays order status and checkout UI", "Web team",
            false, true, false, false, true, new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero)),
        // Planted disagreement: Cortex describes this as calculating estimates; the repo says it only displays them.
        new("order-status-ui", "AMP-330", "Renders the order-status page only; calculates nothing", "Web team",
            false, false, false, false, false, new(2026, 2, 28, 0, 0, 0, TimeSpan.Zero))
    ];

    public static IReadOnlyList<CortexClaim> CortexClaims { get; } =
    [
        new("fulfillment-api", "Owns the customer-facing delivery estimate", "Fulfillment platform team"),
        new("shipping-infra", "Ingests and normalises carrier events", "Fulfillment platform team"),
        new("orders-legacy", "Legacy order lifecycle; being strangled", "Fulfillment platform team"),
        new("billing-core", "Invoice, tax and late-fee calculation", "Billing team"),
        new("legacy-invoicing", "Historic invoicing batch jobs", "Payments team"),
        new("checkout-web", "Displays order status and checkout UI", "Web team"),
        new("order-status-ui", "Calculates and displays the delivery estimate", "Web team")
    ];
}

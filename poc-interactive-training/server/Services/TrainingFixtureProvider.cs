using System.Text;

namespace PocInteractiveTraining.Server.Services;

public sealed class TrainingFixtureProvider
{
    private static readonly string[] ArtifactPaths =
    [
        "README.md",
        "jira/FUL-1842.md",
        "okf/index.md",
        "okf/concepts/delivery-estimate.md",
        "okf/architecture/service-boundaries.md",
        "okf/decisions/adr-024-delay-source.md",
        "okf/code-map/fulfillment-components.md",
        "src/Fulfillment.Application/DeliveryEstimateService.cs",
        "src/Fulfillment.Application/GetOrderStatusHandler.cs",
        "src/Fulfillment.Application/DeliveryEstimateServiceTests.cs"
    ];

    private readonly string _fixtureRoot = Path.Combine(AppContext.BaseDirectory, "TrainingFixture");
    private readonly Lazy<string> _groundingPacket;

    public TrainingFixtureProvider()
    {
        _groundingPacket = new Lazy<string>(LoadGroundingPacket, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlySet<string> AllowedPaths { get; } = ArtifactPaths.ToHashSet(StringComparer.Ordinal);

    public string GroundingPacket => _groundingPacket.Value;

    public string BuildCoachRequest() => $$"""
        {{GroundingPacket}}

        <request>
        Author narration that teaches what OKF is, why it matters, how to inspect the tree, how the illustrated
        investigation follows evidence to code, why motion can clarify sequence, how a reviewed image supports
        but does not replace structured context, how to answer the checks, and how to write a grounded prompt.
        </request>
        """;

    public string BuildReviewRequest(string learnerPrompt) => $$"""
        {{GroundingPacket}}

        <rubric>
        Require index-first navigation; path citations for material claims; concept, boundary, ADR, and code-map
        traversal; implementation, caller, and tests; facts separated from inferences/open questions; no code
        before ownership and constraints; and an explicit verification plan.
        </rubric>

        <learner_prompt>
        {{learnerPrompt}}
        </learner_prompt>
        """;

    private string LoadGroundingPacket()
    {
        var packet = new StringBuilder();
        packet.AppendLine("<okf_training_fixture>");
        packet.AppendLine("The README is the consumer contract. Read it before interpreting the linked artifacts.");

        foreach (var relativePath in ArtifactPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(_fixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(_fixtureRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Required training fixture artifact is missing or outside the allowlist root: {relativePath}");
            }

            packet.AppendLine($"<artifact path=\"{relativePath}\">");
            packet.AppendLine(File.ReadAllText(fullPath));
            packet.AppendLine("</artifact>");
        }

        packet.AppendLine("</okf_training_fixture>");
        return packet.ToString();
    }
}
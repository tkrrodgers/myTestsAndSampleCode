using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Deterministic view of the three context tiers. It supplies the structure and the token cost of each
// tier; it never decides scope. That judgement is the model's job, and keeping the two apart is the
// point of the scene.
public sealed class PortfolioContextService
{
    private readonly TradingCorpus _corpus;
    private readonly EmbeddingGemmaEncoder _encoder;

    public PortfolioContextService(TradingCorpus corpus, EmbeddingGemmaEncoder encoder)
    {
        _corpus = corpus;
        _encoder = encoder;
    }

    public PortfolioReport Build()
    {
        var services = PortfolioTierSamples.Tier1Services
            .Select(service => new TierService(
                service.Name,
                service.AmpId,
                service.Domain,
                service.HandlesClientOrders,
                service.Health,
                service.Sla,
                service.DependsOn,
                PortfolioTierSamples.ServiceToRepository.GetValueOrDefault(service.Name)))
            .ToList();

        var amps = PortfolioTierSamples.Tier2Amps
            .Select(amp => new TierAmp(amp.AmpId, amp.Name, amp.Team, amp.RegulatoryPerimeter, amp.AssetClasses, amp.Repositories, amp.ScopeNote))
            .ToList();

        var repositories = TradingCorpus.Repos
            .Select(repo =>
            {
                var service = PortfolioTierSamples.ServiceToRepository.FirstOrDefault(pair => pair.Value == repo.Name).Key ?? repo.Name;
                var documents = _corpus.OkfDocuments.Where(document => document.Repo == repo.Name).ToList();
                return new TierRepository(
                    repo.Name,
                    services.FirstOrDefault(item => item.Repository == repo.Name)?.AmpId ?? "unknown",
                    service,
                    documents.Count,
                    _corpus.Files.Count(file => file.Repo == repo.Name),
                    documents.Select(document => document.Type).Distinct(StringComparer.Ordinal).OrderBy(type => type, StringComparer.Ordinal).ToList(),
                    documents.Count > 0);
            })
            .ToList();

        return new PortfolioReport(
            _corpus.IsAvailable,
            _corpus.StatusMessage,
            services,
            amps,
            repositories,
            MeasureCost(),
            BuildSharedDependencyWarning(services));
    }

    // The cost of progressive disclosure, stated in tokens: what the funnel actually saves.
    public TierCost MeasureCost()
    {
        var (tier1, exact) = _encoder.CountTokens(PortfolioTierSamples.Tier1Context());
        var tier2 = _encoder.CountTokens(PortfolioTierSamples.Tier2Context([])).Tokens;
        var inScope = TradingCorpus.Repos
            .Where(repo => repo.Name is "EquityTradingPipeline" or "FixedIncomeOptionsEngine")
            .Sum(repo => _encoder.CountTokens(_corpus.Tier3Context(repo.Name)).Tokens);
        var everything = TradingCorpus.Repos.Sum(repo => _encoder.CountTokens(_corpus.Tier3Context(repo.Name)).Tokens);
        return new TierCost(tier1, tier2, inScope, everything, exact);
    }

    // Surfaced deterministically because it is a structural fact, not an opinion: every trading service
    // shares one library, so a Tier-1-only reading will propose changing it.
    private static List<string> BuildSharedDependencyWarning(IReadOnlyList<TierService> services)
    {
        var orderHandlers = services.Where(service => service.HandlesClientOrders).ToList();
        var shared = orderHandlers
            .SelectMany(service => service.DependsOn)
            .GroupBy(dependency => dependency, StringComparer.Ordinal)
            .Where(group => group.Count() == orderHandlers.Count)
            .Select(group => group.Key)
            .ToList();

        if (shared.Count == 0)
        {
            return [];
        }

        return
        [
            $"All {orderHandlers.Count} order-handling services depend on {string.Join(" and ", shared)}.",
            "Tier 1 alone makes that look like the obvious place to add the fraud call — one change, every service covered.",
            "It is the wrong answer, and Tier 1 does not contain the fact that makes it wrong."
        ];
    }
}

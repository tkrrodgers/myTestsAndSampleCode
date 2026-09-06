using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Objective 6: score context readiness at three tiers and make the precedence rule explicit.
// R4 — Tier 3 (the repo) is closest to the code and is authoritative on specifics; Tier 1 exists for
// routing only. Staleness does not cascade downward: a fresh-looking parent never implies a fresh child.
public sealed class PortfolioContextService
{
    public PortfolioReport Build(DateTimeOffset asOf)
    {
        var repos = PortfolioSamples.Repos.Select(repo =>
        {
            var (score, present, missing) = ScoreRepo(repo);
            var stale = repo.StaleAfter <= asOf;
            return new PortfolioRepo(
                repo.Name,
                repo.AmpId,
                repo.RepoPurpose,
                repo.RepoOwner,
                score,
                stale,
                repo.StaleAfter.ToString("yyyy-MM-dd"),
                present,
                missing);
        }).ToList();

        var amps = PortfolioSamples.Amps.Select(amp =>
        {
            var members = repos.Where(repo => repo.AmpId == amp.AmpId).ToList();
            return new PortfolioAmp(
                amp.AmpId,
                amp.Name,
                amp.Convention,
                members.Count == 0 ? 0 : (int)Math.Round(members.Average(member => member.Score)),
                members.Count == 0 ? 0 : members.Min(member => member.Score),
                amp.StaleAfter <= asOf,
                amp.StaleAfter.ToString("yyyy-MM-dd"),
                members.Count,
                members.Count(member => member.Stale));
        }).ToList();

        var conflicts = FindConflicts(repos);
        var cascades = FindStalenessCascades(repos, amps, asOf);

        // The portfolio number is the weakest link, not the average: an agent routed to the worst repo
        // gets the worst experience, and an average hides exactly that.
        var portfolioScore = repos.Count == 0 ? 0 : (int)Math.Round(repos.Average(repo => repo.Score));
        var weakest = repos.OrderBy(repo => repo.Score).FirstOrDefault();

        return new PortfolioReport(
            PortfolioSamples.CortexName,
            portfolioScore,
            weakest?.Score ?? 0,
            weakest?.Name ?? "n/a",
            PortfolioSamples.CortexStaleAfter <= asOf,
            PortfolioSamples.CortexStaleAfter.ToString("yyyy-MM-dd"),
            amps,
            repos,
            conflicts,
            cascades);
    }

    private static (int Score, List<string> Present, List<string> Missing) ScoreRepo(PortfolioSamples.RepoEntry repo)
    {
        var artifacts = new (string Name, bool Has, int Weight)[]
        {
            ("OKF bundle", repo.HasOkf, 30),
            ("README", repo.HasReadme, 15),
            ("ADRs", repo.HasAdr, 20),
            ("Code map", repo.HasCodeMap, 20),
            ("Tests", repo.HasTests, 15)
        };

        var score = artifacts.Where(artifact => artifact.Has).Sum(artifact => artifact.Weight);
        return (score,
            artifacts.Where(artifact => artifact.Has).Select(artifact => artifact.Name).ToList(),
            artifacts.Where(artifact => !artifact.Has).Select(artifact => artifact.Name).ToList());
    }

    private static List<TierConflict> FindConflicts(List<PortfolioRepo> repos)
    {
        var conflicts = new List<TierConflict>();
        foreach (var claim in PortfolioSamples.CortexClaims)
        {
            var repo = repos.FirstOrDefault(candidate => candidate.Name == claim.Repo);
            if (repo is null)
            {
                continue;
            }

            if (!string.Equals(claim.ClaimedOwner, repo.Owner, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new TierConflict(
                    repo.Name,
                    "Ownership",
                    claim.ClaimedOwner,
                    repo.Owner,
                    "Tier 3 wins. The repo is closest to the code; update the Cortex entry, do not 'correct' the repo."));
            }

            if (!string.Equals(claim.ClaimedPurpose, repo.Purpose, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new TierConflict(
                    repo.Name,
                    "Purpose",
                    claim.ClaimedPurpose,
                    repo.Purpose,
                    "Tier 3 wins on specifics. Routing an agent on the Tier 1 description would send it to the wrong repo."));
            }
        }

        return conflicts;
    }

    private static List<string> FindStalenessCascades(List<PortfolioRepo> repos, List<PortfolioAmp> amps, DateTimeOffset asOf)
    {
        var cascades = new List<string>();

        foreach (var amp in amps.Where(amp => !amp.Stale && amp.StaleRepos > 0))
        {
            cascades.Add($"{amp.AmpId} ({amp.Name}) looks fresh until {amp.StaleAfter}, but {amp.StaleRepos} of its {amp.RepoCount} repos are already stale. Freshness does not cascade downward.");
        }

        if (PortfolioSamples.CortexStaleAfter <= asOf)
        {
            cascades.Add($"The Cortex tier itself expired on {PortfolioSamples.CortexStaleAfter:yyyy-MM-dd}. Portfolio-level routing is running on knowledge nobody has re-verified.");
        }

        foreach (var repo in repos.Where(repo => repo.Stale && repo.Score < 50))
        {
            cascades.Add($"{repo.Name} is both stale (since {repo.StaleAfter}) and thin ({repo.Score}/100). An agent sent here has neither current nor sufficient context.");
        }

        return cascades;
    }
}

using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Objective 5: find duplicated logic across a real multi-repo estate and size the consolidation prize.
// Clustering runs on real embeddinggemma vectors — the encoder is already loaded for the context audit,
// so a portfolio-scale capability costs no new infrastructure.
public sealed class CommonCodeAuditService
{
    // Measured with --calibrate-corpus, not guessed. On this corpus the 21 known cross-repo duplicate
    // pairs span 0.8684-0.9588 and the 342 unrelated pairs top out at 0.8576, so 0.863 sits in the gap.
    // The margin is only 0.011 wide and the nearest miss is a genuine near-relative (two clearing
    // services), so re-measure before pointing this at a different estate.
    private const double SimilarityThreshold = 0.863;

    private readonly EmbeddingGemmaEncoder _encoder;
    private readonly TradingCorpus _corpus;
    private IReadOnlyList<float[]?>? _vectors;

    public CommonCodeAuditService(EmbeddingGemmaEncoder encoder, TradingCorpus corpus)
    {
        _encoder = encoder;
        _corpus = corpus;
    }

    private IReadOnlyList<float[]?> Vectors => _vectors ??= _corpus.Files
        .Select(file => _encoder.IsAvailable ? _encoder.EncodeDocument(file.Source) : null)
        .ToList();

    // Diagnostic used to calibrate the threshold against the real distribution rather than a guess.
    public IReadOnlyList<SimilarityPair> AllPairs()
    {
        var files = _corpus.Files;
        var vectors = Vectors;
        var pairs = new List<SimilarityPair>();
        for (var i = 0; i < files.Count; i++)
        {
            for (var j = i + 1; j < files.Count; j++)
            {
                if (vectors[i] is null || vectors[j] is null)
                {
                    continue;
                }

                pairs.Add(new SimilarityPair(
                    $"{files[i].Repo}/{files[i].Name}",
                    $"{files[j].Repo}/{files[j].Name}",
                    Math.Round(EmbeddingGemmaEncoder.CosineSimilarity(vectors[i]!, vectors[j]!), 4),
                    files[i].Repo != files[j].Repo));
            }
        }

        return pairs.OrderByDescending(pair => pair.Similarity).ToList();
    }

    public CommonCodeReport Analyse()
    {
        var files = _corpus.Files;
        if (files.Count == 0)
        {
            return new CommonCodeReport(false, false, _corpus.StatusMessage, SimilarityThreshold, 0, [], new SeparationMetrics(0, 0, 0, 0, 0, 0, 0), [], 0, 0, 0,
                ["The corpus is unavailable, so nothing was measured. Point the server at the repositories before drawing conclusions."], []);
        }

        var vectors = Vectors;
        var usedEmbeddings = vectors.All(vector => vector is not null);

        double Similarity(int i, int j) => usedEmbeddings
            ? EmbeddingGemmaEncoder.CosineSimilarity(vectors[i]!, vectors[j]!)
            : StructuralSimilarity(files[i].Source, files[j].Source);

        var parent = Enumerable.Range(0, files.Count).ToArray();
        int Find(int index) => parent[index] == index ? index : parent[index] = Find(parent[index]);
        void Union(int left, int right)
        {
            var a = Find(left);
            var b = Find(right);
            if (a != b)
            {
                parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }

        var nonFunctionalCross = new List<double>();
        var functionalCross = new List<double>();
        for (var i = 0; i < files.Count; i++)
        {
            for (var j = i + 1; j < files.Count; j++)
            {
                var similarity = Similarity(i, j);
                if (files[i].Repo != files[j].Repo)
                {
                    // Same class name across repos is the known-duplicate set; everything else is the floor.
                    (files[i].Name == files[j].Name ? nonFunctionalCross : functionalCross).Add(similarity);
                }

                if (similarity >= SimilarityThreshold)
                {
                    Union(i, j);
                }
            }
        }

        var clusters = new List<CommonCodeCluster>();
        foreach (var group in Enumerable.Range(0, files.Count).GroupBy(Find).OrderBy(group => group.Key))
        {
            var members = group.Select(index => files[index]).ToList();
            var repos = members.Select(member => member.Repo).Distinct(StringComparer.Ordinal).OrderBy(repo => repo, StringComparer.Ordinal).ToList();
            var lines = members.Select(member => member.Lines).ToList();

            // What consolidation would actually save: keep one implementation, retire the rest.
            var duplicateLines = lines.Sum() - lines.Max();
            var kind = members.Count(member => member.Role == "Non-functional") >= members.Count(member => member.Role == "Functional")
                ? "Non-functional"
                : "Functional";

            clusters.Add(new CommonCodeCluster(
                members.Count > 1 ? $"{members[0].Name} x{members.Count}" : members[0].Name,
                kind,
                members.Select(member => new CommonCodeMember(member.Name, member.Repo, member.Lines)).ToList(),
                repos,
                duplicateLines,
                members.Count > 1 && repos.Count > 1,
                Recommend(kind, members.Count, repos.Count)));
        }

        var duplicated = clusters.Where(cluster => cluster.IsDuplication).ToList();
        var functionalPrize = duplicated.Where(cluster => cluster.Kind == "Functional").Sum(cluster => cluster.DuplicateLines);
        var nonFunctionalPrize = duplicated.Where(cluster => cluster.Kind == "Non-functional").Sum(cluster => cluster.DuplicateLines);

        return new CommonCodeReport(
            usedEmbeddings,
            true,
            _corpus.StatusMessage,
            SimilarityThreshold,
            files.Count,
            BuildRepoSummary(),
            BuildSeparation(nonFunctionalCross, functionalCross, Similarity),
            clusters.OrderByDescending(cluster => cluster.IsDuplication).ThenByDescending(cluster => cluster.DuplicateLines).ToList(),
            duplicated.Count,
            functionalPrize,
            nonFunctionalPrize,
            BuildStrategy(duplicated),
            BuildBoundaryView());
    }

    // The interesting pairs are the ones either side of the threshold, not the obvious top of the list.
    private IReadOnlyList<SimilarityPair> BuildBoundaryView()
    {
        var crossRepo = AllPairs().Where(pair => pair.CrossRepo).ToList();
        var clustered = crossRepo.Where(pair => pair.Similarity >= SimilarityThreshold).ToList();
        var rejected = crossRepo.Where(pair => pair.Similarity < SimilarityThreshold).ToList();
        return clustered.Take(3)
            .Concat(clustered.TakeLast(3))
            .Concat(rejected.Take(4))
            .Distinct()
            .OrderByDescending(pair => pair.Similarity)
            .ToList();
    }

    private IReadOnlyList<CorpusRepo> BuildRepoSummary() => TradingCorpus.Repos
        .Select(repo =>
        {
            var owned = _corpus.Files.Where(file => file.Repo == repo.Name).ToList();
            return new CorpusRepo(
                repo.Name,
                repo.AssetClass,
                repo.Divergence,
                repo.OkfQuality,
                owned.Count,
                owned.Sum(file => file.Lines),
                owned.Count(file => file.Role == "Non-functional"),
                owned.Count(file => file.Role == "Functional"));
        })
        .Where(repo => repo.Files > 0)
        .ToList();

    // The headline claim is that the model separates duplicates from near-relatives. Score it, don't assert it.
    private SeparationMetrics BuildSeparation(List<double> duplicatePairs, List<double> unrelatedPairs, Func<int, int, double> similarity)
    {
        var files = _corpus.Files;
        var hits = 0;
        var total = 0;
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Role != "Non-functional")
            {
                continue;
            }

            total++;
            var best = -1;
            var bestScore = double.MinValue;
            for (var j = 0; j < files.Count; j++)
            {
                if (i == j || files[j].Repo == files[i].Repo)
                {
                    continue;
                }

                var score = similarity(Math.Min(i, j), Math.Max(i, j));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = j;
                }
            }

            if (best >= 0 && string.Equals(files[best].Name, files[i].Name, StringComparison.Ordinal))
            {
                hits++;
            }
        }

        var duplicateMin = duplicatePairs.Count > 0 ? duplicatePairs.Min() : 0;
        var unrelatedMax = unrelatedPairs.Count > 0 ? unrelatedPairs.Max() : 0;
        return new SeparationMetrics(
            Math.Round(duplicatePairs.Count > 0 ? duplicatePairs.Average() : 0, 4),
            Math.Round(duplicateMin, 4),
            Math.Round(unrelatedPairs.Count > 0 ? unrelatedPairs.Average() : 0, 4),
            Math.Round(unrelatedMax, 4),
            Math.Round(duplicateMin - unrelatedMax, 4),
            hits,
            total);
    }

    private static string Recommend(string kind, int memberCount, int repoCount)
    {
        if (memberCount < 2 || repoCount < 2)
        {
            return "Single implementation — nothing to consolidate.";
        }

        return kind == "Non-functional"
            ? $"Extract to a shared platform package. {repoCount} services maintain their own copy of plumbing that has one correct answer."
            : $"Consolidate behind one owning service. {repoCount} services encode the same business rule, so a policy change today requires {repoCount} coordinated edits — and any missed one is a silent divergence.";
    }

    private static List<string> BuildStrategy(List<CommonCodeCluster> duplicated)
    {
        if (duplicated.Count == 0)
        {
            return ["No cross-repo duplication found at this threshold."];
        }

        return
        [
            "Start with the non-functional clusters: plumbing has one correct answer, no business owner to negotiate with, and the lowest risk to consolidate.",
            "Functional duplication is the higher prize and the higher risk. Each cluster needs a single owning service and a golden-baseline test before anything is deleted.",
            "Do not consolidate on similarity alone — confirm the behaviours are genuinely identical. Near-duplicates often differ in a rounding rule or a boundary, and merging them silently picks a winner.",
            "Sequence it behind the repo audit: consolidating into a repo that is itself context-poor moves the problem rather than fixing it."
        ];
    }

    // Fallback only: token overlap, used when the embedding model is unavailable.
    private static double StructuralSimilarity(string left, string right)
    {
        static HashSet<string> Tokens(string text) => text
            .Split([' ', '\n', '\r', '\t', '(', ')', '{', '}', ';', ',', '.', '='], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        return (double)a.Intersect(b, StringComparer.Ordinal).Count() / a.Union(b, StringComparer.Ordinal).Count();
    }
}

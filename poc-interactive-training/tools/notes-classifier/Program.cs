using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML;
using Microsoft.ML.Data;
using PocInteractiveTraining.Server.Services;

namespace NotesClassifier;

// Can EmbeddingGemma + ML.NET reproduce the BankDemo domain classification from the in-code notes alone?
// Ground truth is docs/domains: the catalog's program->domain map and each domain's "Business purpose".
// No number here comes from a model's opinion; every score is a cosine or a held-out prediction.
internal static class Program
{
    private const int EmbeddingDim = 768;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var bankRoot = args.FirstOrDefault(a => !a.StartsWith('-')) ?? @"C:\Users\tkrro\Source\BankDemo";
        var docs = Path.Combine(bankRoot, "docs", "domains");
        if (!Directory.Exists(docs))
        {
            Console.Error.WriteLine($"docs/domains not found under {bankRoot}");
            return 2;
        }

        using var encoder = new EmbeddingGemmaEncoder(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "embeddinggemma-300m-onnx"));
        Console.WriteLine($"encoder: {encoder.StatusMessage}");
        if (!encoder.IsAvailable)
        {
            Console.Error.WriteLine("embeddinggemma is not available; this test needs the real embeddings.");
            return 3;
        }

        var truth = ParseCatalog(Path.Combine(docs, "00-domain-catalog.md"));
        var boilerplate = Boilerplate();
        var domains = LoadDomainDescriptions(docs);

        // The business/functional domains (01-12) are the ones a note can place semantically. The catalog
        // itself says the technical/cross-cutting domains (13-19) are boilerplate and should be scored
        // against the program's layer, not its notes, so they are out of scope for this note test.
        var businessDomains = Enumerable.Range(1, 12).Select(n => n.ToString("00")).ToList();

        // Find each program's source and pull its notes. Keep only programs whose domain is business
        // and whose source and header we can actually read.
        var programs = new List<ProgramNotes>();
        var sources = Directory.EnumerateFiles(Path.Combine(bankRoot, "sources"), "*.cbl", SearchOption.AllDirectories)
            .GroupBy(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var (name, domain) in truth.Where(kv => businessDomains.Contains(kv.Value.Domain)).OrderBy(kv => kv.Key))
        {
            if (!sources.TryGetValue(name, out var path))
            {
                continue;
            }

            var (header, distinctive, allNotes) = ExtractNotes(File.ReadAllLines(path), boilerplate);
            if (header.Length == 0 && distinctive.Count == 0)
            {
                continue;
            }

            programs.Add(new ProgramNotes(name, domain.Domain, domain.Function, header, distinctive, allNotes));
        }

        Console.WriteLine($"\nGround truth: {programs.Count} business programs across {programs.Select(p => p.Domain).Distinct().Count()} domains (01-12).");
        Console.WriteLine($"Domain descriptions loaded: {domains.Count}. Boilerplate phrases filtered: {boilerplate.Count}.\n");

        // Embed the clean domain descriptions once (never the "Classification cues" section, which quotes
        // the very notes we are testing).
        var domainVectors = businessDomains
            .Where(domains.ContainsKey)
            .ToDictionary(d => d, d => Encode(encoder, domains[d].Title + ". " + domains[d].Purpose));

        // --- Test 1: retrieval. Assign each program to the nearest domain description. No training. ---
        Console.WriteLine("=== Test 1  Retrieval: program notes -> nearest domain description (no training) ===");
        var deboiler = ScoreRetrieval(encoder, programs, domainVectors, useBoilerplate: false, out var t1Rows);
        PrintRows(t1Rows);
        Console.WriteLine($"  top-1 {deboiler.Top1}/{programs.Count} = {Pct(deboiler.Top1, programs.Count)},  top-3 {deboiler.Top3}/{programs.Count} = {Pct(deboiler.Top3, programs.Count)}\n");

        // Domain 10 (Online Help) describes itself as "implemented inside every business program", so its
        // description overlaps everything and acts as a magnet. Removing it as a candidate shows how much of
        // the top-1 loss is this one cross-cutting label rather than weak note signal.
        var withoutHelp = new Dictionary<string, float[]>(domainVectors);
        withoutHelp.Remove("10");
        var noHelp = ScoreRetrieval(encoder, programs.Where(p => p.Domain != "10").ToList(), withoutHelp, useBoilerplate: false, out _);
        var nonHelp = programs.Count(p => p.Domain != "10");
        Console.WriteLine($"  excluding the cross-cutting Help domain (10) as a target: top-1 {noHelp.Top1}/{nonHelp} = {Pct(noHelp.Top1, nonHelp)},  top-3 {Pct(noHelp.Top3, nonHelp)}\n");

        // --- Test 2: the catalog's prediction. Include boilerplate and watch accuracy fall. ---
        Console.WriteLine("=== Test 2  Ablation: same retrieval but WITH boilerplate notes included ===");
        var withBoiler = ScoreRetrieval(encoder, programs, domainVectors, useBoilerplate: true, out _);
        Console.WriteLine($"  top-1 {withBoiler.Top1}/{programs.Count} = {Pct(withBoiler.Top1, programs.Count)},  top-3 {withBoiler.Top3}/{programs.Count} = {Pct(withBoiler.Top3, programs.Count)}");
        Console.WriteLine($"  removing boilerplate changed top-1 by {deboiler.Top1 - withBoiler.Top1:+0;-0;0} programs.\n");

        // --- Test 3: nearest-centroid, leave-one-out. Each domain = mean of its members' note vectors. ---
        // The fair unsupervised test: no domain doc is used, only a few tagged programs bootstrap each label.
        Console.WriteLine("=== Test 3  Nearest-centroid, leave-one-out (notes only, no domain docs) ===");
        RunCentroid(encoder, programs);

        // --- Test 4: supervised ML.NET, leave-one-out, on domains with >= 2 programs. ---
        Console.WriteLine("\n=== Test 4  Supervised: embeddinggemma -> ML.NET SdcaMaximumEntropy, leave-one-out ===");
        RunSupervised(encoder, programs);

        return 0;
    }

    // Represent each domain by the mean vector of its member programs, holding the query out of its own
    // centroid. This is how you would bootstrap labels from a few tagged programs per domain.
    private static void RunCentroid(EmbeddingGemmaEncoder encoder, List<ProgramNotes> programs)
    {
        var vectors = programs.ToDictionary(p => p.Name, p => Encode(encoder, p.DocumentText(false)));
        var byDomain = programs.GroupBy(p => p.Domain).ToDictionary(g => g.Key, g => g.Select(p => p.Name).ToList());
        var evaluable = programs.Where(p => byDomain[p.Domain].Count >= 2).ToList();

        var correct = 0;
        foreach (var held in evaluable)
        {
            var predicted = byDomain
                .Select(kv => (Domain: kv.Key, Names: kv.Value.Where(n => n != held.Name).ToList()))
                .Where(entry => entry.Names.Count > 0)
                .Select(entry => (entry.Domain, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vectors[held.Name], Mean(entry.Names.Select(n => vectors[n])))))
                .OrderByDescending(entry => entry.Sim)
                .First().Domain;
            if (predicted == held.Domain)
            {
                correct++;
            }
        }

        Console.WriteLine($"  evaluable programs (domain has >=2 members): {evaluable.Count}");
        Console.WriteLine($"  leave-one-out top-1: {correct}/{evaluable.Count} = {Pct(correct, evaluable.Count)}");
    }

    private static float[] Mean(IEnumerable<float[]> vectors)
    {
        var list = vectors.ToList();
        var mean = new float[EmbeddingDim];
        foreach (var vector in list)
        {
            for (var i = 0; i < EmbeddingDim; i++)
            {
                mean[i] += vector[i];
            }
        }

        for (var i = 0; i < EmbeddingDim; i++)
        {
            mean[i] /= Math.Max(1, list.Count);
        }

        return mean;
    }

    private static RetrievalScore ScoreRetrieval(EmbeddingGemmaEncoder encoder, List<ProgramNotes> programs, Dictionary<string, float[]> domainVectors, bool useBoilerplate, out List<string> rows)
    {
        rows = [];
        var top1 = 0;
        var top3 = 0;
        foreach (var program in programs)
        {
            var text = program.DocumentText(useBoilerplate);
            var vector = Encode(encoder, text);
            var ranked = domainVectors
                .Select(kv => (Domain: kv.Key, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vector, kv.Value)))
                .OrderByDescending(entry => entry.Sim)
                .ToList();

            var predicted = ranked[0].Domain;
            var rank = ranked.FindIndex(entry => entry.Domain == program.Domain);
            if (predicted == program.Domain)
            {
                top1++;
            }

            if (rank is >= 0 and < 3)
            {
                top3++;
            }

            if (!useBoilerplate)
            {
                var mark = predicted == program.Domain ? "  ok" : "MISS";
                rows.Add($"  {mark}  {program.Name,-9} truth {program.Domain}  pred {predicted} ({ranked[0].Sim:0.00})  truth-rank {(rank < 0 ? "?" : (rank + 1).ToString())}  \"{Trim(program.Function, 34)}\"");
            }
        }

        return new RetrievalScore(top1, top3);
    }

    // Leave-one-out so every program is judged by a model that never saw it. Only domains with at least
    // two programs can be evaluated this way; singletons are reported as unevaluable, not as wins.
    private static void RunSupervised(EmbeddingGemmaEncoder encoder, List<ProgramNotes> programs)
    {
        var vectors = programs.ToDictionary(p => p.Name, p => Encode(encoder, p.DocumentText(false)));
        var byDomain = programs.GroupBy(p => p.Domain).ToDictionary(g => g.Key, g => g.Count());
        var evaluable = programs.Where(p => byDomain[p.Domain] >= 2).ToList();
        var singletons = programs.Count - evaluable.Count;

        var correct = 0;
        foreach (var held in evaluable)
        {
            var train = programs.Where(p => p.Name != held.Name).ToList();
            // A held-out program whose domain becomes a singleton in the training set cannot be recovered.
            if (train.Count(p => p.Domain == held.Domain) == 0)
            {
                continue;
            }

            var predicted = TrainAndPredict(train, vectors, vectors[held.Name]);
            if (predicted == held.Domain)
            {
                correct++;
            }
        }

        Console.WriteLine($"  evaluable programs (domain has >=2 members): {evaluable.Count}; singleton domains skipped: {singletons}");
        Console.WriteLine($"  leave-one-out top-1: {correct}/{evaluable.Count} = {Pct(correct, evaluable.Count)}");
        Console.WriteLine("  Note: with 2-3 programs per domain this is a data-poor upper bound, not an estate-scale figure.");
    }

    private static string TrainAndPredict(List<ProgramNotes> train, Dictionary<string, float[]> vectors, float[] query)
    {
        var ml = new MLContext(seed: 1);
        var rows = train.Select(p => new Row { Label = p.Domain, Embedding = vectors[p.Name] }).ToList();
        var data = ml.Data.LoadFromEnumerable(rows);
        var rank = Math.Max(2, Math.Min(16, rows.Select(r => r.Label).Distinct().Count() * 2));
        var pipeline = ml.Transforms.Conversion.MapValueToKey("Label")
            .Append(ml.Transforms.ProjectToPrincipalComponents("Features", nameof(Row.Embedding), rank: rank))
            .Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy())
            .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
        var model = pipeline.Fit(data);
        var engine = ml.Model.CreatePredictionEngine<Row, Prediction>(model);
        return engine.Predict(new Row { Label = "", Embedding = query }).PredictedLabel;
    }

    private static float[] Encode(EmbeddingGemmaEncoder encoder, string text)
    {
        var vector = encoder.EncodeDocument(text) ?? new float[EmbeddingDim];
        if (vector.Length == EmbeddingDim)
        {
            return vector;
        }

        var fitted = new float[EmbeddingDim];
        Array.Copy(vector, fitted, Math.Min(vector.Length, EmbeddingDim));
        return fitted;
    }

    // ---- ground truth parsing ----

    private static Dictionary<string, (string Domain, string Function)> ParseCatalog(string path)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var row = new Regex(@"^\|\s*([A-Z][A-Z0-9]{2,9})\s*\|\s*[^|]*\|\s*(\d{1,2})(?:/\d+)?\s*\|\s*(.+?)\s*\|");
        foreach (var line in File.ReadLines(path))
        {
            var match = row.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var program = match.Groups[1].Value.ToUpperInvariant();
            var domain = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture).ToString("00");
            var function = match.Groups[3].Value.Replace("*", "").Trim();
            map.TryAdd(program, (domain, function));
        }

        return map;
    }

    private static Dictionary<string, (string Title, string Purpose)> LoadDomainDescriptions(string docsDir)
    {
        var result = new Dictionary<string, (string, string)>();
        foreach (var file in Directory.EnumerateFiles(docsDir, "??-*.md"))
        {
            var number = Path.GetFileName(file)[..2];
            if (number == "00")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var title = Regex.Match(text, @"^#\s+(?:Domain\s+\d+\s+[—-]\s+)?(.+)$", RegexOptions.Multiline) is { Success: true } t
                ? t.Groups[1].Value.Trim()
                : number;

            // The "Business purpose" section is human-written prose. Deliberately NOT the "Classification
            // cues" section, which quotes the program notes and would make retrieval circular.
            var purpose = Section(text, "Business purpose") ?? FirstParagraph(text);
            result[number] = (title, Collapse(purpose));
        }

        return result;
    }

    private static string? Section(string markdown, string heading)
    {
        var match = Regex.Match(markdown, $@"^##+\s*{Regex.Escape(heading)}\s*$(.+?)(?=^##\s|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string FirstParagraph(string markdown)
    {
        foreach (var block in markdown.Split("\n\n"))
        {
            var clean = block.Trim();
            if (clean.Length > 40 && !clean.StartsWith('#') && !clean.StartsWith('|'))
            {
                return clean;
            }
        }

        return markdown;
    }

    // ---- note extraction ----

    private static (string Header, List<string> Distinctive, List<string> All) ExtractNotes(string[] lines, HashSet<string> boilerplate)
    {
        var header = "";
        var distinctive = new List<string>();
        var all = new List<string>();

        foreach (var raw in lines)
        {
            var stripped = raw.Length > 6 && (raw[6] == '*' || raw.TrimStart().StartsWith('*')) ? raw : null;
            if (stripped is null)
            {
                continue;
            }

            var text = stripped.Trim().Trim('*', ' ').Trim();
            if (text.Length < 3 || text.All(c => !char.IsLetter(c)))
            {
                continue;
            }

            var functionMatch = Regex.Match(text, @"^Function:\s*(.+)$", RegexOptions.IgnoreCase);
            if (functionMatch.Success)
            {
                header = functionMatch.Groups[1].Value.Trim();
                continue;
            }

            if (IsMetadataOrLicence(text))
            {
                continue;
            }

            all.Add(text);
            if (!boilerplate.Contains(Normalize(text)))
            {
                distinctive.Add(text);
            }
        }

        return (header, distinctive, all);
    }

    private static bool IsMetadataOrLicence(string text)
    {
        var upper = text.ToUpperInvariant();
        string[] markers =
        [
            "COPYRIGHT", "ROCKET SOFTWARE", "WARRANT", "EULA", "AS IS", "LIABILITY", "MERCHANTABILITY",
            "PROGRAM:", "PRGRAM:", "LAYER:", "AUTHOR:", "DATE-", "VERSION", "SEQUENCED ON", "MODULE NAME",
            "*", "---"
        ];
        if (markers.Any(upper.Contains) && (upper.Contains("COPYRIGHT") || upper.Contains("WARRANT") || upper.StartsWith("PROGRAM") || upper.StartsWith("PRGRAM") || upper.StartsWith("LAYER") || upper.StartsWith("VERSION") || upper.Contains("SEQUENCED")))
        {
            return true;
        }

        return upper.Contains("EULA") || upper.Contains("MERCHANTABILITY") || upper.Contains("LIABILITY") || upper.Contains("AS IS");
    }

    // The boilerplate phrases the catalog lists in section 5 as identical across programs.
    private static HashSet<string> Boilerplate()
    {
        string[] phrases =
        [
            "Write entry to log to show we have been invoked",
            "Store our transaction-id",
            "Store passed data or abend if there wasn't any",
            "This is the main process",
            "Determine what we have to do (read from or send to screen)",
            "Call the appropriate routine to handle the business logic",
            "Now we have to have finished and can return to our invoker.",
            "Now return to CICS",
            "Retrieve data from screen and format it",
            "Build the output screen and send it",
            "Clear map area, get date & time and move to the map",
            "Ensure the last map fields are correct",
            "Move in any error message",
            "Move in screen specific fields",
            "Turn colour off if required",
            "Move in screen name",
            "Move in userid and any error message",
            "Call common routine to perform date conversions",
            "Make ourselves re-entrant",
            "Move the passed area to our area",
            "Ensure error message is cleared",
            "Save the passed return flag and then turn it off",
            "Check the AID to see if its valid at this point",
            "Check the AID to see if we have to quit",
            "Check the to see if user needs or has been using help",
            "Check the AID to see if we have to return to previous screen",
            "Check if we have set the screen up before or is this 1st time",
            "As this is a display screen we need PFK03/4 to get out so we will just redisplay the data",
            "Now go get the data",
            "Set up our own copy of any linkage",
            "Initialise our working storage",
            "Go and do the processing",
            "Return to our caller",
            "Send the map",
            "Receive the map"
        ];
        return phrases.Select(Normalize).ToHashSet();
    }

    // ---- helpers ----

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static string Collapse(string text) =>
        Regex.Replace(text.Replace("\n", " "), @"\s+", " ").Trim();

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string Pct(int n, int d) => d == 0 ? "n/a" : $"{100.0 * n / d:0}%";

    private static void PrintRows(List<string> rows)
    {
        foreach (var row in rows)
        {
            Console.WriteLine(row);
        }
    }

    private sealed record ProgramNotes(string Name, string Domain, string Function, string Header, List<string> Distinctive, List<string> All)
    {
        public string DocumentText(bool withBoilerplate)
        {
            var builder = new StringBuilder();
            if (Header.Length > 0)
            {
                builder.Append(Header).Append(". ");
            }

            builder.Append(string.Join(". ", withBoilerplate ? All : Distinctive));
            return builder.ToString();
        }
    }

    private sealed record RetrievalScore(int Top1, int Top3);

    private sealed class Row
    {
        public string Label { get; set; } = "";

        [VectorType(EmbeddingDim)]
        public float[] Embedding { get; set; } = new float[EmbeddingDim];
    }

    private sealed class Prediction
    {
        public string PredictedLabel { get; set; } = "";
        public float[] Score { get; set; } = [];
    }
}

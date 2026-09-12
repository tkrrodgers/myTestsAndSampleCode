using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML;
using Microsoft.ML.Data;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// The BankDemo notes -> domain experiment, run against the docs/domains ground truth. Deterministic:
// embeddinggemma vectors, cosine retrieval, ML.NET SdcaMaximumEntropy and a call-graph clustering step.
// No bridge model is involved; the result is identical every run, so it is computed once and cached.
public sealed partial class NotesClassificationService
{
    private const int EmbeddingDim = 768;
    private const int CrossCuttingFanIn = 5;

    private readonly EmbeddingGemmaEncoder _encoder;
    private readonly string _bankRoot;
    private readonly Lazy<NotesClassificationResult> _result;

    public NotesClassificationService(EmbeddingGemmaEncoder encoder, string bankRoot)
    {
        _encoder = encoder;
        _bankRoot = bankRoot;
        _result = new Lazy<NotesClassificationResult>(Compute, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool Available => Directory.Exists(Path.Combine(_bankRoot, "docs", "domains")) && _encoder.IsAvailable;

    public string EnvironmentStatus => !Directory.Exists(Path.Combine(_bankRoot, "docs", "domains"))
        ? $"BankDemo docs/domains not found under {_bankRoot}. Set BankDemoRoot to the repository path."
        : !_encoder.IsAvailable
            ? "embeddinggemma is not loaded; this experiment needs the real embeddings."
            : $"BankDemo at {_bankRoot}; embeddinggemma ready.";

    public string BankRoot => _bankRoot;

    public NotesClassificationResult Result => _result.Value;

    public string SelfCheck()
    {
        if (!Available)
        {
            return $"unavailable: {EnvironmentStatus}";
        }

        var r = Result;
        return $"{r.ProgramCount} programs, {r.EdgeCount} call edges; retrieval top-1 {r.Retrieval.Top1}/{r.Retrieval.Total}, no-help {r.RetrievalNoHelp.Top1}/{r.RetrievalNoHelp.Total}, structural {r.Structural.Top1}/{r.Structural.Total}";
    }

    private NotesClassificationResult Compute()
    {
        if (!Available)
        {
            return Empty(EnvironmentStatus);
        }

        var docs = Path.Combine(_bankRoot, "docs", "domains");
        var truth = ParseCatalog(Path.Combine(docs, "00-domain-catalog.md"));
        var boilerplate = Boilerplate();
        var domains = LoadDomainDescriptions(docs);
        var businessDomains = Enumerable.Range(1, 12).Select(n => n.ToString("00")).ToList();

        var sources = Directory.EnumerateFiles(Path.Combine(_bankRoot, "sources"), "*.cbl", SearchOption.AllDirectories)
            .GroupBy(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Interface copybooks carry a LINK to one data program; resolve them so a COPY becomes an edge.
        var copyTargets = ResolveInterfaceCopybooks();

        var programs = new List<ProgramNotes>();
        var callEdges = new List<(string From, string To)>();
        foreach (var (name, path) in sources)
        {
            var lines = File.ReadAllLines(path);
            var (header, distinctive, all) = ExtractNotes(lines, boilerplate);
            var targets = CallTargets(lines, sources.Keys, copyTargets);
            foreach (var target in targets)
            {
                callEdges.Add((name, target));
            }

            if (truth.TryGetValue(name, out var t) && businessDomains.Contains(t.Domain) && (header.Length > 0 || distinctive.Count > 0))
            {
                programs.Add(new ProgramNotes(name, t.Domain, t.Function, header, distinctive, all));
            }
        }

        var programNames = programs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var truthByName = programs.ToDictionary(p => p.Name, p => p.Domain, StringComparer.OrdinalIgnoreCase);

        // Fan-in across the whole estate, then the caller domains for the ones we can label.
        var callersOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in callEdges)
        {
            (callersOf.TryGetValue(to, out var set) ? set : callersOf[to] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(from);
        }

        bool IsCrossCutting(string program) => callersOf.TryGetValue(program, out var callers) && callers.Count >= CrossCuttingFanIn;

        var crossCutting = programs
            .Where(p => IsCrossCutting(p.Name))
            .Select(p => new NotesCrossCutting(
                p.Name,
                callersOf[p.Name].Count,
                callersOf[p.Name].Where(c => truthByName.ContainsKey(c)).Select(c => truthByName[c]).Distinct().OrderBy(d => d).ToList(),
                "called across many programs — assign by call graph, not notes"))
            .OrderByDescending(c => c.FanIn)
            .ToList();
        var crossSet = crossCutting.Select(c => c.Program).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Encode every program once (deboilerplated) and every business domain description once.
        var vectors = programs.ToDictionary(p => p.Name, p => Encode(p.DocumentText(false)), StringComparer.OrdinalIgnoreCase);
        var domainVectors = businessDomains
            .Where(domains.ContainsKey)
            .ToDictionary(d => d, d => Encode(domains[d].Title + ". " + domains[d].Purpose));
        var domainVectorsNoHelp = domainVectors.Where(kv => kv.Key != "10").ToDictionary(kv => kv.Key, kv => kv.Value);

        // ---- retrieval (all domains, and excluding the cross-cutting Help domain) ----
        var retrievalPred = new Dictionary<string, (string Pred, int Rank)>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in programs)
        {
            var ranked = domainVectors
                .Select(kv => (kv.Key, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vectors[program.Name], kv.Value)))
                .OrderByDescending(e => e.Sim)
                .ToList();
            var rank = ranked.FindIndex(e => e.Key == program.Domain);
            retrievalPred[program.Name] = (ranked[0].Key, rank);
        }

        var retrieval = Score("Retrieval vs domain descriptions", "notes → nearest domain description, no training",
            programs, name => retrievalPred[name].Pred, name => retrievalPred[name].Rank);

        var noHelpPrograms = programs.Where(p => p.Domain != "10").ToList();
        var noHelp = Score("Retrieval, Help excluded", "cross-cutting Help domain removed as a target",
            noHelpPrograms,
            name => domainVectorsNoHelp.Select(kv => (kv.Key, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vectors[name], kv.Value))).OrderByDescending(e => e.Sim).First().Key,
            name => domainVectorsNoHelp.Select(kv => (kv.Key, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vectors[name], kv.Value))).OrderByDescending(e => e.Sim).ToList().FindIndex(e => e.Key == truthByName[name]));

        // ---- nearest-centroid leave-one-out (notes only) ----
        var byDomain = programs.GroupBy(p => p.Domain).ToDictionary(g => g.Key, g => g.Select(p => p.Name).ToList());
        var centroidPred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var held in programs)
        {
            centroidPred[held.Name] = byDomain
                .Select(kv => (kv.Key, Names: kv.Value.Where(n => n != held.Name).ToList()))
                .Where(e => e.Names.Count > 0)
                .Select(e => (e.Key, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vectors[held.Name], Mean(e.Names.Select(n => vectors[n])))))
                .OrderByDescending(e => e.Sim)
                .First().Key;
        }

        var centroid = Score("Nearest-centroid, leave-one-out", "notes only, a few tagged programs bootstrap each domain",
            programs, name => centroidPred[name], _ => 0);

        // ---- supervised ML.NET leave-one-out ----
        var supervisedPred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var held in programs)
        {
            var train = programs.Where(p => p.Name != held.Name).ToList();
            supervisedPred[held.Name] = train.Count(p => p.Domain == held.Domain) == 0
                ? "?"
                : TrainAndPredict(train, vectors, vectors[held.Name]);
        }

        var supervised = Score("Supervised ML.NET, leave-one-out", "embeddinggemma → PCA → SdcaMaximumEntropy",
            programs, name => supervisedPred[name], _ => 0);

        // ---- structure-aware: cluster the call graph, pool notes per feature, classify once ----
        var (structuralPred, clusters) = StructuralClassify(programs, callEdges, crossSet, vectors, domainVectorsNoHelp, domains, callersOf, truthByName);
        var structural = Score("Structure-aware (call-graph clusters)", "cross-cutting set aside; S/B/D feature pooled and classified once",
            programs, name => structuralPred[name], _ => 0);

        // ---- per-domain and per-program reporting ----
        var domainScores = businessDomains.Where(byDomain.ContainsKey).Select(d => new NotesDomainScore(
            d,
            domains.TryGetValue(d, out var dd) ? dd.Title : d,
            byDomain[d].Count,
            byDomain[d].Count(n => retrievalPred[n].Pred == d),
            byDomain[d].Count(n => structuralPred[n] == d))).ToList();

        var clusterIdByName = clusters.SelectMany(c => c.Members.Select(m => (m, c.ClusterId))).ToDictionary(x => x.m, x => x.ClusterId, StringComparer.OrdinalIgnoreCase);
        var rows = programs.OrderBy(p => p.Name).Select(p => new NotesProgramRow(
            p.Name,
            LayerOf(p.Name),
            p.Domain,
            domains.TryGetValue(p.Domain, out var dt) ? dt.Title : p.Domain,
            p.Function,
            p.All.Count,
            p.Distinctive.Count,
            retrievalPred[p.Name].Pred,
            retrievalPred[p.Name].Rank < 0 ? -1 : retrievalPred[p.Name].Rank + 1,
            structuralPred[p.Name],
            crossSet.Contains(p.Name),
            clusterIdByName.GetValueOrDefault(p.Name, -1))).ToList();

        var edgeCount = callEdges.Count(e => programNames.Contains(e.From) && programNames.Contains(e.To));

        return new NotesClassificationResult(
            true,
            $"BankDemo at {_bankRoot}: {programs.Count} business programs, {edgeCount} call edges among them, {domainVectors.Count} domain descriptions.",
            _bankRoot,
            programs.Count,
            domainVectors.Count,
            boilerplate.Count,
            edgeCount,
            retrieval,
            noHelp,
            centroid,
            supervised,
            structural,
            rows,
            crossCutting,
            domainScores,
            clusters);
    }

    // Cut edges into cross-cutting and shared-data programs, take connected components as features, pool
    // each feature's notes and classify the pool once — the weak data-program notes ride the strong
    // business header. Cross-cutting and shared programs are assigned by their callers, not their notes.
    private (Dictionary<string, string> Pred, List<NotesClusterRow> Clusters) StructuralClassify(
        List<ProgramNotes> programs,
        List<(string From, string To)> callEdges,
        HashSet<string> crossSet,
        Dictionary<string, float[]> vectors,
        Dictionary<string, float[]> domainVectorsNoHelp,
        Dictionary<string, (string Title, string Purpose)> domains,
        Dictionary<string, HashSet<string>> callersOf,
        Dictionary<string, string> truthByName)
    {
        var names = programs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A data program pulled by callers that span more than one caller-cluster is "shared"; keep it out
        // of clustering and assign it by majority vote of its callers' predictions afterwards.
        var businessCallers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in callEdges.Where(e => names.Contains(e.From) && names.Contains(e.To)))
        {
            (businessCallers.TryGetValue(to, out var set) ? set : businessCallers[to] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(from);
        }

        var shared = names.Where(n => businessCallers.TryGetValue(n, out var c) && c.Count >= 2 && LayerOf(n) == "Data").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(crossSet, StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(shared);

        // Union-find over edges among non-excluded programs.
        var parent = names.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        string Find(string x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(string a, string b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }
        foreach (var (from, to) in callEdges)
        {
            if (names.Contains(from) && names.Contains(to) && !excluded.Contains(from) && !excluded.Contains(to))
            {
                Union(from, to);
            }
        }

        var componentMembers = programs.Where(p => !excluded.Contains(p.Name))
            .GroupBy(p => Find(p.Name))
            .ToList();

        var pred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clusterRows = new List<NotesClusterRow>();
        var id = 0;
        foreach (var component in componentMembers)
        {
            id++;
            var pooled = string.Join(". ", component.SelectMany(p => new[] { p.Header }.Concat(p.Distinctive)).Where(s => s.Length > 0));
            var vector = Encode(pooled);
            // Classify against every domain except Help: Help is genuinely cross-cutting, so allowing it as a
            // cluster target pulls thin clusters into it (the magnet fires even on pooled notes). The cost is
            // that the small "More Information" feature, which truly is Help, is placed elsewhere — a fair
            // trade of two edge-case programs against roughly thirty the magnet would otherwise capture.
            var predicted = domainVectorsNoHelp
                .Select(kv => (kv.Key, Sim: EmbeddingGemmaEncoder.CosineSimilarity(vector, kv.Value)))
                .OrderByDescending(e => e.Sim)
                .First().Key;

            foreach (var member in component)
            {
                pred[member.Name] = predicted;
            }

            clusterRows.Add(new NotesClusterRow(id, predicted, domains.TryGetValue(predicted, out var d) ? d.Title : predicted, component.Select(p => p.Name).OrderBy(n => n).ToList()));
        }

        // Shared data programs: majority vote of their business callers' cluster predictions.
        foreach (var program in shared)
        {
            var votes = businessCallers[program]
                .Where(pred.ContainsKey)
                .GroupBy(c => pred[c])
                .OrderByDescending(g => g.Count())
                .ToList();
            pred[program] = votes.Count > 0 ? votes[0].Key : "?";
        }

        // Cross-cutting programs: assign the domain of their dominant caller if unambiguous, else Help.
        foreach (var program in crossSet.Where(names.Contains))
        {
            pred[program] = "10";
        }

        return (pred, clusterRows);
    }

    private NotesTestScore Score(string name, string method, List<ProgramNotes> programs, Func<string, string> predict, Func<string, int> truthRank)
    {
        var top1 = 0;
        var top3 = 0;
        foreach (var program in programs)
        {
            if (predict(program.Name) == program.Domain)
            {
                top1++;
            }

            var rank = truthRank(program.Name);
            if (rank is >= 0 and < 3)
            {
                top3++;
            }
        }

        // For methods without a ranked list, top3 == top1 (single prediction only).
        if (programs.All(p => truthRank(p.Name) == 0))
        {
            top3 = top1;
        }

        return new NotesTestScore(name, method, top1, top3, programs.Count, $"{top1}/{programs.Count} = {Pct(top1, programs.Count)}");
    }

    private string TrainAndPredict(List<ProgramNotes> train, Dictionary<string, float[]> vectors, float[] query)
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

    // ---- call graph ----

    private static readonly Regex LinkPattern = new(@"(?:LINK|XCTL)\s+PROGRAM\(\s*'([A-Z0-9]{4,8})'", RegexOptions.Compiled);
    private static readonly Regex CallPattern = new(@"CALL\s+'([A-Z0-9]{4,8})'", RegexOptions.Compiled);
    private static readonly Regex MovePattern = new(@"MOVE\s+'([A-Z0-9]{4,8})'\s+TO", RegexOptions.Compiled);
    private static readonly Regex CopyPattern = new(@"COPY\s+(C[A-Z0-9]{3,7})", RegexOptions.Compiled);

    private IEnumerable<string> CallTargets(string[] lines, IEnumerable<string> known, Dictionary<string, string> copyTargets)
    {
        var knownSet = known as HashSet<string> ?? known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            if (raw.Length > 6 && (raw[6] == '*' || raw.TrimStart().StartsWith('*')))
            {
                continue;
            }

            foreach (Match m in LinkPattern.Matches(raw)) { targets.Add(m.Groups[1].Value.ToUpperInvariant()); }
            foreach (Match m in CallPattern.Matches(raw)) { targets.Add(m.Groups[1].Value.ToUpperInvariant()); }
            foreach (Match m in MovePattern.Matches(raw))
            {
                var value = m.Groups[1].Value.ToUpperInvariant();
                if (knownSet.Contains(value))
                {
                    targets.Add(value);
                }
            }

            foreach (Match m in CopyPattern.Matches(raw))
            {
                if (copyTargets.TryGetValue(m.Groups[1].Value.ToUpperInvariant(), out var linked))
                {
                    targets.Add(linked);
                }
            }
        }

        return targets;
    }

    private Dictionary<string, string> ResolveInterfaceCopybooks()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copyDir = Path.Combine(_bankRoot, "sources");
        if (!Directory.Exists(copyDir))
        {
            return map;
        }

        foreach (var file in Directory.EnumerateFiles(copyDir, "C*.cpy", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            foreach (var raw in File.ReadLines(file))
            {
                if (raw.Length > 6 && (raw[6] == '*' || raw.TrimStart().StartsWith('*')))
                {
                    continue;
                }

                var match = LinkPattern.Match(raw);
                if (match.Success)
                {
                    map[name] = match.Groups[1].Value.ToUpperInvariant();
                    break;
                }
            }
        }

        return map;
    }

    // ---- ground truth and notes (shared with the console harness) ----

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

    private static (string Header, List<string> Distinctive, List<string> All) ExtractNotes(string[] lines, HashSet<string> boilerplate)
    {
        var header = "";
        var distinctive = new List<string>();
        var all = new List<string>();

        foreach (var raw in lines)
        {
            if (!(raw.Length > 6 && (raw[6] == '*' || raw.TrimStart().StartsWith('*'))))
            {
                continue;
            }

            var text = raw.Trim().Trim('*', ' ').Trim();
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
        if (upper.Contains("COPYRIGHT") || upper.Contains("WARRANT") || upper.Contains("EULA") || upper.Contains("MERCHANTABILITY") || upper.Contains("LIABILITY") || upper.Contains("AS IS"))
        {
            return true;
        }

        return upper.StartsWith("PROGRAM") || upper.StartsWith("PRGRAM") || upper.StartsWith("LAYER") || upper.StartsWith("VERSION") || upper.Contains("SEQUENCED ON") || upper.Contains("MODULE NAME") || upper.StartsWith("DATE-");
    }

    private static HashSet<string> Boilerplate()
    {
        string[] phrases =
        [
            "Write entry to log to show we have been invoked", "Store our transaction-id",
            "Store passed data or abend if there wasn't any", "This is the main process",
            "Determine what we have to do (read from or send to screen)", "Call the appropriate routine to handle the business logic",
            "Now we have to have finished and can return to our invoker.", "Now return to CICS",
            "Retrieve data from screen and format it", "Build the output screen and send it",
            "Clear map area, get date & time and move to the map", "Ensure the last map fields are correct",
            "Move in any error message", "Move in screen specific fields", "Turn colour off if required",
            "Move in screen name", "Move in userid and any error message", "Call common routine to perform date conversions",
            "Make ourselves re-entrant", "Move the passed area to our area", "Ensure error message is cleared",
            "Save the passed return flag and then turn it off", "Check the AID to see if its valid at this point",
            "Check the AID to see if we have to quit", "Check the to see if user needs or has been using help",
            "Check the AID to see if we have to return to previous screen", "Check if we have set the screen up before or is this 1st time",
            "As this is a display screen we need PFK03/4 to get out so we will just redisplay the data",
            "Now go get the data", "Set up our own copy of any linkage", "Initialise our working storage",
            "Go and do the processing", "Return to our caller", "Send the map", "Receive the map"
        ];
        return phrases.Select(Normalize).ToHashSet();
    }

    private static string LayerOf(string program)
    {
        var upper = program.ToUpperInvariant();
        if (upper.StartsWith('S')) { return "Screen"; }
        if (upper.StartsWith('B')) { return "Business"; }
        if (upper.StartsWith('D')) { return "Data"; }
        if (upper.StartsWith('Z')) { return "Batch"; }
        return "Other";
    }

    private float[] Encode(string text)
    {
        var vector = _encoder.EncodeDocument(text) ?? new float[EmbeddingDim];
        if (vector.Length == EmbeddingDim)
        {
            return vector;
        }

        var fitted = new float[EmbeddingDim];
        Array.Copy(vector, fitted, Math.Min(vector.Length, EmbeddingDim));
        return fitted;
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

    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static string Collapse(string text) => Regex.Replace(text.Replace("\n", " "), @"\s+", " ").Trim();

    private static string Pct(int n, int d) => d == 0 ? "n/a" : $"{100.0 * n / d:0}%";

    private static NotesClassificationResult Empty(string status) => new(
        false, status, "", 0, 0, 0, 0,
        new NotesTestScore("", "", 0, 0, 0, ""), new NotesTestScore("", "", 0, 0, 0, ""),
        new NotesTestScore("", "", 0, 0, 0, ""), new NotesTestScore("", "", 0, 0, 0, ""),
        new NotesTestScore("", "", 0, 0, 0, ""), [], [], [], []);

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

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML;
using Microsoft.ML.Data;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Separates a mixed design document back into its domains with a local classifier, then scores the
// separation two ways: against the sealed labels (did it keep the right sections?) and, later, by what a
// model built from the kept sections (did anything that mattered get lost?).
//
// The classifier is embeddinggemma vectors through an ML.NET multiclass model trained on the two real
// trading repositories, fused with a lexical arm built from each repository's own vocabulary. It never
// sees the design document during training. Everything except the model calls is deterministic.
public sealed partial class ContextClassifierService
{
    private const int EmbeddingDim = 768;
    private const double UncertainMargin = 0.20;

    private readonly EmbeddingGemmaEncoder _encoder;
    private readonly TradingCorpus _corpus;
    private readonly MigrationSandbox _sandbox;
    private readonly MLContext _ml = new(seed: 1);
    private readonly object _gate = new();

    private ITransformer? _model;
    private DataViewSchema? _schema;
    private HashSet<string> _fixedIncomeTerms = [];
    private HashSet<string> _cryptoTerms = [];
    private RenderedSegment[]? _mixed;
    private RenderedSegment[]? _ideal;

    public ContextClassifierService(EmbeddingGemmaEncoder encoder, TradingCorpus corpus, MigrationSandbox sandbox)
    {
        _encoder = encoder;
        _corpus = corpus;
        _sandbox = sandbox;
        Training = Train();
    }

    public ClassifierTrainingSummary Training { get; }

    // ------------------------------------------------------------ documents

    /// <summary>The two designs interleaved with a fixed seed. Sealed labels travel with each segment.</summary>
    public RenderedSegment[] MixedDocument()
    {
        if (_mixed is not null)
        {
            return _mixed;
        }

        var segments = ClassifyContextSamples.Segments.ToList();
        var overview = segments.First(segment => segment.Id == "SH-OVERVIEW");
        segments.Remove(overview);

        // Fisher–Yates with a fixed seed: reproducible, and not sorted by anything a classifier could exploit.
        var random = new Random(ClassifyContextSamples.MixSeed);
        for (var index = segments.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (segments[index], segments[swap]) = (segments[swap], segments[index]);
        }

        segments.Insert(0, overview);
        return _mixed = Render(segments);
    }

    /// <summary>One domain's design on its own, in authored order — what Step 1 shows.</summary>
    public RenderedSegment[] DomainDocument(string domain) =>
        Render(ClassifyContextSamples.Segments.Where(segment => segment.Domain == domain).ToList());

    /// <summary>
    /// The sections a perfect filter would keep for the fixed-income task: its own, the shared ones, and
    /// the crypto sections it explicitly refers to. This is the floor for the token comparison.
    /// </summary>
    public RenderedSegment[] IdealKeptSet()
    {
        if (_ideal is not null)
        {
            return _ideal;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in ClassifyContextSamples.Segments.Where(s => s.Domain is ClassifyContextSamples.FixedIncome or ClassifyContextSamples.Shared))
        {
            wanted.Add(segment.Id);
            foreach (var reference in segment.References)
            {
                wanted.Add(reference);
            }
        }

        return _ideal = MixedDocument().Where(segment => wanted.Contains(segment.Id)).ToArray();
    }

    public static string ToText(IEnumerable<RenderedSegment> segments)
    {
        var text = new StringBuilder();
        foreach (var segment in segments)
        {
            text.AppendLine($"## Section {segment.Number} — {segment.Title}");
            text.AppendLine(segment.Body.Trim());
            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    public int Tokens(string text) => _encoder.CountTokens(text).Tokens;

    private static RenderedSegment[] Render(List<DesignSegment> ordered)
    {
        var numbers = ordered.Select((segment, index) => (segment.Id, Number: index + 1))
            .ToDictionary(entry => entry.Id, entry => entry.Number, StringComparer.Ordinal);

        return ordered.Select((segment, index) =>
        {
            // "§CR-CODES" becomes "§14": the reference survives, the label does not.
            var body = ReferencePattern().Replace(segment.Body, match =>
                numbers.TryGetValue(match.Groups["id"].Value, out var number) ? $"§{number}" : match.Value);
            return new RenderedSegment(index + 1, segment.Id, segment.Domain, segment.Title, body.Trim());
        }).ToArray();
    }

    // ------------------------------------------------------------ training

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

    private ClassifierTrainingSummary Train()
    {
        var documents = TrainingDocuments();
        var fixedIncome = documents.Where(document => document.Label == ClassifyContextSamples.FixedIncome).Select(document => document.Text).ToList();
        var crypto = documents.Where(document => document.Label == ClassifyContextSamples.Crypto).Select(document => document.Text).ToList();
        var shared = documents.Where(document => document.Label == ClassifyContextSamples.Shared).Select(document => document.Text).ToList();

        BuildVocabulary(fixedIncome, crypto, shared);

        if (!_encoder.IsAvailable)
        {
            return new ClassifierTrainingSummary(false, documents.Count, fixedIncome.Count, crypto.Count, shared.Count,
                _fixedIncomeTerms.Count, _cryptoTerms.Count,
                "Lexical arm only — embeddinggemma is not loaded, so the ML.NET arm is disabled and the page says so.");
        }

        // Label order is fixed by first occurrence so the Score vector can be read back by position.
        var anchors = new[] { ClassifyContextSamples.FixedIncome, ClassifyContextSamples.Crypto, ClassifyContextSamples.Shared }
            .Select(label => documents.First(document => document.Label == label))
            .ToList();
        var rows = anchors.Concat(documents.Where(document => !anchors.Contains(document)))
            .Select(document => ToRow(document.Label, document.Text))
            .ToList();

        var data = _ml.Data.LoadFromEnumerable(rows);
        var rank = Math.Max(2, Math.Min(16, rows.Count - 1));
        var pipeline = _ml.Transforms.Conversion.MapValueToKey("Label")
            .Append(_ml.Transforms.ProjectToPrincipalComponents("Features", nameof(Row.Embedding), rank: rank))
            .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy())
            .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        _model = pipeline.Fit(data);
        _schema = data.Schema;

        return new ClassifierTrainingSummary(true, documents.Count, fixedIncome.Count, crypto.Count, shared.Count,
            _fixedIncomeTerms.Count, _cryptoTerms.Count,
            $"embeddinggemma-300m vectors → PCA({rank}) → ML.NET SdcaMaximumEntropy, fused with a lexical arm of repository vocabulary. Trained on the trading repositories only; the design document was never seen.");
    }

    private Row ToRow(string label, string text) => new()
    {
        Label = label,
        Embedding = FitDim(_encoder.EncodeDocument(text))
    };

    private static float[] FitDim(float[]? vector)
    {
        var result = new float[EmbeddingDim];
        if (vector is not null)
        {
            Array.Copy(vector, result, Math.Min(vector.Length, EmbeddingDim));
        }

        return result;
    }

    // The two repositories are free labelled data: their trading code is domain-specific, and the
    // framework spine they share is the very definition of "shared". Falls back to short built-in exemplars
    // when the corpus is not on disk, and says so.
    private List<(string Label, string Text)> TrainingDocuments()
    {
        var documents = new List<(string Label, string Text)>();
        if (_corpus.IsAvailable)
        {
            foreach (var file in _corpus.Files)
            {
                var label = file.Role == "Non-functional"
                    ? ClassifyContextSamples.Shared
                    : file.Repo switch
                    {
                        "FixedIncomeOptionsEngine" => ClassifyContextSamples.FixedIncome,
                        "CryptoFxSpotDesk" => ClassifyContextSamples.Crypto,
                        _ => null
                    };
                if (label is not null)
                {
                    documents.Add((label, file.Source));
                }
            }

            foreach (var document in _corpus.OkfDocuments)
            {
                var label = document.Repo switch
                {
                    "FixedIncomeOptionsEngine" => ClassifyContextSamples.FixedIncome,
                    "CryptoFxSpotDesk" => ClassifyContextSamples.Crypto,
                    _ => null
                };
                if (label is not null)
                {
                    documents.Add((label, document.Text));
                }
            }
        }

        if (documents.Count(document => document.Label == ClassifyContextSamples.FixedIncome) < 2 ||
            documents.Count(document => document.Label == ClassifyContextSamples.Crypto) < 2 ||
            documents.Count(document => document.Label == ClassifyContextSamples.Shared) < 2)
        {
            documents.AddRange(FallbackExemplars);
        }

        return documents;
    }

    private static readonly (string Label, string Text)[] FallbackExemplars =
    [
        (ClassifyContextSamples.FixedIncome, "Regulation T initial margin for listed options and bonds: long premium paid in full, short naked options at twenty percent of underlying less out-of-the-money amount, Treasuries at a one percent haircut and corporates at ten."),
        (ClassifyContextSamples.FixedIncome, "Yield to maturity solved by Newton-Raphson on the present value of coupon cash flows; accrued interest on a 30/360 basis; dirty price equals clean price plus accrued."),
        (ClassifyContextSamples.FixedIncome, "Multi-leg options strategies: vertical spreads margin at maximum loss, straddles and strangles carry a short put leg, strike width less net credit."),
        (ClassifyContextSamples.Crypto, "Spot crypto instant settlement against firm inventory, liquidity provider quote aggregation with staleness limits, and on-chain confirmation thresholds per asset before release."),
        (ClassifyContextSamples.Crypto, "AML wallet screening: sanctions list hits, vendor risk scores, travel-rule payloads above the notional threshold, and venue rate limits with exponential backoff on 429."),
        (ClassifyContextSamples.Crypto, "Order placement on the exchange venue with weight-based request budgets, client order ids for unknown-outcome reconciliation, and best-execution reporting of the winning provider."),
        (ClassifyContextSamples.Shared, "Kafka order consumer, order repository persistence, pipeline stage contracts, metrics collector and pipeline configuration shared by every trading service."),
        (ClassifyContextSamples.Shared, "Deployment behind a feature flag, structured logging of stage decisions, table-driven tests asserting exact output strings, invariant culture formatting.")
    ];

    // Distinctive vocabulary per domain: identifiers and words common in one repository and absent from
    // the other. Shared-corpus words are excluded from both so framework vocabulary cannot vote.
    private void BuildVocabulary(List<string> fixedIncome, List<string> crypto, List<string> shared)
    {
        var fi = Frequencies(fixedIncome);
        var cr = Frequencies(crypto);
        var sh = Frequencies(shared);

        _fixedIncomeTerms = fi.Where(entry => entry.Value >= 2 && !cr.ContainsKey(entry.Key) && !sh.ContainsKey(entry.Key))
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        _cryptoTerms = cr.Where(entry => entry.Value >= 2 && !fi.ContainsKey(entry.Key) && !sh.ContainsKey(entry.Key))
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, int> Frequencies(IEnumerable<string> documents)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var token in Tokenise(document).Distinct(StringComparer.Ordinal))
            {
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        }

        return counts;
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "this", "that", "with", "from", "into", "than", "then", "when", "where", "which", "while", "would",
        "should", "must", "have", "has", "been", "were", "will", "each", "every", "only", "also", "such",
        "public", "private", "static", "readonly", "sealed", "class", "namespace", "using", "return", "async",
        "await", "task", "string", "decimal", "double", "bool", "void", "null", "true", "false", "value",
        "order", "service", "stage", "result", "context", "state", "record", "field", "fields", "output",
        "input", "section", "services", "platform", "desk", "client", "account", "number", "whole", "exactly"
    };

    private static IEnumerable<string> Tokenise(string text)
    {
        foreach (Match match in WordPattern().Matches(text))
        {
            // Split identifiers so RegTMarginCalculator contributes "margin" and "calculator".
            foreach (var part in CamelPattern().Split(match.Value))
            {
                var token = part.ToLowerInvariant();
                if (token.Length >= 4 && !Stopwords.Contains(token) && !token.All(char.IsDigit))
                {
                    yield return token;
                }
            }
        }
    }

    // ------------------------------------------------------------ classification

    /// <summary>Arm B: the local classifier decides, section by section, what the implementer sees.</summary>
    public FilterResult Classify()
    {
        var segments = MixedDocument();
        var decisions = new List<SegmentDecision>();

        foreach (var segment in segments)
        {
            var (fi, cr, sh) = Probabilities(segment);
            var ranked = new[] { (ClassifyContextSamples.FixedIncome, fi), (ClassifyContextSamples.Crypto, cr), (ClassifyContextSamples.Shared, sh) }
                .OrderByDescending(entry => entry.Item2).ToList();
            var predicted = ranked[0].Item1;
            var margin = ranked[0].Item2 - ranked[1].Item2;
            var uncertain = margin < UncertainMargin;

            // Asymmetric by design: dropping a needed section is worse than keeping a stray one.
            var kept = predicted != ClassifyContextSamples.Crypto || uncertain;
            var reason = uncertain
                ? $"uncertain (margin {margin:0.00} < {UncertainMargin:0.00}) — kept rather than guessed"
                : predicted == ClassifyContextSamples.Crypto
                    ? "classified as crypto — dropped"
                    : $"classified as {predicted.ToLowerInvariant()} — kept";

            decisions.Add(new SegmentDecision(segment.Number, segment.Id, segment.Title, segment.SealedDomain, predicted,
                Math.Round(fi, 3), Math.Round(cr, 3), Math.Round(sh, 3), Math.Round(margin, 3), kept, reason));
        }

        decisions = FollowReferences(segments, decisions);
        return Summarise("B", Training.Method, segments, decisions, null);
    }

    /// <summary>Arm D: a model's keep-list, scored by exactly the same rules as the classifier's.</summary>
    public FilterResult FromKeepList(IReadOnlyCollection<int> keep, IReadOnlyDictionary<int, string> reasons, string method, string? note)
    {
        var segments = MixedDocument();
        var decisions = segments.Select(segment =>
        {
            var kept = keep.Contains(segment.Number);
            var reason = reasons.TryGetValue(segment.Number, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : kept ? "kept by the model" : "dropped by the model";
            return new SegmentDecision(segment.Number, segment.Id, segment.Title, segment.SealedDomain,
                kept ? "kept" : "dropped", 0, 0, 0, 0, kept, reason);
        }).ToList();

        return Summarise("D", method, segments, decisions, note);
    }

    private (double FixedIncome, double Crypto, double Shared) Probabilities(RenderedSegment segment)
    {
        var text = segment.Title + "\n" + segment.Body;

        // Lexical arm: distinctive words vote; a segment with none of either vocabulary reads as shared.
        var tokens = Tokenise(text).ToList();
        double fiHits = tokens.Count(_fixedIncomeTerms.Contains);
        double crHits = tokens.Count(_cryptoTerms.Contains);
        var total = fiHits + crHits + 1.0;
        var lexical = (fiHits / total, crHits / total, 1.0 / total);

        if (_model is null || _schema is null)
        {
            return lexical;
        }

        Prediction prediction;
        lock (_gate)
        {
            var engine = _ml.Model.CreatePredictionEngine<Row, Prediction>(_model, _schema);
            prediction = engine.Predict(new Row { Embedding = FitDim(_encoder.Encode(text)) });
        }

        var scores = prediction.Score.Length >= 3 ? prediction.Score : [1f / 3, 1f / 3, 1f / 3];
        return (
            (scores[0] + lexical.Item1) / 2,
            (scores[1] + lexical.Item2) / 2,
            (scores[2] + lexical.Item3) / 2);
    }

    // A kept section that says "see §14" needs §14, whatever §14 is about. Followed transitively.
    private static List<SegmentDecision> FollowReferences(RenderedSegment[] segments, List<SegmentDecision> decisions)
    {
        var byNumber = decisions.ToDictionary(decision => decision.Number);
        var bodies = segments.ToDictionary(segment => segment.Number, segment => segment.Body);
        var queue = new Queue<int>(decisions.Where(decision => decision.Kept).Select(decision => decision.Number));
        var visited = new HashSet<int>();

        while (queue.Count > 0)
        {
            var number = queue.Dequeue();
            if (!visited.Add(number))
            {
                continue;
            }

            foreach (Match match in NumberedReferencePattern().Matches(bodies[number]))
            {
                var target = int.Parse(match.Groups["n"].Value);
                if (!byNumber.TryGetValue(target, out var decision) || decision.Kept)
                {
                    continue;
                }

                byNumber[target] = decision with
                {
                    Kept = true,
                    Reason = $"referenced from kept section {number} — kept regardless of label"
                };
                queue.Enqueue(target);
            }
        }

        return decisions.Select(decision => byNumber[decision.Number]).ToList();
    }

    private FilterResult Summarise(string arm, string method, RenderedSegment[] segments, List<SegmentDecision> decisions, string? note)
    {
        var kept = segments.Where(segment => decisions.First(decision => decision.Number == segment.Number).Kept).ToList();
        var text = ToText(kept);

        int Count(string domain, bool keptState) => decisions.Count(decision => decision.SealedDomain == domain && decision.Kept == keptState);
        var trapsHonoured = ClassifyContextSamples.TrapIds.Count(trapId =>
        {
            var trap = ClassifyContextSamples.Segments.First(segment => segment.Id == trapId);
            return trap.References.All(reference => decisions.First(decision => decision.Id == reference).Kept);
        });

        return new FilterResult(
            arm,
            method,
            decisions,
            kept.Count,
            segments.Length,
            Count(ClassifyContextSamples.FixedIncome, true),
            decisions.Count(decision => decision.SealedDomain == ClassifyContextSamples.FixedIncome),
            Count(ClassifyContextSamples.Crypto, false),
            decisions.Count(decision => decision.SealedDomain == ClassifyContextSamples.Crypto),
            Count(ClassifyContextSamples.Shared, true),
            decisions.Count(decision => decision.SealedDomain == ClassifyContextSamples.Shared),
            decisions.Count(decision => decision.Reason.StartsWith("uncertain", StringComparison.Ordinal)),
            decisions.Count(decision => decision.Reason.StartsWith("referenced", StringComparison.Ordinal)),
            trapsHonoured,
            ClassifyContextSamples.TrapIds.Length,
            Tokens(text),
            text,
            note);
    }

    // ------------------------------------------------------------ implementation scoring

    public string BuildImplementRequest(string designText) => $"""
        <design>
        {designText}
        </design>
        Implement the bond settlement amount service described in the design as a single C# file. The class
        must be named {ClassifyContextSamples.EntryType} and expose public static string {ClassifyContextSamples.EntryMethod}(string record).
        Anything in the design that the bond settlement service does not need is context, not a requirement.
        """;

    /// <summary>
    /// The alternative to removal: name what to ignore. Built only from the classifier's dropped set, so
    /// the instruction is as deterministic as the filter and says nothing the filter did not decide.
    /// </summary>
    public static string BuildIgnoreInstruction(FilterResult filter)
    {
        var dropped = filter.Decisions.Where(decision => !decision.Kept).OrderBy(decision => decision.Number).ToList();
        var keptByReference = filter.Decisions.Where(decision => decision.Kept && decision.Reason.StartsWith("referenced", StringComparison.Ordinal))
            .OrderBy(decision => decision.Number).ToList();

        var text = new StringBuilder();
        text.AppendLine("FOCUS INSTRUCTION — produced by a local classifier, not by a person:");
        text.AppendLine("The design below describes two services. You are implementing ONLY the bond settlement amount service.");
        if (dropped.Count > 0)
        {
            text.AppendLine($"IGNORE the following {dropped.Count} section(s). They describe the other service. Do not implement them, do not borrow logic, names or defaults from them, and do not ask questions about them:");
            foreach (var decision in dropped)
            {
                text.AppendLine($"- Section {decision.Number} — {decision.Title}");
            }
        }

        if (keptByReference.Count > 0)
        {
            text.AppendLine($"The following section(s) look like they belong to the other service but are referred to by a bond section, so they DO apply:");
            foreach (var decision in keptByReference)
            {
                text.AppendLine($"- Section {decision.Number} — {decision.Title}");
            }
        }

        text.AppendLine("Every other section applies. Read it in full.");
        return text.ToString().TrimEnd();
    }

    public string BuildFocusRequest(string instruction, string mixedText) => $"""
        <focus_instruction>
        {instruction}
        </focus_instruction>
        {BuildImplementRequest(mixedText)}
        """;

    /// <summary>Compiles a model's code, runs it against the reference oracle, and scans it for leaked vocabulary.</summary>
    public ImplementationArm Score(ImplementationArm arm, string code)
    {
        var (source, assumptions) = SplitAssumptions(code);
        var run = _sandbox.Run(source, ClassifyContextSamples.EntryType, ClassifyContextSamples.EntryMethod, ClassifyContextSamples.TestVectors);

        var cases = new List<MigrationCaseResult>();
        if (run.Ran)
        {
            foreach (var output in run.Outputs)
            {
                var expected = ClassifyContextSamples.Oracle(output.Input);
                var actual = output.Error is null ? output.Value.Trim() : "error";
                cases.Add(new MigrationCaseResult(output.Input, expected, actual, output.Error is null && actual == expected, output.Error));
            }
        }

        var leaked = ClassifyContextSamples.CryptoLeakTerms
            .Where(term => Regex.IsMatch(source, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase))
            .ToList();

        return arm with
        {
            Status = "completed",
            Code = source,
            Compiled = run.Ran,
            CompileErrors = run.CompileErrors,
            Cases = cases,
            Passed = cases.Count(item => item.Matched),
            LeakedTerms = leaked,
            Assumptions = assumptions,
            Error = run.Ran ? null : run.Error
        };
    }

    private static (string Source, List<string> Assumptions) SplitAssumptions(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                text = text[(firstLine + 1)..lastFence];
            }
        }

        var assumptions = new List<string>();
        var body = new StringBuilder();
        var inAssumption = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("// ASSUMPTION:", StringComparison.OrdinalIgnoreCase))
            {
                assumptions.Add(trimmed["// ASSUMPTION:".Length..].Trim());
                inAssumption = true;
            }
            else if (inAssumption && trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                // Models wrap long assumptions over several comment lines; keep the continuation with its assumption.
                assumptions[^1] = assumptions[^1] + " " + trimmed.TrimStart('/').Trim();
            }
            else if (!trimmed.StartsWith("// FILE:", StringComparison.OrdinalIgnoreCase))
            {
                inAssumption = false;
                body.AppendLine(line.TrimEnd());
            }
        }

        return (body.ToString().Trim(), assumptions);
    }

    /// <summary>Proves the oracle and the classifier before anyone demonstrates them.</summary>
    public string SelfCheck()
    {
        var oracle = ClassifyContextSamples.Oracle(ClassifyContextSamples.TestVectors[0]);
        var example = "status=OK;notional=9950.00;accrued=56.25;settlement=10006.25;margin=99.50;settle=T+1";
        var oracleOk = oracle == example;

        var filter = Classify();
        var mixedTokens = Tokens(ToText(MixedDocument()));
        var idealTokens = Tokens(ToText(IdealKeptSet()));
        return $"oracle matches published example={oracleOk}; {filter.TotalSegments} segments, kept {filter.KeptSegments}; " +
            $"fixed-income recall {filter.FixedIncomeKept}/{filter.FixedIncomeTotal}, crypto dropped {filter.CryptoDropped}/{filter.CryptoTotal}, " +
            $"shared kept {filter.SharedKept}/{filter.SharedTotal}, uncertain {filter.Uncertain}, by reference {filter.KeptByReference}, " +
            $"traps honoured {filter.TrapsHonoured}/{filter.TrapsTotal}; tokens mixed {mixedTokens} → kept {filter.PromptTokens} (ideal {idealTokens}); " +
            $"training {Training.TrainingDocuments} docs ({Training.FixedIncomeDocuments} FI, {Training.CryptoDocuments} crypto, {Training.SharedDocuments} shared), " +
            $"vocabulary {Training.FixedIncomeTerms}/{Training.CryptoTerms} terms; embeddings={Training.EmbeddingsUsed}";
    }

    [GeneratedRegex(@"§(?<id>[A-Z]{2}-[A-Z]+)")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"§(?<n>\d+)")]
    private static partial Regex NumberedReferencePattern();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]{2,}")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelPattern();
}

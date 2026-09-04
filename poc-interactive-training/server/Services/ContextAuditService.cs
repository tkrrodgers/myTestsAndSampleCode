using System.Text.RegularExpressions;
using Microsoft.ML;
using Microsoft.ML.Data;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Tier 1 + 2 of the context audit. A deterministic structural scan is combined with the full
// embeddinggemma-300m embedding (real int8 ONNX inference), PCA-reduced, and trained through a real
// ML.NET multiclass classifier on a labeled corpus. Fails soft to structural-only when the model is
// absent. The labeled corpus here is illustrative and is replaced with SME-labeled CES repos before
// production gating.
public sealed class ContextAuditService
{
    private const int EmbeddingDim = 768;
    private const int PcaRank = 24;

    private readonly MLContext _ml = new(seed: 1);
    private readonly ITransformer _model;
    private readonly DataViewSchema _schema;
    private readonly object _predictGate = new();
    private readonly EmbeddingGemmaEncoder _encoder;
    private readonly bool _embeddingsUsed;
    private readonly float[]? _richCentroid;
    private readonly float[]? _poorCentroid;

    public ContextAuditService(EmbeddingGemmaEncoder encoder)
    {
        _encoder = encoder;
        _embeddingsUsed = encoder.IsAvailable;

        var rows = BuildTrainingRows();
        var training = _ml.Data.LoadFromEnumerable(rows);

        var structural = _ml.Transforms.Concatenate("Struct",
                nameof(ContextFeatures.HasOkf), nameof(ContextFeatures.HasMemoryBank), nameof(ContextFeatures.HasWiki),
                nameof(ContextFeatures.HasReadme), nameof(ContextFeatures.HasAdr), nameof(ContextFeatures.HasCodeMap),
                nameof(ContextFeatures.DocCommentDensity), nameof(ContextFeatures.ContextRatio), nameof(ContextFeatures.LinkCount))
            .Append(_ml.Transforms.NormalizeMinMax("Struct"));

        IEstimator<ITransformer> pipeline;
        if (_embeddingsUsed)
        {
            // Structural features + PCA-reduced embeddinggemma embedding, trained together.
            pipeline = _ml.Transforms.Conversion.MapValueToKey("Label")
                .Append(structural)
                .Append(_ml.Transforms.ProjectToPrincipalComponents("EmbeddingPca", nameof(ContextFeatures.Embedding), rank: PcaRank))
                .Append(_ml.Transforms.Concatenate("Features", "Struct", "EmbeddingPca"))
                .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy())
                .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
            _richCentroid = Centroid(RichExemplars);
            _poorCentroid = Centroid(PoorExemplars);
        }
        else
        {
            pipeline = _ml.Transforms.Conversion.MapValueToKey("Label")
                .Append(structural)
                .Append(_ml.Transforms.Concatenate("Features", "Struct"))
                .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy())
                .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
        }

        _model = pipeline.Fit(training);
        _schema = training.Schema;
    }

    public ContextAuditInitial Analyze(string codeWithContext)
    {
        var (features, signals) = Scan(codeWithContext);
        features.Embedding = _embeddingsUsed ? FitDim(_encoder.Encode(codeWithContext)) : new float[EmbeddingDim];

        ContextPrediction prediction;
        lock (_predictGate)
        {
            var engine = _ml.Model.CreatePredictionEngine<ContextFeatures, ContextPrediction>(_model, _schema);
            prediction = engine.Predict(features);
        }

        var labels = GetLabelOrder();
        var score = 0.0;
        for (var index = 0; index < labels.Count && index < prediction.Score.Length; index++)
        {
            score += prediction.Score[index] * labels[index] switch { "Rich" => 100.0, "Adequate" => 60.0, _ => 15.0 };
        }

        var confidence = prediction.Score.Length > 0 ? (int)Math.Round(prediction.Score.Max() * 100) : 0;

        var method = "Structural features classified by an ML.NET SdcaMaximumEntropy model (embeddinggemma unavailable — structural only).";
        if (_embeddingsUsed && _richCentroid is not null && _poorCentroid is not null)
        {
            var cosRich = EmbeddingGemmaEncoder.CosineSimilarity(features.Embedding, _richCentroid);
            var cosPoor = EmbeddingGemmaEncoder.CosineSimilarity(features.Embedding, _poorCentroid);
            signals.Add(new ContextStructureSignal(
                "embeddinggemma semantic",
                cosRich >= cosPoor,
                $"cos(rich)={cosRich:0.00}, cos(poor)={cosPoor:0.00} → {(cosRich >= cosPoor ? "context-rich" : "context-poor")}"));
            method = $"ML.NET multiclass over structural features + PCA({PcaRank})-reduced embeddinggemma-300m embeddings (real int8 ONNX). {_encoder.StatusMessage}";
        }

        var positive = prediction.PredictedLabel is "Rich" or "Adequate";

        return new ContextAuditInitial(
            prediction.PredictedLabel,
            (int)Math.Round(score),
            confidence,
            positive,
            signals,
            method);
    }

    private List<ContextFeatures> BuildTrainingRows()
    {
        var rows = new List<ContextFeatures>();
        foreach (var (text, label) in LabeledCorpus())
        {
            var (features, _) = Scan(text);
            features.Label = label;
            features.Embedding = _embeddingsUsed ? FitDim(_encoder.Encode(text)) : new float[EmbeddingDim];
            rows.Add(features);
        }

        return rows;
    }

    private float[]? Centroid(IReadOnlyList<string> exemplars)
    {
        float[]? sum = null;
        var count = 0;
        foreach (var exemplar in exemplars)
        {
            var vector = _encoder.Encode(exemplar);
            if (vector is null)
            {
                continue;
            }

            sum ??= new float[vector.Length];
            if (sum.Length != vector.Length)
            {
                continue;
            }

            for (var index = 0; index < vector.Length; index++)
            {
                sum[index] += vector[index];
            }

            count++;
        }

        if (sum is null || count == 0)
        {
            return null;
        }

        var norm = 0.0;
        for (var index = 0; index < sum.Length; index++)
        {
            sum[index] /= count;
            norm += sum[index] * sum[index];
        }

        var magnitude = (float)Math.Sqrt(norm);
        if (magnitude > 0)
        {
            for (var index = 0; index < sum.Length; index++)
            {
                sum[index] /= magnitude;
            }
        }

        return sum;
    }

    private static float[] FitDim(float[]? vector)
    {
        var result = new float[EmbeddingDim];
        if (vector is null)
        {
            return result;
        }

        Array.Copy(vector, result, Math.Min(vector.Length, EmbeddingDim));
        return result;
    }

    private static (ContextFeatures Features, List<ContextStructureSignal> Signals) Scan(string text)
    {
        var lower = text.ToLowerInvariant();
        bool Has(params string[] markers) => markers.Any(marker => lower.Contains(marker));

        var hasOkf = Has("okf_version", "okf/index", "open knowledge format") || Regex.IsMatch(text, "(?m)^type:\\s");
        var hasMemoryBank = Has("memory-bank", "memory bank", "/memories/", "memorybank");
        var hasWiki = Has("wiki", "confluence", "sharepoint");
        var hasReadme = Has("readme");
        var hasAdr = Has("adr-", "adr ", "architecture decision");
        var hasCodeMap = Has("code-map", "code map");

        var lines = text.Split('\n');
        var commentLines = lines.Count(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal);
        });
        var markdownLines = lines.Count(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("-", StringComparison.Ordinal) ||
                trimmed.StartsWith(">", StringComparison.Ordinal) || trimmed.StartsWith("|", StringComparison.Ordinal) ||
                Regex.IsMatch(trimmed, "^[0-9]+\\.");
        });
        var docDensity = lines.Length == 0 ? 0f : (float)commentLines / lines.Length;
        var contextRatio = lines.Length == 0 ? 0f : (float)markdownLines / lines.Length;
        var linkCount = Regex.Matches(text, "\\]\\(").Count;

        var features = new ContextFeatures
        {
            HasOkf = hasOkf ? 1f : 0f,
            HasMemoryBank = hasMemoryBank ? 1f : 0f,
            HasWiki = hasWiki ? 1f : 0f,
            HasReadme = hasReadme ? 1f : 0f,
            HasAdr = hasAdr ? 1f : 0f,
            HasCodeMap = hasCodeMap ? 1f : 0f,
            DocCommentDensity = docDensity,
            ContextRatio = contextRatio,
            LinkCount = linkCount,
            Embedding = new float[EmbeddingDim]
        };

        var signals = new List<ContextStructureSignal>
        {
            new("OKF bundle", hasOkf, hasOkf ? "OKF markers / frontmatter detected" : "no OKF markers"),
            new("Memory bank", hasMemoryBank, hasMemoryBank ? "memory-bank references detected" : "none"),
            new("Wiki / external docs", hasWiki, hasWiki ? "wiki/Confluence references detected" : "none"),
            new("README", hasReadme, hasReadme ? "README present" : "none"),
            new("ADR", hasAdr, hasAdr ? "architecture decision records referenced" : "none"),
            new("Code map", hasCodeMap, hasCodeMap ? "code-map referenced" : "none"),
            new("Doc-comment density", commentLines > 0, $"{docDensity:P0} of lines are comments"),
            new("Context/prose ratio", markdownLines > 0, $"{contextRatio:P0} of lines are structured prose"),
            new("Cross-links", linkCount > 0, $"{linkCount} markdown link(s)")
        };

        return (features, signals);
    }

    private List<string> GetLabelOrder()
    {
        try
        {
            var scoreColumn = _model.GetOutputSchema(_schema).GetColumnOrNull("Score");
            if (scoreColumn is { } column)
            {
                var slots = default(VBuffer<ReadOnlyMemory<char>>);
                column.GetSlotNames(ref slots);
                var names = slots.DenseValues().Select(value => value.ToString()).Where(name => !string.IsNullOrEmpty(name)).ToList();
                if (names.Count > 0)
                {
                    return names;
                }
            }
        }
        catch
        {
            // Fall through to the default class order below.
        }

        return ["Rich", "Adequate", "Poor"];
    }

    // Labeled corpus (illustrative). Real texts are embedded by embeddinggemma; structural features
    // are scanned from the same text. Replace with SME-labeled CES repos before production gating.
    private static IEnumerable<(string Text, string Label)> LabeledCorpus()
    {
        foreach (var text in RichExemplars)
        {
            yield return (text, "Rich");
        }

        foreach (var text in AdequateExemplars)
        {
            yield return (text, "Adequate");
        }

        foreach (var text in PoorExemplars)
        {
            yield return (text, "Poor");
        }
    }

    private static readonly string[] RichExemplars =
    [
        "type: Business Rule\ntitle: Late fee policy\nstatus: stable\nsources: [ADR-024]\nRules: daily rate 1.5%, fee capped at $250, no fee when balance <= 0, 3-day grace period, round to 2 decimals. See [ADR-024](decisions/adr-024.md), [code map](code-map/billing.md), [index](okf/index.md).",
        "# README\n## Architecture\nService boundaries are documented in okf/architecture. [ADR-012](decisions/adr-012.md) explains the retry policy. The [code map](code-map/index.md) links each concept to its implementation and tests. See the wiki for onboarding.",
        "type: Decision\ntitle: ADR-031 idempotent payments\nstatus: stable\nContext, decision and consequences are documented and linked to [payment concept](concepts/payment.md) and the memory-bank. Sources and verification recorded.",
        "type: Business Concept\ntitle: Delivery estimate\ntags: [fulfillment]\nGoverning links: [service boundaries](architecture/boundaries.md), [ADR-024](decisions/adr-024.md), [code map](code-map/fulfillment.md). status: stable, verified by reviewer.",
        "# Memory bank — progress\nDecisions and architecture are tracked here with links to [ADR-007](decisions/adr-007.md) and [concepts](okf/concepts/index.md). Open questions and sources are recorded per entry.",
        "type: Code Map\ntitle: Billing components\n| Role | Path | Why |\n|--|--|--|\n| Impl | src/Billing/LateFee.cs | applies policy |\n| Tests | src/Billing/LateFeeTests.cs | covers rules |\nLinked to [concept](concepts/late-fee.md) and [ADR-024](decisions/adr-024.md).",
        "type: Architecture Boundary\ntitle: Fulfillment boundaries\nOwnership documented; Shipping ingests events, Fulfillment owns estimates. See [code map](code-map/fulfillment.md) and [ADR-024](decisions/adr-024.md).",
        "# README\nQuickstart, architecture diagram, and an OKF bundle under okf/. ADR records in decisions/. Onboarding in the wiki. [Index](okf/index.md) lists every concept with sources and verification.",
        "type: Business Rule\ntitle: Refund eligibility\nstatus: stable\nsources: [ADR-018]\nRules documented with edge cases and linked to [concept](concepts/refund.md), [code map](code-map/refund.md), and the wiki runbook.",
        "type: Decision\ntitle: ADR-024 confirmed delay source\nOnly a confirmed carrier delay revises the estimate. Consequences and tests documented. Linked to [concept](concepts/delivery-estimate.md) and [code map](code-map/fulfillment.md)."
    ];

    private static readonly string[] AdequateExemplars =
    [
        "# README\n## Setup\nRun dotnet build then dotnet run. The service computes order totals. Configuration is in appsettings.json. No architecture decision records yet.",
        "/// <summary>Calculates the late fee for an overdue invoice.</summary>\n/// <remarks>Daily rate applies after a grace period; the total is capped.</remarks>\npublic decimal CalculateFee(decimal balance, int days) { /* ... */ }",
        "// The retry policy backs off exponentially. See the team wiki for rationale.\n// Rules: max 5 retries, 2s base delay.\npublic async Task Send() { /* ... */ }",
        "# README\n## Changelog\n- 1.2 added tax rules\n- 1.1 discounts\nShort overview of the billing module. Endpoints documented inline.",
        "/// <summary>Applies volume and customer discounts, then tax and shipping.</summary>\n/// Edge cases: zero quantity returns zero; wholesale customers skip shipping.\npublic double Checkout(Order o) { /* ... */ }",
        "# API\nPOST /orders creates an order. GET /orders/{id} returns status. Errors return problem+json. See comments for validation rules.",
        "// Business rule: gold customers get 5% off, wholesale 15%. Tax by state.\n// TODO: document the shipping thresholds.\npublic decimal Total() { /* ... */ }",
        "# Design note\nThe estimator recalculates on confirmed delay only. This paragraph explains the intent; no ADR exists yet.\npublic class Estimator { }",
        "# README\n## Known limitations\n- no grace period handling\n- rounding is naive\nBrief module overview and setup instructions.",
        "// Config documented: DailyRate=0.015, MaxFee=250. These map to the billing policy.\npublic static class Config { }"
    ];

    private static readonly string[] PoorExemplars =
    [
        "public class A { public int f(int x){ return x*2+1; } }",
        "static void Main(){ var t=0; for(int i=0;i<n;i++) t+=a[i]; Console.WriteLine(t); }",
        "class Helper { void Do(){ /* stuff */ x(); y(); z(); } }",
        "public double c(object[] o,int t,bool r){ double s=0; for(int i=0;i<o.Length;i++){ s+=(double)((object[])o[i])[1]; } if(t==2)s*=0.95; return s; }",
        "static int g(int a,int b,int c){ if(a>b){ if(b>c){ return a; } else { return c; } } return b; }",
        "class U { public static string p(string s){ return s.Substring(3).Replace(\",\",\";\"); } }",
        "void h(){ int a=5,b=10,c=15; var r=a*b+c-7; d(r); }",
        "public object proc(int code){ switch(code){ case 7: return 42; case 9: return 99; default: return 0; } }",
        "void run(){ var f=File.ReadAllText(p); var n=int.Parse(f); Console.WriteLine(n*3.14159); }",
        "class P { void m(string x){ var parts=x.Split('|'); foreach(var p in parts){ q(p); } } }"
    ];

    private sealed class ContextFeatures
    {
        public float HasOkf { get; set; }
        public float HasMemoryBank { get; set; }
        public float HasWiki { get; set; }
        public float HasReadme { get; set; }
        public float HasAdr { get; set; }
        public float HasCodeMap { get; set; }
        public float DocCommentDensity { get; set; }
        public float ContextRatio { get; set; }
        public float LinkCount { get; set; }

        [VectorType(EmbeddingDim)]
        public float[] Embedding { get; set; } = new float[EmbeddingDim];

        public string Label { get; set; } = string.Empty;
    }

    private sealed class ContextPrediction
    {
        [ColumnName("PredictedLabel")]
        public string PredictedLabel { get; set; } = string.Empty;

        public float[] Score { get; set; } = [];
    }
}

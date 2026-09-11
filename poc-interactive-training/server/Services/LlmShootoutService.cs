using System.Text.Json;
using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Ground truth first, opinion last. GnuCOBOL compiles and runs the harness; every model answer is scored
// against that output and a sealed SME checklist before any model is asked what it thinks.
public sealed partial class LlmShootoutService(CobolToolchain toolchain, TrainingFixtureProvider fixture)
{
    private readonly Lazy<ShootoutOracle> _oracle = new(() => Build(toolchain, fixture), LazyThreadSafetyMode.ExecutionAndPublication);

    public ShootoutOracle Oracle => _oracle.Value;

    public string HarnessSource => fixture.Read("gam/oracle/GAMORACLE.cbl");

    public static string Brief => LlmShootoutSamples.Brief;

    public static string BuildRequest() => $"""
        <brief>
        {LlmShootoutSamples.Brief}
        </brief>

        <deliverable>
        {LlmShootoutSamples.AnswerContract}
        </deliverable>
        """;

    private static ShootoutOracle Build(CobolToolchain toolchain, TrainingFixtureProvider fixture)
    {
        var offsets = ExpectedOffsets();
        var source = fixture.Read("gam/oracle/GAMORACLE.cbl");
        if (string.IsNullOrWhiteSpace(source))
        {
            return new ShootoutOracle(false, "Harness gam/oracle/GAMORACLE.cbl is missing from the training fixture.", 0, 0, [], new Dictionary<string, string>(), offsets, "");
        }

        var run = toolchain.RunOracle(source, [""]).FirstOrDefault();
        if (run is null || run.Error is not null || string.IsNullOrWhiteSpace(run.Output))
        {
            return new ShootoutOracle(false, run?.Error ?? toolchain.StatusMessage, 0, 0, [], new Dictionary<string, string>(), offsets, run?.Output ?? "");
        }

        var lines = run.Output.Replace("\r", "").Split('\n');
        var recordLength = IntAfter(lines, "LENGTH DCLEASTINVNTRY=");
        var outpusLength = IntAfter(lines, " OUTPUS=");
        var cases = lines.Where(line => line.StartsWith("CASE", StringComparison.Ordinal)).Select(line => line.TrimEnd()).ToList();

        // The hex line follows each CASE line's BUILD-ROW; pair them in order.
        var hexLines = lines.Where(line => line.Contains("PRICE packed bytes=", StringComparison.Ordinal)).ToList();
        var hex = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hexLines.Count >= 5)
        {
            hex["PRICE_123456"] = HexField(hexLines[1], "PRICE packed bytes=");
            hex["PRICE_NEG1500"] = HexField(hexLines[3], "PRICE packed bytes=");
            hex["AUTOYEAR_2024"] = HexField(hexLines[0], "AUTOYEAR bytes=");
            var vin = HexField(hexLines[0], "VIN bytes=");
            hex["VINLEN_4"] = vin.Length >= 4 ? vin[..4] : vin;
        }

        var layoutTotal = LlmShootoutSamples.Layout.Sum(item => item.Length);
        var status = recordLength == layoutTotal
            ? $"GnuCOBOL {ToolchainVersion(toolchain)} compiled and ran the harness; LENGTH OF agrees with the declared layout ({layoutTotal} bytes)."
            : $"GnuCOBOL ran the harness but LENGTH OF ({recordLength}) disagrees with the declared layout ({layoutTotal}); offsets are suspect.";

        return new ShootoutOracle(cases.Count == 5 && hex.Count == 4, status, recordLength, outpusLength, cases, hex, offsets, run.Output);
    }

    private static string ToolchainVersion(CobolToolchain toolchain)
    {
        var match = VersionPattern().Match(toolchain.StatusMessage);
        return match.Success ? match.Value : "";
    }

    public static IReadOnlyDictionary<string, int> ExpectedOffsets()
    {
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = 0;
        foreach (var (name, length) in LlmShootoutSamples.Layout)
        {
            offsets[name] = position;
            position += length;
        }

        return offsets;
    }

    // Deterministic scoring. Anything the compiler printed is compared exactly; SME insights are keyword
    // floors over the design prose. The judge sees these numbers and may not change them.
    public ShootoutEntry Score(ShootoutEntry entry, string content)
    {
        var oracle = Oracle;
        AnswerDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AnswerDto>(ExtractJson(content), JsonOptions);
        }
        catch (JsonException ex)
        {
            return entry with { Status = "failed", Error = $"The answer was not the agreed JSON shape: {ex.Message}" };
        }

        if (dto is null)
        {
            return entry with { Status = "failed", Error = "The answer was empty." };
        }

        var checks = new List<ShootoutCheck>
        {
            Check("Record length (bytes)", oracle.RecordLength.ToString(), dto.RecordLength?.ToString()),
            Check("OUTPUS length (bytes)", oracle.OutpusLength.ToString(), dto.OutpusLength?.ToString())
        };

        foreach (var (name, expected) in oracle.Offsets)
        {
            var given = dto.Offsets is not null && TryGetOffset(dto.Offsets, name, out var value) ? value.ToString() : null;
            checks.Add(Check($"Offset {name}", expected.ToString(), given));
        }

        for (var i = 0; i < oracle.CaseLines.Count; i++)
        {
            var given = dto.Cases is not null && i < dto.Cases.Count ? dto.Cases[i] : null;
            checks.Add(Check($"CASE{i + 1} output row", oracle.CaseLines[i], given, NormalizeCase));
        }

        foreach (var key in LlmShootoutSamples.HexKeys)
        {
            var given = dto.Hex is not null && dto.Hex.TryGetValue(key, out var value) ? value : null;
            checks.Add(Check($"Hex {key}", oracle.Hex.GetValueOrDefault(key, "?"), given, NormalizeHex));
        }

        var prose = string.Join(" ", (dto.Design?.Values ?? Enumerable.Empty<string>()).Concat(dto.Observations ?? []).Concat([dto.NotWritten ?? ""])).ToLowerInvariant();
        var insights = LlmShootoutSamples.Insights
            .Select(insight => new ShootoutInsight(insight.Name, insight.Groups.All(group => group.Any(prose.Contains)), insight.Why))
            .ToList();

        var java = dto.Java?.Trim();
        return entry with
        {
            Status = "completed",
            Checks = checks,
            CheckablePassed = checks.Count(check => check.Passed),
            CheckableTotal = checks.Count,
            Insights = insights,
            InsightsFound = insights.Count(insight => insight.Present),
            JavaConfidence = dto.JavaConfidence?.Trim().ToLowerInvariant(),
            JavaProvided = !string.IsNullOrWhiteSpace(java),
            Observations = dto.Observations ?? [],
            Design = dto.Design ?? new Dictionary<string, string>(),
            Java = string.IsNullOrWhiteSpace(java) ? null : java,
            NotWritten = dto.NotWritten?.Trim(),
            Error = null
        };
    }

    private static bool TryGetOffset(Dictionary<string, JsonElement> offsets, string name, out int value)
    {
        value = 0;
        var element = offsets.FirstOrDefault(pair => pair.Key.Replace('_', '-').Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), out value),
            _ => false
        };
    }

    private static ShootoutCheck Check(string name, string expected, string? given, Func<string, string>? normalize = null)
    {
        if (given is null)
        {
            return new ShootoutCheck(name, expected, null, false);
        }

        normalize ??= value => value.Trim();
        return new ShootoutCheck(name, expected, given, string.Equals(normalize(expected), normalize(given), StringComparison.Ordinal));
    }

    private static string NormalizeCase(string value)
    {
        // Tolerate a missing trailing separator; never tolerate field width changes.
        var trimmed = value.TrimEnd('\r', '\n');
        return trimmed.EndsWith('|') ? trimmed : trimmed + "|";
    }

    private static string NormalizeHex(string value) =>
        HexNoise().Replace(value, "").ToUpperInvariant().Replace("0X", "");

    private static int IntAfter(IEnumerable<string> lines, string marker)
    {
        foreach (var line in lines)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var digits = new string(line[(index + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static string HexField(string line, string marker)
    {
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return "";
        }

        return new string(line[(index + marker.Length)..].TakeWhile(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    private static string ExtractJson(string content)
    {
        var text = content.Trim();
        var fence = FencePattern().Match(text);
        if (fence.Success)
        {
            text = fence.Groups[1].Value.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    public string SelfCheck()
    {
        var oracle = Oracle;
        var layoutTotal = LlmShootoutSamples.Layout.Sum(item => item.Length);
        if (!oracle.Available)
        {
            return $"oracle unavailable: {oracle.Status}";
        }

        // A perfect answer built from the oracle itself must score full marks, or the scorer is wrong.
        var perfect = JsonSerializer.Serialize(new
        {
            recordLength = oracle.RecordLength,
            outpusLength = oracle.OutpusLength,
            offsets = oracle.Offsets,
            cases = oracle.CaseLines,
            hex = oracle.Hex,
            design = new { equivalence = "compile with cobc and diff bytes against a golden oracle" },
            observations = Array.Empty<string>(),
            javaConfidence = "low",
            java = "",
            notWritten = "everything"
        });
        var blank = new ShootoutEntry("T", "self-test", null, "pending", 0, 0, 0, [], 0, 0, [], 0, null, false, [], new Dictionary<string, string>(), null, null, null);
        var scored = Score(blank, "```json\n" + perfect + "\n```");

        return $"oracle ready: record {oracle.RecordLength} bytes (layout {layoutTotal}), OUTPUS {oracle.OutpusLength}, {oracle.CaseLines.Count} cases, {oracle.Hex.Count} hex facts, {oracle.Offsets.Count} offsets, {LlmShootoutSamples.Insights.Length} insight checks; synthetic perfect answer scores {scored.CheckablePassed}/{scored.CheckableTotal} checks and {scored.InsightsFound} insight(s)";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private sealed record AnswerDto(
        int? RecordLength,
        int? OutpusLength,
        Dictionary<string, JsonElement>? Offsets,
        List<string>? Cases,
        Dictionary<string, string>? Hex,
        Dictionary<string, string>? Design,
        List<string>? Observations,
        string? JavaConfidence,
        string? Java,
        string? NotWritten);

    [GeneratedRegex(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"[\s\-]")]
    private static partial Regex HexNoise();

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?(?:-rc\d+|rc\d+)?")]
    private static partial Regex VersionPattern();
}

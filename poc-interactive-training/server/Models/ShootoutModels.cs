namespace PocInteractiveTraining.Server.Models;

// "Right LLM for the right job": four models answer one IBM Enterprise COBOL design question and are
// scored first by what GnuCOBOL says, then by sealed SME checks, then by a blinded judge model.

/// <summary>What the compiled COBOL harness actually printed. This is the ground truth, not an opinion.</summary>
public sealed record ShootoutOracle(
    bool Available,
    string Status,
    int RecordLength,
    int OutpusLength,
    IReadOnlyList<string> CaseLines,
    IReadOnlyDictionary<string, string> Hex,
    IReadOnlyDictionary<string, int> Offsets,
    string RawOutput);

public sealed record ShootoutCheck(string Name, string Expected, string? Given, bool Passed);

public sealed record ShootoutInsight(string Name, bool Present, string Why);

public sealed record ShootoutEntry(
    string Slot,
    string RequestedModel,
    string? ModelUsed,
    string Status,
    long DurationMs,
    int PromptTokens,
    int OutputTokens,
    IReadOnlyList<ShootoutCheck> Checks,
    int CheckablePassed,
    int CheckableTotal,
    IReadOnlyList<ShootoutInsight> Insights,
    int InsightsFound,
    string? JavaConfidence,
    bool JavaProvided,
    IReadOnlyList<string> Observations,
    IReadOnlyDictionary<string, string> Design,
    string? Java,
    string? NotWritten,
    string? Error);

public sealed record ShootoutJudgeSlot(
    string Slot,
    string Verdict,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    int DesignScore);

public sealed record ShootoutVerdict(
    string JudgeModel,
    string Summary,
    IReadOnlyList<ShootoutJudgeSlot> Slots,
    IReadOnlyList<string> Ranking,
    string Recommendation);

public sealed record ShootoutState(
    string Status,
    ShootoutOracle Oracle,
    IReadOnlyList<ShootoutEntry> Entries,
    ShootoutVerdict? Verdict,
    IReadOnlyDictionary<string, string> SlotToModel,
    string? Error);

namespace PocInteractiveTraining.Server.Models;

// "Classify Context" scene. A design document that interleaves two domains is separated back out by a
// local classifier, and the separation is judged by what a model then builds from it. Every record here
// is produced by executed code except the model outputs, which are labelled as such.

/// <summary>One heading-delimited section of a design document, with its sealed domain label.</summary>
public sealed record DesignSegment(
    string Id,
    string Domain,
    string Title,
    string Body,
    IReadOnlyList<string> References);

/// <summary>A segment as it appears in a rendered document: renumbered, with references rewritten.</summary>
public sealed record RenderedSegment(
    int Number,
    string Id,
    string SealedDomain,
    string Title,
    string Body);

/// <summary>What one filter decided about one segment, and why.</summary>
public sealed record SegmentDecision(
    int Number,
    string Id,
    string Title,
    string SealedDomain,
    string Predicted,
    double FixedIncome,
    double Crypto,
    double Shared,
    double Margin,
    bool Kept,
    string Reason);

/// <summary>A filter's output over the whole mixed document, scored against the sealed labels.</summary>
public sealed record FilterResult(
    string Arm,
    string Method,
    IReadOnlyList<SegmentDecision> Decisions,
    int KeptSegments,
    int TotalSegments,
    int FixedIncomeKept,
    int FixedIncomeTotal,
    int CryptoDropped,
    int CryptoTotal,
    int SharedKept,
    int SharedTotal,
    int Uncertain,
    int KeptByReference,
    int TrapsHonoured,
    int TrapsTotal,
    int PromptTokens,
    string FilteredText,
    string? Note);

public sealed record ClassifierTrainingSummary(
    bool EmbeddingsUsed,
    int TrainingDocuments,
    int FixedIncomeDocuments,
    int CryptoDocuments,
    int SharedDocuments,
    int FixedIncomeTerms,
    int CryptoTerms,
    string Method);

public sealed record ImplementationArm(
    string Arm,
    string Label,
    string Status,
    string? ModelUsed,
    int PromptTokens,
    int OutputTokens,
    string? Code,
    bool Compiled,
    IReadOnlyList<string> CompileErrors,
    IReadOnlyList<MigrationCaseResult> Cases,
    int Passed,
    IReadOnlyList<string> LeakedTerms,
    IReadOnlyList<string> Assumptions,
    string? Error);

public sealed record ContextReviewArm(
    string Arm,
    string Verdict,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Gaps);

public sealed record ContextReview(
    string Summary,
    IReadOnlyList<ContextReviewArm> Arms,
    string Recommendation);

public sealed record ClassifyContextState(
    string Status,
    FilterResult? Classifier,
    FilterResult? ClaudeFilter,
    string? ClaudeFilterModel,
    int ClaudeFilterPromptTokens,
    IReadOnlyList<ImplementationArm> Arms,
    ContextReview? Review,
    string? ReviewModel,
    string? Error);

/// <summary>
/// "Tell the model what to ignore" instead of removing it: the classifier's dropped set becomes a
/// deterministic instruction block prepended to the full document. Compared against removal by execution.
/// </summary>
public sealed record FocusState(
    string Status,
    FilterResult? Classifier,
    string? Instruction,
    int InstructionTokens,
    IReadOnlyList<ImplementationArm> Arms,
    ContextReview? Review,
    string? ReviewModel,
    string? Error);

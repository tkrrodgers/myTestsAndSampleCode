namespace PocInteractiveTraining.Server.Models;

public sealed record TrainingSession(
    string SessionId,
    string BridgeToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastBridgeHeartbeat,
    string CoachStatus,
    string ReviewStatus,
    string? CoachModel,
    bool CoachFallbackUsed,
    CoachResponse? Coach,
    string? ReviewModel,
    ReviewResponse? Review,
    IReadOnlyList<ReviewTraceEvent> ReviewTrace,
    string? LastError,
    ComparisonState Comparison,
    RoundTripState RoundTrip,
    ModernizeState Modernize,
    AuditState Audit,
    ContextAuditState ContextAudit,
    ClaraState Clara,
    LanguageTestState LanguageTest,
    MigrationState Migration,
    TicketGateState TicketGate,
    TrapHuntState TrapHunt);

public sealed record BridgeTask(
    string TaskId,
    string SessionId,
    string Kind,
    string PreferredModel,
    string? FallbackModel,
    string SystemPrompt,
    string UserPrompt,
    int TimeoutSeconds,
    DateTimeOffset CreatedAt);

public sealed record BridgeTaskResult(
    string TaskId,
    string SessionId,
    string Status,
    string ModelRequested,
    string? ModelUsed,
    bool FallbackUsed,
    long DurationMs,
    string? Content,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ReviewTraceEvent(
    string TaskId,
    string SessionId,
    int Sequence,
    string Stage,
    string Evidence,
    string Decision,
    DateTimeOffset Timestamp);

public sealed record CoachResponse(
    string LessonTitle,
    IReadOnlyList<CoachScene> Scenes,
    IReadOnlyList<string> OpenQuestions);

public sealed record CoachScene(
    string SceneId,
    string Narration,
    IReadOnlyList<string> SourcePaths);

public sealed record ReviewResponse(
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> UnsupportedAssumptions,
    string SuggestedPrompt,
    IReadOnlyList<string> EvidenceQuotes);

public sealed record QuizQuestion(
    string Prompt,
    IReadOnlyList<string> Options,
    int CorrectIndex,
    string Feedback);

public sealed record ComparisonTopic(
    string Id,
    string Title,
    string Question,
    IReadOnlyList<string> ReferenceUrls,
    IReadOnlyList<string> MustIncludeKeywords);

public sealed record ComparisonAnswer(
    string Slot,
    string RequestedModel,
    string? ModelUsed,
    bool FallbackUsed,
    string Status,
    string? Content,
    long DurationMs,
    IReadOnlyList<string> MatchedKeywords,
    int KeywordCoverage);

public sealed record ComparisonDimensionScore(
    string Slot,
    int Factuality,
    int Completeness,
    int Conciseness,
    string? Notes);

public sealed record ComparisonVerdict(
    string JudgeModel,
    IReadOnlyList<ComparisonDimensionScore> Scores,
    IReadOnlyList<string> Ranking,
    string Rationale);

public sealed record ComparisonState(
    string Status,
    string? TopicId,
    string? TopicTitle,
    string? Question,
    IReadOnlyList<ComparisonAnswer> Answers,
    ComparisonVerdict? Verdict,
    string? Error);

public sealed record RoundTripQa(
    int Fidelity,
    string Summary,
    IReadOnlyList<string> Preserved,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> Risks,
    string Recommendation);

public sealed record RoundTripState(
    string Status,
    string? OriginalClass,
    string? Story,
    string? StoryModel,
    bool StoryFallbackUsed,
    string? RecreatedClass,
    string? RecreatedModel,
    bool RecreatedFallbackUsed,
    RoundTripQa? Qa,
    string? QaModel,
    string? Error);

public sealed record ModernizeResult(
    string ModernizedCode,
    string Explanation,
    IReadOnlyList<string> BusinessRules,
    IReadOnlyList<string> MicroserviceCandidates);

public sealed record ModernizeState(
    string Status,
    string? LegacyCode,
    ModernizeResult? Result,
    string? Model,
    string? Error);

public sealed record AuditMethodMetric(
    string Type,
    string Method,
    int Complexity,
    int Lines,
    int MaxNesting,
    int Parameters);

public sealed record AuditTypeMetric(
    string Name,
    string Kind,
    int Methods,
    int Fields,
    int PublicMembers,
    int Lines,
    IReadOnlyList<string> DependsOn);

public sealed record AuditScore(
    string Dimension,
    int Value,
    string Detail);

public sealed record AuditFinding(
    string Severity,
    string Message);

public sealed record AuditReport(
    int OverallScore,
    string Grade,
    IReadOnlyList<AuditScore> Dimensions,
    IReadOnlyList<AuditFinding> Findings,
    IReadOnlyList<AuditTypeMetric> Types,
    IReadOnlyList<AuditMethodMetric> Methods,
    IReadOnlyList<string> GraphEdges,
    string Summary,
    bool ParseError,
    string? ParseMessage);

public sealed record AuditRecommendation(
    string Summary,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<string> AgenticReadiness);

public sealed record AuditState(
    string Status,
    string? Code,
    AuditReport? Report,
    AuditRecommendation? Recommendation,
    string? Model,
    string? Error);

public sealed record ContextStructureSignal(
    string Name,
    bool Present,
    string Detail);

public sealed record ContextAuditInitial(
    string PredictedLabel,
    int Score,
    int Confidence,
    bool Positive,
    IReadOnlyList<ContextStructureSignal> Signals,
    string Method);

public sealed record ContextJudgeResult(
    int TrapsFound,
    int TrapsTotal,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Missed,
    IReadOnlyList<string> FalsePositives,
    string Verdict);

public sealed record ContextAuditState(
    string Status,
    string? Code,
    ContextAuditInitial? Initial,
    string? GemmaOverview,
    IReadOnlyList<string> GemmaGaps,
    string? GemmaModel,
    ContextJudgeResult? Judge,
    string? JudgeModel,
    IReadOnlyList<string> PlantedTraps,
    string? Error);

public enum ClaraSeverity { Error, Warning }

public sealed record ClaraDiagnostic(
    ClaraSeverity Severity,
    string Code,
    int Line,
    int Column,
    string Scope,
    string Message)
{
    public string Format() =>
        $"{Code} (line {Line}, col {Column}) in {Scope}: {Message}";
}

public sealed record ClaraExampleResult(
    string Description,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Derived,
    bool? Passed,
    string? Note,
    string? MatchedRule);

public sealed record ClaraRuleSummary(string Name, bool IsOtherwise, int Line, string? Because, string? Owner);

public sealed record ClaraProgramResult(
    bool Compiled,
    string? PolicyName,
    string? PolicyDescription,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Derived,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Invariants,
    IReadOnlyList<ClaraRuleSummary> Rules,
    IReadOnlyList<ClaraExampleResult> Examples,
    IReadOnlyList<ClaraDiagnostic> Diagnostics,
    int RuleCount,
    double CompileMilliseconds);

public sealed record ClaraBenchmarkRow(
    string Engine,
    double NanosecondsPerCall,
    double MillionCallsPerSecond,
    double RelativeToCSharp,
    string Detail);

public sealed record ClaraBenchmarkResult(
    string PolicyName,
    long Iterations,
    int Rounds,
    double CompileMilliseconds,
    IReadOnlyList<ClaraBenchmarkRow> Rows,
    int ParityChecks,
    int ParityMismatches,
    string Runtime,
    string Summary);

public sealed record ClaraConformanceCase(string Name, bool Passed, string Detail);

// --- COBOL migration A/B/C test (tab 16) ---

public sealed record CobolField(string Name, string Base, int Offset, int Size, string Attribute);

public sealed record CobolFacts(
    bool Compiled,
    string Error,
    IReadOnlyList<CobolField> Fields,
    IReadOnlyList<string> Paragraphs,
    IReadOnlyList<string> ControlFlow,
    string GeneratedC);

public sealed record CobolOracleRun(string Input, string Output, string? Error);

public sealed record MigrationOutput(string Input, string Value, string? Error);

public sealed record MigrationRunResult(
    bool Ran,
    string Error,
    IReadOnlyList<string> CompileErrors,
    IReadOnlyList<MigrationOutput> Outputs);

public sealed record MigrationCaseResult(string Input, string Expected, string Actual, bool Matched, string? Note);

public sealed record MigrationArm(
    string Arm,
    string Grounding,
    string Status,
    string RequestedModel,
    string? ModelUsed,
    long DurationMs,
    int PromptTokens,
    string? Code,
    bool Compiled,
    IReadOnlyList<string> CompileErrors,
    IReadOnlyList<MigrationCaseResult> Cases,
    int Passed,
    string? Error);

public sealed record MigrationState(
    string Status,
    string CobolSource,
    string StructuralArtifacts,
    IReadOnlyList<CobolField> CompilerFields,
    IReadOnlyList<string> CompilerControlFlow,
    IReadOnlyList<CobolOracleRun> Oracle,
    bool ToolchainAvailable,
    string ToolchainStatus,
    IReadOnlyList<MigrationArm> Arms,
    string? Error);

// --- Tab 17: ticket quality gate ---

public sealed record TicketPrecheck(
    int Score,
    bool Passed,
    IReadOnlyList<ContextStructureSignal> Signals,
    IReadOnlyList<string> VagueTerms);

public sealed record TicketReview(
    string Model,
    int Score,
    bool Passed,
    IReadOnlyList<ContextStructureSignal> Findings,
    string Rewritten,
    string Verdict);

public sealed record TicketGateState(
    string Status,
    string? Ticket,
    TicketPrecheck? Precheck,
    TicketReview? Review,
    string? Error);

// --- Tab 18: cloud design trap hunt ---

public sealed record TrapHuntState(
    string Status,
    string Design,
    string? Findings,
    string? FindingsModel,
    ContextJudgeResult? Grade,
    string? JudgeModel,
    IReadOnlyList<string> PlantedTraps,
    string? Error);

public sealed record ClaraConformanceReport(
    int Total,
    int Passed,
    IReadOnlyList<ClaraConformanceCase> Cases,
    double DurationMilliseconds);

// --- Language A/B test: the same JIRA given to the same model over C# and over CLARA ---

public sealed record LanguageTestArm(
    string Arm,
    string Language,
    string Status,
    string RequestedModel,
    string? ModelUsed,
    bool FallbackUsed,
    long DurationMs,
    int PromptTokens,
    string? Answer,
    ClaraProgramResult? Verification,
    string? Error);

public sealed record LanguageTestCriterionScore(
    string Arm,
    string Criterion,
    bool Met,
    string Evidence);

public sealed record LanguageTestVerdict(
    string JudgeModel,
    IReadOnlyList<LanguageTestCriterionScore> Scores,
    int CSharpMet,
    int ClaraMet,
    int CriteriaTotal,
    string Winner,
    string Rationale,
    IReadOnlyList<string> LocalKnowledgeMissed,
    IReadOnlyList<string> Caveats);

public sealed record LanguageTestState(
    string Status,
    string? Jira,
    string CSharpSource,
    string ClaraSource,
    int CSharpTokens,
    int ClaraTokens,
    int ContextPackTokens,
    string TokenMethod,
    IReadOnlyList<LanguageTestArm> Arms,
    LanguageTestVerdict? Verdict,
    IReadOnlyList<string> SealedCriteria,
    bool CriteriaRevealed,
    string? Error);

public sealed record ClaraReview(
    int Fidelity,
    string Verdict,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> LanguageNotes);

public sealed record ClaraState(
    string Status,
    string? Jira,
    string? Source,
    string? AuthorModel,
    ClaraProgramResult? Result,
    ClaraReview? Review,
    string? ReviewModel,
    string? Error);
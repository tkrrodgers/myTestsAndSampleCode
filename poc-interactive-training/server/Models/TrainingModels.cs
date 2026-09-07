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
    string ReviewPartial,
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
    TrapHuntState TrapHunt,
    DriftState Drift,
    GroundedChangeState GroundedChange,
    LlmSupportState LlmSupport,
    ContextCurveState ContextCurve,
    FramingState Framing,
    EphemeralState Ephemeral,
    ConsolidationState Consolidation,
    PortfolioTierState PortfolioTiers,
    RegressionGuidanceState RegressionGuidance,
    PatternRunState PatternRun,
    CryptoSmeState CryptoSme,
    NarrationState Narration);

public sealed record NarrationState(
    string Status,
    string? Text,
    string? Model,
    bool FallbackUsed);

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

public sealed record ReviewStreamChunk(
    string TaskId,
    string SessionId,
    string Text);

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
    int KeywordCoverage,
    string? Error,
    string? ErrorCode);

public sealed record ReferenceDocument(
    string Url,
    string Title,
    bool Retrieved,
    int Characters,
    string Text,
    bool Truncated,
    DateTimeOffset AttemptedAt,
    string? Error,
    bool FromCache);

public sealed record ComparisonDimensionScore(
    string Slot,
    int Factuality,
    int Completeness,
    int Conciseness,
    string? Notes,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Unsupported);

public sealed record ComparisonVerdict(
    string JudgeModel,
    IReadOnlyList<ComparisonDimensionScore> Scores,
    IReadOnlyList<string> Ranking,
    string Rationale,
    string? CoverageNote);

public sealed record ComparisonState(
    string Status,
    string? TopicId,
    string? TopicTitle,
    string? Question,
    IReadOnlyList<ComparisonAnswer> Answers,
    ComparisonVerdict? Verdict,
    IReadOnlyList<ReferenceDocument> Sources,
    int GroundingTokens,
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

// --- Scene 4: does the OKF context actually let an agent resolve the JIRA? ---

public sealed record FixtureArtifact(string Path, string Kind, string Content);

public sealed record GroundingCitation(
    string Change,
    string Document,
    string Statement,
    bool Grounded);

public sealed record GroundedChangeAudit(
    string Model,
    IReadOnlyList<GroundingCitation> Citations,
    IReadOnlyList<string> Ungrounded,
    bool OpenQuestionRaised,
    string OpenQuestionNote,
    bool PlaceholdersMarked,
    string PlaceholderNote,
    string Verdict);

public sealed record GroundedChangeState(
    string Status,
    string Jira,
    IReadOnlyList<FixtureArtifact> Context,
    string? Implementation,
    string? ImplementationModel,
    int PromptTokens,
    GroundedChangeAudit? Audit,
    string? Error);

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

// --- Tab 19: drift scorecard against a golden baseline ---

public sealed record DriftStructural(int Score, IReadOnlyList<ContextStructureSignal> Findings);

public sealed record DriftSemantic(
    string Model,
    int GoalFidelity,
    int ScopeDiscipline,
    IReadOnlyList<string> Deviations,
    string Verdict);

public sealed record DriftState(
    string Status,
    string Ticket,
    string FrozenCode,
    string HumanPatch,
    string CandidatePatch,
    string CandidateLabel,
    DriftStructural? Structural,
    DriftSemantic? Semantic,
    string? Error);

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

public sealed record GeminiAdvice(
    string Overview,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Citations);

public sealed record SupportDesignStep(
    string Step,
    string Detail,
    string Source);

public sealed record SupportSynthesis(
    IReadOnlyList<string> AlreadyKnew,
    IReadOnlyList<string> LearnedFromGemini,
    IReadOnlyList<string> NeedsVerification,
    string DesignSummary,
    IReadOnlyList<SupportDesignStep> DesignSteps,
    string ProvenanceNote);

public sealed record LlmSupportState(
    string Status,
    string? Requirement,
    GeminiAdvice? Advice,
    string? AdviceModel,
    SupportSynthesis? Synthesis,
    string? SynthesisModel,
    string? Error);

// --- Phase 0 prerequisites: agent registry, data tiering, incident path (deterministic, no model) ---

public sealed class AgentRegistration
{
    public string Name { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string ReleaseId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string BlastRadius { get; set; } = "Repo";
    public string Autonomy { get; set; } = "Suggests";
    public string DataTier { get; set; } = "Internal";
    public string AgentStatus { get; set; } = "Piloting";
    public bool ToolEnabled { get; set; }
    public string RuntimeIdentity { get; set; } = string.Empty;
    public string ActionPolicy { get; set; } = string.Empty;
    public string ApprovalMode { get; set; } = string.Empty;
    public int? DependsOnTier { get; set; }
}

public sealed record ControlRequirement(
    string Control,
    string Requirement,
    bool Required);

public sealed record AgentTierResult(
    int Tier,
    string TierName,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> MissingFields,
    bool Registrable,
    IReadOnlyList<ControlRequirement> Controls,
    string ReviewCadence);

public sealed record DataTierFinding(
    string Label,
    string Category,
    int Tier,
    int Count,
    string Sample);

public sealed record IncidentPath(
    string Severity,
    string Summary,
    IReadOnlyList<string> Steps,
    string Owner,
    string Sla);

public sealed record DataTierAssessment(
    int Tier,
    string TierName,
    string Decision,
    string Rationale,
    IReadOnlyList<DataTierFinding> Findings,
    string Scrubbed,
    IncidentPath Incident);

// --- Phase 1: context sufficiency tiers, action authority, neutral framing ---

public sealed record ContextTierRun(
    int Tier,
    int Attempt,
    string Status,
    int Score,
    IReadOnlyList<string> Met,
    IReadOnlyList<string> Missed,
    string? Plan,
    string? ModelUsed,
    string? Error);

public sealed record ContextTierResult(
    int Index,
    string Id,
    string Label,
    string Description,
    int PromptTokens,
    int MedianScore,
    int BestScore,
    int WorstScore,
    IReadOnlyList<string> MedianMet,
    IReadOnlyList<string> MedianMissed,
    IReadOnlyList<ContextTierRun> Runs);

public sealed record TierDegradation(
    string BestTier,
    string WorstTier,
    int Drop,
    IReadOnlyList<string> LostCriteria,
    string FloorTier,
    string Note);

public sealed record TierJudgement(
    string Tier,
    int Correctness,
    int Grounding,
    string Assessment,
    IReadOnlyList<string> Errors);

public sealed record CurveJudgeVerdict(
    string Summary,
    IReadOnlyList<TierJudgement> Tiers,
    IReadOnlyList<string> DegradationEvidence,
    string MinimumViableContext,
    string Caveat);

public sealed record ContextCurveState(
    string Status,
    int RunsPerTier,
    IReadOnlyList<ContextTierResult> Tiers,
    TierDegradation? Degradation,
    CurveJudgeVerdict? Verdict,
    string? JudgeModel,
    bool TokensExact,
    string? Error);

public sealed record CandidateAction(
    string Id,
    string Description,
    bool Reversible,
    bool ProductionAffecting,
    bool CustomerVisible,
    bool SecurityPolicy,
    bool InEnvelope);

public sealed record ActionAuthorityDecision(
    string ActionId,
    string Description,
    string Verdict,
    string VerdictLabel,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> ExactActionChecks,
    IncidentPath Intervention);

public sealed record FramingArm(
    string Id,
    string Label,
    string Framing,
    string Status,
    string? Answer,
    string? ModelUsed,
    int PromptTokens,
    double SimilarityToNeutral,
    string? Error);

public sealed record FramingVerdict(
    bool SubstanceChanged,
    string Summary,
    IReadOnlyList<string> Differences,
    string Recommendation);

public sealed record FramingState(
    string Status,
    string? Question,
    IReadOnlyList<FramingArm> Arms,
    FramingVerdict? Verdict,
    string? JudgeModel,
    string? Error);

// --- Phase 2: regression adequacy (real mutation testing) and the ephemeral test-ticket harness ---

public sealed record MutantResult(
    string Description,
    int Line,
    string Original,
    string Mutated,
    bool Killed,
    string Detail);

public sealed record MutationReport(
    bool Ran,
    int MutationScore,
    int TotalMutants,
    int KilledMutants,
    IReadOnlyList<string> BaselineTests,
    IReadOnlyList<MutantResult> Mutants,
    string Verdict,
    string? Error);

// One row of QA's condition-permutation data, run through both the production baseline and the
// candidate. Classification is what turns a raw diff into a decision.
public sealed record VectorComparison(
    int Row,
    string Inputs,
    string BaselineResult,
    string CandidateResult,
    bool Differs,
    string Classification,
    string Note);

public sealed record DifferentialReport(
    bool Ran,
    int VectorCount,
    int Identical,
    int Intended,
    int Unintended,
    int NotImplemented,
    IReadOnlyList<VectorComparison> Comparisons,
    IReadOnlyList<string> DeclaredChanges,
    string Verdict,
    string? Error);

public sealed record PredicateCoverage(
    string Expression,
    int Line,
    bool TrueSeen,
    bool FalseSeen,
    bool Evaluable);

public sealed record ConditionCoverageReport(
    bool Ran,
    int Predicates,
    int FullyExercised,
    int CoveragePercent,
    IReadOnlyList<PredicateCoverage> Details,
    IReadOnlyList<string> Gaps,
    int SkippedPredicates,
    string Verdict,
    string? Error);

public sealed record RegressionAuditReport(
    DifferentialReport Differential,
    ConditionCoverageReport Coverage,
    MutationReport Mutation);

public sealed record ProposedTest(
    string Name,
    string TargetsSurvivor,
    string Behaviour,
    string Assertion);

public sealed record MutationRemediation(
    string Summary,
    IReadOnlyList<ProposedTest> Tests,
    IReadOnlyList<string> NotWorthTesting,
    string? Model);

public sealed record TestAssessment(
    string Name,
    bool WouldKill,
    string Reasoning);

public sealed record MutationReview(
    string Verdict,
    IReadOnlyList<TestAssessment> Assessments,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> Overreach,
    string NextStep,
    string? Model);

public sealed record RegressionGuidanceState(
    string Status,
    MutationRemediation? Remediation,
    MutationReview? Review,
    string? Error);

public sealed record EphemeralRecord(
    string TicketId,
    string Owner,
    string Storage,
    string Ttl,
    string LogPolicy,
    string PayloadDigest,
    IReadOnlyList<string> Purged);

public sealed record EphemeralState(
    string Status,
    string? RawTicket,
    DataTierAssessment? Gate,
    EphemeralRecord? Record,
    string? AgentPlan,
    string? AgentModel,
    string? Error);

// --- Phase 3: portfolio context tiers and cross-repo common-code audit (deterministic) ---

public sealed record TierService(
    string Name,
    string AmpId,
    string Domain,
    bool HandlesClientOrders,
    string Health,
    string Sla,
    IReadOnlyList<string> DependsOn,
    string? Repository);

public sealed record TierAmp(
    string AmpId,
    string Name,
    string Team,
    string RegulatoryPerimeter,
    IReadOnlyList<string> AssetClasses,
    IReadOnlyList<string> Repositories,
    string ScopeNote);

public sealed record TierRepository(
    string Name,
    string AmpId,
    string Service,
    int OkfDocuments,
    int CodeFiles,
    IReadOnlyList<string> OkfTypes,
    bool Available);

public sealed record TierCost(
    int Tier1Tokens,
    int Tier2Tokens,
    int Tier3TokensInScope,
    int Tier3TokensEverything,
    bool Exact);

public sealed record PortfolioReport(
    bool CorpusAvailable,
    string CorpusStatus,
    IReadOnlyList<TierService> Services,
    IReadOnlyList<TierAmp> Amps,
    IReadOnlyList<TierRepository> Repositories,
    TierCost Cost,
    IReadOnlyList<string> SharedDependencyWarning);

// --- The three-stage funnel: discover on Tier 1, scope on Tier 2, design on Tier 3 ---

public sealed record TierCandidate(
    string Service,
    string AmpId,
    string Why);

public sealed record Tier1Discovery(
    IReadOnlyList<TierCandidate> Candidates,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> CannotDetermineYet,
    string Summary);

public sealed record TierScopeDecision(
    string Service,
    bool InScope,
    string Reason,
    string Evidence);

public sealed record Tier2Scoping(
    IReadOnlyList<TierScopeDecision> Decisions,
    string SharedLibraryVerdict,
    string Summary);

public sealed record ServiceChange(
    string File,
    string Change,
    string Justification);

public sealed record ServiceDesign(
    string Service,
    string Repository,
    string Summary,
    IReadOnlyList<ServiceChange> Changes,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> OpenQuestions);

public sealed record PortfolioTierState(
    string Status,
    Tier1Discovery? Discovery,
    Tier2Scoping? Scoping,
    IReadOnlyList<ServiceDesign> Designs,
    string? Model,
    TierCost? Cost,
    string? Error);

public sealed record CommonCodeMember(
    string Name,
    string Repo,
    int Lines);

public sealed record CommonCodeCluster(
    string Name,
    string Kind,
    IReadOnlyList<CommonCodeMember> Members,
    IReadOnlyList<string> Repos,
    int DuplicateLines,
    bool IsDuplication,
    string Recommendation);

public sealed record SimilarityPair(
    string Left,
    string Right,
    double Similarity,
    bool CrossRepo);

public sealed record CorpusRepo(
    string Name,
    string AssetClass,
    string Divergence,
    string OkfQuality,
    int Files,
    int Lines,
    int NonFunctionalFiles,
    int FunctionalFiles);

// How well the clustering separated known duplicates from everything else, measured against the source tree.
public sealed record SeparationMetrics(
    double DuplicateMean,
    double DuplicateMin,
    double UnrelatedMean,
    double UnrelatedMax,
    double Margin,
    int NearestNeighbourHits,
    int NearestNeighbourTotal);

public sealed record CommonCodeReport(
    bool UsedEmbeddings,
    bool CorpusAvailable,
    string CorpusStatus,
    double Threshold,
    int UnitCount,
    IReadOnlyList<CorpusRepo> Repos,
    SeparationMetrics Separation,
    IReadOnlyList<CommonCodeCluster> Clusters,
    int DuplicatedClusters,
    int FunctionalPrize,
    int NonFunctionalPrize,
    IReadOnlyList<string> Strategy,
    IReadOnlyList<SimilarityPair> TopPairs);

// --- Consolidation: Claude designs, Gemma implements ---

public sealed record ConsolidationModule(
    string Name,
    string Responsibility,
    string PublicApi,
    IReadOnlyList<string> Replaces);

public sealed record ConsolidationDesign(
    string LibraryName,
    string Summary,
    IReadOnlyList<ConsolidationModule> Modules,
    IReadOnlyList<string> MigrationSteps,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> OutOfScope,
    string ImplementationSpec);

public sealed record ConsolidationFile(
    string Path,
    string Purpose,
    string Source);

public sealed record ConsolidationBuild(
    IReadOnlyList<ConsolidationFile> Files,
    IReadOnlyList<string> Assumptions,
    int ModulesTotal,
    int ModulesCompleted,
    IReadOnlyList<string> FailedModules);

// The point of the split: the expensive model writes the spec, the cheap model writes the volume.
public sealed record ConsolidationTokens(
    int DesignPromptTokens,
    int DesignOutputTokens,
    int BuildPromptTokens,
    int BuildOutputTokens,
    int CorpusTokens,
    bool Exact);

public sealed record ConsolidationState(
    string Status,
    ConsolidationDesign? Design,
    string? DesignModel,
    ConsolidationBuild? Build,
    string? BuildModel,
    ConsolidationTokens? Tokens,
    string? Error);

// --- Phase 4: token economics, pattern library, plan-first, leadership walkthrough ---

public sealed record EconomicsRow(
    string Rung,
    string Model,
    string Kind,
    int SuccessPercent,
    decimal CostPerAttempt,
    decimal AttemptsPerSuccess,
    decimal CostPerSuccess,
    string Note);

public sealed record EconomicsReport(
    int PromptTokens,
    int OutputTokens,
    IReadOnlyList<EconomicsRow> Rows,
    string CheapestPerSuccess,
    string CheapestPerAttempt,
    bool RateCardMisleads,
    IReadOnlyList<string> Policy);

public sealed class PatternSubmission
{
    public string Title { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
    public string Approach { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string ContextNeeded { get; set; } = string.Empty;
    public string FailureModes { get; set; } = string.Empty;
}

public sealed record LibraryEntry(
    string Title,
    string Problem,
    string Approach,
    string Evidence,
    string ContextNeeded,
    string FailureModes,
    bool Admitted,
    int Score,
    IReadOnlyList<string> Tags);

public sealed record CurationCheck(
    string Name,
    bool Passed,
    string Requirement,
    string Why);

public sealed record LibraryReview(
    bool Admitted,
    int Score,
    IReadOnlyList<CurationCheck> Checks,
    string Verdict,
    int LibrarySize);

public sealed record PatternApplyReview(
    int Fidelity,
    string Verdict,
    IReadOnlyList<string> Followed,
    IReadOnlyList<string> Ignored,
    IReadOnlyList<string> Risks,
    string PromptRecommendation,
    string? Model);

public sealed record PatternRunState(
    string Status,
    string? ModifiedClass,
    string? GemmaModel,
    PatternApplyReview? Review,
    string? Error);

// --- Making Gemma an SME: unaided attempt, grounded subagent answers, then the design ---

public sealed record SmeUnaided(
    string Attempt,
    IReadOnlyList<string> Uncertain,
    IReadOnlyList<string> Questions,
    string? Model,
    int FactScore,
    IReadOnlyList<string> FactsHit,
    IReadOnlyList<string> FactsMissed);

public sealed record SmeAnswer(
    string Question,
    string Answer,
    string Citation,
    bool InPack);

public sealed record SmeConsultation(
    IReadOnlyList<SmeAnswer> Answers,
    IReadOnlyList<string> Corrections,
    IReadOnlyList<string> Unprompted,
    string? Model,
    int PackTokens);

public sealed record SmeFactTrace(
    string Name,
    bool InUnaided,
    bool Asked,
    bool AnsweredBySme,
    bool InDesign,
    string Verdict);

public sealed record SmeDesignStep(
    string Step,
    string Detail,
    string Source);

public sealed record SmeDesign(
    string Summary,
    IReadOnlyList<SmeDesignStep> Steps,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> OpenQuestions,
    string? Model,
    int FactScore,
    IReadOnlyList<string> FactsHit,
    IReadOnlyList<string> FactsMissed);

public sealed record CryptoSmeState(
    string Status,
    SmeUnaided? Unaided,
    SmeConsultation? Consultation,
    SmeDesign? Design,
    IReadOnlyList<SmeFactTrace> Trace,
    string? Error);

public sealed record PlanCheck(
    string Name,
    bool Passed,
    string Requirement);

public sealed record PlanReview(
    int Score,
    IReadOnlyList<PlanCheck> Checks,
    string Verdict,
    int SummaryWords);

public sealed record LeadershipAct(
    string Act,
    string Question,
    string Control,
    string Metric,
    string Summary,
    int SceneIndex,
    string SceneName);
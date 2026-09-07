using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

public sealed class TrainingSessionStore
{
    private const string GemmaModel = "google/gemma-4-31B-it via novita";
    private const string GptFallbackModel = "GPT-5.6 Sol";
    private const string ClaudeModel = "Claude Opus 5";
    private const string ClaraAuthorModel = "Claude Opus 4.8";
    private const string GeminiAdvisorModel = "Gemini 3.7 Flash";
    private static readonly string[] ComparisonModels = ["GPT-5.6 Sol", "Claude Opus 5.0", "Gemini 3.7 Flash"];
    private const string JudgeModel = "GPT-5.6 Sol";
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly object _gate = new();
    private readonly TrainingFixtureProvider _fixture;
    private readonly CSharpAuditAnalyzer _auditAnalyzer;
    private readonly ContextAuditService _contextAudit;
    private readonly DataTierClassifier _dataTier;
    private readonly TradingCorpus _tradingCorpus;
    private readonly DocumentationFetcher _documentation;
    private readonly EmbeddingGemmaEncoder _encoder;
    private readonly CobolToolchain _cobol;
    private readonly MigrationSandbox _sandbox;

    // Measured once: what each representation of the same logic actually costs in context.
    private static int CSharpSourceTokens;
    private static int ClaraSourceTokens;
    private static int ContextPackTokens;
    private static bool TokensAreExact;
    private static bool ToolchainAvailable;
    private static string ToolchainStatus = "not probed";
    private static IReadOnlyList<FixtureArtifact> FixtureArtifacts = [];
    private static string FixtureJira = "";

    // The tier prompts are constants, so their cost is known before anything runs.
    private static IReadOnlyDictionary<int, int> TierPromptTokens = new Dictionary<int, int>();

    // The fixture ticket ends with a section naming the omission. That is a note to the training author,
    // not part of the ticket - leaving it in would hand the planted open question straight to the model.
    private static string StripPlantedHint(string ticket)
    {
        var marker = ticket.IndexOf("## Intentionally Missing", StringComparison.Ordinal);
        return marker < 0 ? ticket.Trim() : ticket[..marker].TrimEnd();
    }

    private static readonly string[] ContextTraps =
    [
        "Daily rate is 2.5% in code but the context specifies 1.5%.",
        "Fee cap is $500 in code but the context specifies $250.",
        "The balance <= 0 guard required by rule 3 is missing.",
        "The 3-day grace period required by rule 5 is not implemented.",
        "The fee is rounded to whole dollars, but rule 6 requires 2 decimal places."
    ];

    public TrainingSessionStore(TrainingFixtureProvider fixture, CSharpAuditAnalyzer auditAnalyzer, ContextAuditService contextAudit, EmbeddingGemmaEncoder encoder, CobolToolchain cobol, MigrationSandbox sandbox, DataTierClassifier dataTier, TradingCorpus tradingCorpus, DocumentationFetcher documentation)
    {
        _fixture = fixture;
        _auditAnalyzer = auditAnalyzer;
        _contextAudit = contextAudit;
        _encoder = encoder;
        _cobol = cobol;
        _sandbox = sandbox;
        _dataTier = dataTier;
        _tradingCorpus = tradingCorpus;
        _documentation = documentation;

        var (csharpTokens, exact) = _encoder.CountTokens(LanguageTestSamples.CSharpSource);
        CSharpSourceTokens = csharpTokens;
        ClaraSourceTokens = _encoder.CountTokens(LanguageTestSamples.ClaraSource).Tokens;
        ContextPackTokens = _encoder.CountTokens(LanguageTestSamples.ContextPack).Tokens;
        TokensAreExact = exact;
        ToolchainAvailable = _cobol.IsAvailable;
        ToolchainStatus = _cobol.StatusMessage;
        FixtureArtifacts = _fixture.Artifacts();
        FixtureJira = StripPlantedHint(_fixture.Read("jira/FUL-1842.md"));
        TierPromptTokens = ContextTierSamples.Tiers.ToDictionary(
            tier => tier.Index,
            tier => _encoder.CountTokens(Prompts.ContextTierSystem + "\n" + ContextTierSamples.BuildRequest(tier)).Tokens);
    }

    public TrainingSession CreateSession()
    {
        var state = new SessionState(
            Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('='));
        _sessions[state.SessionId] = state;
        return state.Snapshot();
    }

    public TrainingSession GetSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var state)
            ? state.Snapshot()
            : throw new InvalidOperationException("Training session was not found.");

    public bool IsValidToken(string token) =>
        _sessions.Values.Any(session => FixedTimeEquals(session.BridgeToken, token));

    public object RecordHeartbeat(string token)
    {
        var session = FindByToken(token)!;
        lock (_gate)
        {
            session.LastBridgeHeartbeat = DateTimeOffset.UtcNow;
        }

        return new { session.SessionId, status = "connected" };
    }

    public bool IsBridgeConnected(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.LastBridgeHeartbeat is null)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - session.LastBridgeHeartbeat < TimeSpan.FromSeconds(12);
    }

    public void QueueCoach(string sessionId)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            if (session.Tasks.Values.Any(task => task.Task.Kind == "coach-narration" && task.Status != "failed"))
            {
                return;
            }

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "coach-narration",
                GemmaModel,
                GptFallbackModel,
                Prompts.CoachSystem,
                _fixture.BuildCoachRequest(),
                45,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.CoachStatus = "queued";
            session.LastError = null;
        }
    }

    /// <summary>
    /// Narration for one autopilot step. Gemma is primary; the bridge falls back to Claude only on a
    /// mechanical failure, never because someone judged Gemma's prose to be worse.
    /// </summary>
    public string QueueNarration(string sessionId, AutoStep step, string? observedValues)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            var request = new System.Text.StringBuilder();
            request.AppendLine("Narrate this step of a training walkthrough.");
            request.AppendLine();
            request.AppendLine("FACTS YOU MAY STATE (use only these):");
            foreach (var fact in step.Facts)
            {
                request.AppendLine($"- {fact}");
            }

            if (step.MustNotClaim.Count > 0)
            {
                request.AppendLine();
                request.AppendLine("YOU MUST NOT CLAIM:");
                foreach (var claim in step.MustNotClaim)
                {
                    request.AppendLine($"- {claim}");
                }
            }

            if (!string.IsNullOrWhiteSpace(observedValues))
            {
                request.AppendLine();
                request.AppendLine("OBSERVED VALUES (these are real results; you may quote these numbers):");
                request.AppendLine(observedValues);
            }

            request.AppendLine();
            request.AppendLine($"Maximum {step.MaxWords} words. Plain spoken English.");

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "autopilot-narration",
                GemmaModel,
                ClaudeModel,
                Prompts.AutopilotSystem,
                request.ToString(),
                40,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.NarrationStatus = "queued";
            session.NarrationText = null;
            session.NarrationModel = null;
            session.NarrationFallbackUsed = false;
            return task.TaskId;
        }
    }

    private static void CompleteNarration(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.NarrationStatus = "failed";
            session.NarrationText = null;
            return;
        }

        if (!TryDeserialize<NarrationDto>(result.Content!, out var dto) ||
            dto is null || string.IsNullOrWhiteSpace(dto.Narration))
        {
            session.NarrationStatus = "failed";
            return;
        }

        session.NarrationText = dto.Narration.Trim();
        session.NarrationModel = result.ModelUsed;
        session.NarrationFallbackUsed = result.FallbackUsed;
        session.NarrationStatus = "completed";
    }

    private sealed record NarrationDto(string? Narration);

    public void QueueReview(string sessionId, string learnerPrompt)
    {
        if (string.IsNullOrWhiteSpace(learnerPrompt) || learnerPrompt.Length > 6000)
        {
            throw new ArgumentException("Enter a prompt between 1 and 6000 characters.", nameof(learnerPrompt));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "claude-review",
                ClaudeModel,
                null,
                Prompts.ReviewSystem,
                _fixture.BuildReviewRequest(learnerPrompt),
                60,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.ReviewStatus = "queued";
            session.Review = null;
            session.ReviewModel = null;
            session.ReviewTrace.Clear();
            session.ReviewPartial = string.Empty;
            session.LastError = null;
        }
    }

    public void QueueComparison(string sessionId, string topicId)
    {
        var topic = ComparisonCatalog.Find(topicId)
            ?? throw new ArgumentException("Unknown comparison topic.", nameof(topicId));

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.ComparisonSlots.Clear();
            session.ComparisonTaskSlot.Clear();
            session.ComparisonJudgeTaskId = null;
            session.ComparisonVerdict = null;
            session.ComparisonSources = [];
            session.ComparisonGroundingTokens = 0;
            session.ComparisonGroundingStarted = false;
            session.ComparisonError = null;
            session.ComparisonTopic = topic;
            session.ComparisonStatus = "queued";

            var slots = new[] { "A", "B", "C" };
            // Shuffle which model lands in which slot so the blinded judge cannot infer identity from position.
            var models = ComparisonModels.OrderBy(_ => Random.Shared.Next()).ToArray();
            for (var index = 0; index < slots.Length; index++)
            {
                var task = new BridgeTask(
                    Guid.NewGuid().ToString("N"),
                    sessionId,
                    "model-answer",
                    models[index],
                    null,
                    Prompts.AnswerSystem,
                    BuildAnswerRequest(topic),
                    60,
                    DateTimeOffset.UtcNow);
                session.ComparisonSlots[slots[index]] = new ComparisonSlotState(slots[index], models[index]) { TaskId = task.TaskId };
                session.ComparisonTaskSlot[task.TaskId] = slots[index];
                session.Tasks[task.TaskId] = new TaskState(task);
            }
        }
    }

    private void MaybeQueueJudge(SessionState session)
    {
        if (session.ComparisonSlots.Count == 0 || session.ComparisonJudgeTaskId is not null || session.ComparisonGroundingStarted)
        {
            return;
        }

        if (session.ComparisonSlots.Values.Any(slot => slot.Status is "pending" or "running"))
        {
            return;
        }

        var answered = session.ComparisonSlots.Values
            .Count(slot => slot.Status == "completed" && !string.IsNullOrWhiteSpace(slot.Content));
        if (answered < 2)
        {
            session.ComparisonStatus = "failed";
            session.ComparisonError = "Not enough model answers completed to run an evaluation.";
            return;
        }

        // Retrieval is network I/O and must not run under the session lock.
        session.ComparisonGroundingStarted = true;
        session.ComparisonStatus = "grounding";
        var topic = session.ComparisonTopic!;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<ReferenceDocument> documents;
            try
            {
                documents = await _documentation.FetchAsync(topic.ReferenceUrls, CancellationToken.None);
            }
            catch (Exception ex)
            {
                documents = topic.ReferenceUrls
                    .Select(url => new ReferenceDocument(url, url, false, 0, string.Empty, false, DateTimeOffset.UtcNow, ex.Message, false))
                    .ToList();
            }

            lock (_gate)
            {
                session.ComparisonSources = documents;
                var retrieved = documents.Where(document => document.Retrieved).ToList();
                if (retrieved.Count == 0)
                {
                    session.ComparisonStatus = "failed";
                    session.ComparisonError = "No reference documentation could be retrieved, so there is nothing to judge against. "
                        + string.Join(" | ", documents.Select(document => $"{document.Url}: {document.Error}"));
                    return;
                }

                var request = BuildJudgeRequest(session, retrieved);
                session.ComparisonGroundingTokens = _encoder.CountTokens(request).Tokens;

                var judgeTask = new BridgeTask(
                    Guid.NewGuid().ToString("N"),
                    session.SessionId,
                    "model-judge",
                    ClaraAuthorModel,
                    JudgeModel,
                    Prompts.JudgeSystem,
                    request,
                    240,
                    DateTimeOffset.UtcNow);
                session.ComparisonJudgeTaskId = judgeTask.TaskId;
                session.Tasks[judgeTask.TaskId] = new TaskState(judgeTask);
                session.ComparisonStatus = "judging";
            }
        });
    }

    private static string BuildAnswerRequest(ComparisonTopic topic) => $"""
        <question>
        {topic.Question}
        </question>
        Answer accurately and concisely for a technical practitioner. Prefer correct, specific detail over length.
        If you are not certain of a fact, say so rather than inventing it.
        """;

    private static string BuildJudgeRequest(SessionState session, IReadOnlyList<ReferenceDocument> documents)
    {
        var topic = session.ComparisonTopic!;
        var request = new System.Text.StringBuilder();
        request.AppendLine("<question>");
        request.AppendLine(topic.Question);
        request.AppendLine("</question>");

        request.AppendLine("<authoritative_documentation>");
        request.AppendLine("The following text was retrieved from the official Google Cloud documentation at judging time. It is your ONLY source of ground truth.");
        foreach (var document in documents)
        {
            request.AppendLine($"<document url=\"{document.Url}\" title=\"{document.Title}\" retrieved_at=\"{document.AttemptedAt:u}\"{(document.Truncated ? " truncated=\"true\"" : "")}>");
            request.AppendLine(document.Text);
            request.AppendLine("</document>");
        }

        request.AppendLine("</authoritative_documentation>");

        request.AppendLine("<keyword_checklist>");
        request.AppendLine("Terms a complete answer would normally use. Presence is not correctness and absence is not error — use these only as a prompt to check coverage:");
        foreach (var keyword in topic.MustIncludeKeywords)
        {
            request.AppendLine($"- {keyword}");
        }

        request.AppendLine("</keyword_checklist>");

        foreach (var slot in session.ComparisonSlots.Values
            .Where(slot => slot.Status == "completed" && !string.IsNullOrWhiteSpace(slot.Content))
            .OrderBy(slot => slot.Slot, StringComparer.Ordinal))
        {
            request.AppendLine($"<answer id=\"{slot.Slot}\">");
            request.AppendLine(slot.Content);
            request.AppendLine("</answer>");
        }

        request.AppendLine("Score each answer against the retrieved documentation only.");
        return request.ToString();
    }

    private static (List<string> Matched, int Coverage) ScoreKeywords(ComparisonTopic topic, string content)
    {
        var text = content.ToLowerInvariant();
        var matched = topic.MustIncludeKeywords
            .Where(keyword => text.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal))
            .ToList();
        var coverage = topic.MustIncludeKeywords.Count == 0
            ? 0
            : (int)Math.Round(100.0 * matched.Count / topic.MustIncludeKeywords.Count);
        return (matched, coverage);
    }

    private static int ClampScore(int value) => Math.Clamp(value, 1, 5);

    public void QueueRoundTrip(string sessionId, string originalClass)
    {
        if (string.IsNullOrWhiteSpace(originalClass) || originalClass.Length > 12000)
        {
            throw new ArgumentException("Provide a class between 1 and 12000 characters.", nameof(originalClass));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.RoundTripOriginal = originalClass.Trim();
            session.RoundTripStory = null;
            session.RoundTripStoryModel = null;
            session.RoundTripStoryFallback = false;
            session.RoundTripRecreated = null;
            session.RoundTripRecreatedModel = null;
            session.RoundTripRecreatedFallback = false;
            session.RoundTripQa = null;
            session.RoundTripQaModel = null;
            session.RoundTripError = null;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "gemma-story",
                GemmaModel,
                null,
                Prompts.RoundTripStorySystem,
                $"<source_class>\n{session.RoundTripOriginal}\n</source_class>\nWrite the single JIRA story this class satisfies.",
                60,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.RoundTripStatus = "queued";
        }
    }

    private void CompleteRoundTripStory(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.RoundTripStatus = "failed";
            session.RoundTripError = result.ErrorMessage ?? "The model could not draft the JIRA story.";
            return;
        }

        session.RoundTripStory = result.Content!.Trim();
        session.RoundTripStoryModel = result.ModelUsed;
        session.RoundTripStoryFallback = result.FallbackUsed;

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "gemma-recreate",
            GemmaModel,
            null,
            Prompts.RoundTripRecreateSystem,
            $"<jira_story>\n{session.RoundTripStory}\n</jira_story>\nImplement the single class this story describes. Return only the code.",
            60,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.RoundTripStatus = "recreating";
    }

    private void CompleteRoundTripRecreate(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.RoundTripStatus = "failed";
            session.RoundTripError = result.ErrorMessage ?? "The model could not re-create the class.";
            return;
        }

        session.RoundTripRecreated = StripCodeFence(result.Content!.Trim());
        session.RoundTripRecreatedModel = result.ModelUsed;
        session.RoundTripRecreatedFallback = result.FallbackUsed;

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-roundtrip-qa",
            ClaudeModel,
            null,
            Prompts.RoundTripQaSystem,
            $"<original_class>\n{session.RoundTripOriginal}\n</original_class>\n<recreated_class>\n{session.RoundTripRecreated}\n</recreated_class>\nCompare the behavioral fidelity of the re-created class to the original.",
            90,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.RoundTripStatus = "qa";
    }

    private static void CompleteRoundTripQa(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.RoundTripStatus = "failed";
            session.RoundTripError = result.ErrorMessage ?? "The QA review failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out RoundTripQaDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Summary))
        {
            session.RoundTripStatus = "failed";
            session.RoundTripError = "The QA response did not match the review contract.";
            return;
        }

        session.RoundTripQa = new RoundTripQa(
            ClampScore(dto.Fidelity),
            dto.Summary!.Trim(),
            dto.Preserved ?? [],
            dto.Gaps ?? [],
            dto.Risks ?? [],
            dto.Recommendation?.Trim() ?? string.Empty);
        session.RoundTripQaModel = result.ModelUsed;
        session.RoundTripStatus = "completed";
    }

    private static string StripCodeFence(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLine = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine ? text[(firstLine + 1)..lastFence].Trim() : text;
    }

    public void QueueModernize(string sessionId, string legacyCode)
    {
        if (string.IsNullOrWhiteSpace(legacyCode) || legacyCode.Length > 12000)
        {
            throw new ArgumentException("Provide code between 1 and 12000 characters.", nameof(legacyCode));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.ModernizeLegacy = legacyCode.Trim();
            session.ModernizeResult = null;
            session.ModernizeModel = null;
            session.ModernizeError = null;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "claude-modernize",
                ClaudeModel,
                null,
                Prompts.ModernizeSystem,
                $"<legacy_code>\n{session.ModernizeLegacy}\n</legacy_code>\nModernize this code behavior-preservingly and expose its business logic.",
                90,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.ModernizeStatus = "queued";
        }
    }

    private static void CompleteModernize(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ModernizeStatus = "failed";
            session.ModernizeError = result.ErrorMessage ?? "The modernization task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out ModernizeDto? dto) || dto is null ||
            string.IsNullOrWhiteSpace(dto.ModernizedCode))
        {
            session.ModernizeStatus = "failed";
            session.ModernizeError = "The modernization response did not match the expected contract.";
            return;
        }

        session.ModernizeResult = new ModernizeResult(
            StripCodeFence(dto.ModernizedCode!.Trim()),
            dto.Explanation?.Trim() ?? string.Empty,
            dto.BusinessRules ?? [],
            dto.MicroserviceCandidates ?? []);
        session.ModernizeModel = result.ModelUsed;
        session.ModernizeStatus = "completed";
    }

    public void QueueAudit(string sessionId, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000)
        {
            throw new ArgumentException("Provide code between 1 and 16000 characters.", nameof(code));
        }

        var session = RequireSession(sessionId);
        var report = _auditAnalyzer.Analyze(code.Trim());
        lock (_gate)
        {
            session.AuditCode = code.Trim();
            session.AuditReport = report;
            session.AuditRecommendation = null;
            session.AuditModel = null;
            session.AuditError = null;

            if (report.ParseError)
            {
                session.AuditStatus = "failed";
                session.AuditError = report.ParseMessage ?? "The code could not be analyzed.";
                return;
            }

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "gemma-audit",
                GemmaModel,
                GptFallbackModel,
                Prompts.AuditSystem,
                BuildAuditRequest(report),
                75,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.AuditStatus = "analyzed";
        }
    }

    private static void CompleteAudit(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.AuditStatus = "failed";
            session.AuditError = result.ErrorMessage ?? "The audit review failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out AuditRecommendationDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Summary))
        {
            session.AuditStatus = "failed";
            session.AuditError = "The audit response did not match the review contract.";
            return;
        }

        session.AuditRecommendation = new AuditRecommendation(
            dto.Summary!.Trim(),
            dto.Strengths ?? [],
            dto.Priorities ?? [],
            dto.AgenticReadiness ?? []);
        session.AuditModel = result.ModelUsed;
        session.AuditStatus = "completed";
    }

    private static string BuildAuditRequest(AuditReport report)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<deterministic_audit>");
        request.AppendLine($"Overall agentic-readiness score: {report.OverallScore}/100 (grade {report.Grade}).");
        request.AppendLine($"Summary: {report.Summary}");
        request.AppendLine("Dimension scores (0-100), authoritative — do not change these numbers:");
        foreach (var dimension in report.Dimensions)
        {
            request.AppendLine($"- {dimension.Dimension}: {dimension.Value} ({dimension.Detail})");
        }

        request.AppendLine("Static findings:");
        foreach (var finding in report.Findings)
        {
            request.AppendLine($"- [{finding.Severity}] {finding.Message}");
        }

        request.AppendLine("Type metrics:");
        foreach (var type in report.Types)
        {
            request.AppendLine($"- {type.Kind} {type.Name}: {type.Methods} methods, {type.Fields} fields, {type.PublicMembers} public, {type.Lines} lines, depends on [{string.Join(", ", type.DependsOn)}]");
        }

        request.AppendLine("Most complex methods:");
        foreach (var method in report.Methods)
        {
            request.AppendLine($"- {method.Type}.{method.Method}: complexity {method.Complexity}, {method.Lines} lines, nesting {method.MaxNesting}, {method.Parameters} params");
        }

        request.AppendLine("Type dependency graph (edges):");
        request.AppendLine(report.GraphEdges.Count > 0 ? string.Join("; ", report.GraphEdges) : "no internal type dependencies");
        request.AppendLine("</deterministic_audit>");
        return request.ToString();
    }

    public void QueueContextAudit(string sessionId, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000)
        {
            throw new ArgumentException("Provide code between 1 and 16000 characters.", nameof(code));
        }

        var session = RequireSession(sessionId);
        var initial = _contextAudit.Analyze(code.Trim());
        lock (_gate)
        {
            session.ContextCode = code.Trim();
            session.ContextInitial = initial;
            session.ContextGemmaOverview = null;
            session.ContextGemmaGaps = null;
            session.ContextGemmaModel = null;
            session.ContextJudge = null;
            session.ContextJudgeModel = null;
            session.ContextError = null;
            session.ContextTraps = ContextTraps;

            if (!initial.Positive)
            {
                // Cheap ML gate is negative — do not spend frontier tokens on a deep audit.
                session.ContextStatus = "skipped";
                return;
            }

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "gemma-context",
                GemmaModel,
                GptFallbackModel,
                Prompts.ContextAuditSystem,
                $"{session.ContextCode}\n\nAudit whether the CODE matches its CONTEXT. Report inconsistencies the context lets you detect.",
                75,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.ContextStatus = "analyzed";
        }
    }

    private void CompleteContextGemma(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ContextStatus = "failed";
            session.ContextError = result.ErrorMessage ?? "The Gemma context audit failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out ContextGemmaDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Overview))
        {
            session.ContextStatus = "failed";
            session.ContextError = "The Gemma context audit did not match the expected contract.";
            return;
        }

        session.ContextGemmaOverview = dto.Overview!.Trim();
        session.ContextGemmaGaps = dto.Gaps ?? [];
        session.ContextGemmaModel = result.ModelUsed;

        var judgeTask = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-context-judge",
            ClaudeModel,
            null,
            Prompts.ContextJudgeSystem,
            BuildContextJudgeRequest(session),
            75,
            DateTimeOffset.UtcNow);
        session.Tasks[judgeTask.TaskId] = new TaskState(judgeTask);
        session.ContextStatus = "judging";
    }

    private static void CompleteContextJudge(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ContextStatus = "failed";
            session.ContextError = result.ErrorMessage ?? "The Claude judgement failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out ContextJudgeDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Verdict))
        {
            session.ContextStatus = "failed";
            session.ContextError = "The Claude judgement did not match the expected contract.";
            return;
        }

        var total = session.ContextTraps.Count;
        session.ContextJudge = new ContextJudgeResult(
            Math.Clamp(dto.TrapsFound, 0, total),
            total,
            dto.Matched ?? [],
            dto.Missed ?? [],
            dto.FalsePositives ?? [],
            dto.Verdict!.Trim());
        session.ContextJudgeModel = result.ModelUsed;
        session.ContextStatus = "completed";
    }

    private static string BuildContextJudgeRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<code_with_context>");
        request.AppendLine(session.ContextCode);
        request.AppendLine("</code_with_context>");
        request.AppendLine("<planted_traps>");
        request.AppendLine("These are the known discrepancies between the code and its context (ground truth):");
        for (var index = 0; index < session.ContextTraps.Count; index++)
        {
            request.AppendLine($"{index + 1}. {session.ContextTraps[index]}");
        }

        request.AppendLine("</planted_traps>");
        request.AppendLine("<gemma_findings>");
        request.AppendLine(string.IsNullOrWhiteSpace(session.ContextGemmaOverview) ? "(no overview)" : session.ContextGemmaOverview);
        foreach (var gap in session.ContextGemmaGaps ?? [])
        {
            request.AppendLine($"- {gap}");
        }

        request.AppendLine("</gemma_findings>");
        request.AppendLine("Grade how many planted traps Gemma actually found.");
        return request.ToString();
    }

    public void QueueClaraPoc(string sessionId, string jira)
    {
        if (string.IsNullOrWhiteSpace(jira) || jira.Length > 8000)
        {
            throw new ArgumentException("Provide a requirement between 1 and 8000 characters.", nameof(jira));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.ClaraJira = jira.Trim();
            session.ClaraSource = null;
            session.ClaraAuthorModel = null;
            session.ClaraResult = null;
            session.ClaraReview = null;
            session.ClaraReviewModel = null;
            session.ClaraError = null;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "claude-clara-author",
                ClaraAuthorModel,
                null,
                Prompts.ClaraAuthorSystem,
                $"<requirement>\n{session.ClaraJira}\n</requirement>\nTranslate this requirement into a single CLARA policy. Return only CLARA source.",
                90,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.ClaraStatus = "authoring";
        }
    }

    private void CompleteClaraAuthor(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ClaraStatus = "failed";
            session.ClaraError = result.ErrorMessage ?? "The CLARA author task failed.";
            return;
        }

        var source = StripCodeFence(result.Content!.Trim());
        session.ClaraSource = source;
        session.ClaraAuthorModel = result.ModelUsed;
        session.ClaraResult = ClaraCompiler.Run(source);

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-clara-review",
            ClaudeModel,
            null,
            Prompts.ClaraReviewSystem,
            BuildClaraReviewRequest(session),
            90,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.ClaraStatus = "reviewing";
    }

    private static void CompleteClaraReview(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ClaraStatus = "failed";
            session.ClaraError = result.ErrorMessage ?? "The CLARA review task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out ClaraReviewDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Verdict))
        {
            session.ClaraStatus = "failed";
            session.ClaraError = "The CLARA review did not match the expected contract.";
            return;
        }

        session.ClaraReview = new ClaraReview(
            Math.Clamp(dto.Fidelity, 1, 5),
            dto.Verdict!.Trim(),
            dto.Strengths ?? [],
            dto.Issues ?? [],
            dto.LanguageNotes ?? []);
        session.ClaraReviewModel = result.ModelUsed;
        session.ClaraStatus = "completed";
    }

    private static string BuildClaraReviewRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<requirement>");
        request.AppendLine(session.ClaraJira);
        request.AppendLine("</requirement>");
        request.AppendLine("<clara_source>");
        request.AppendLine(session.ClaraSource);
        request.AppendLine("</clara_source>");
        request.AppendLine("<execution>");
        var result = session.ClaraResult;
        if (result is null)
        {
            request.AppendLine("not executed");
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                request.AppendLine($"{(diagnostic.Severity == ClaraSeverity.Error ? "ERROR" : "WARNING")} {diagnostic.Format()}");
            }

            if (!result.Compiled)
            {
                request.AppendLine("The policy did not compile, so no examples were executed.");
            }
            else
            {
                foreach (var example in result.Examples)
                {
                    var status = example.Passed switch { true => "PASS", false => "FAIL", _ => "n/a" };
                    request.AppendLine($"[{status}] {example.Description}: {string.Join(", ", example.Inputs)} => {string.Join(", ", example.Outputs)} (rule: {example.MatchedRule ?? "none"}){(example.Note is null ? "" : " — " + example.Note)}");
                }
            }
        }

        request.AppendLine("</execution>");
        request.AppendLine("Review whether the CLARA policy faithfully and unambiguously encodes the requirement, and assess CLARA as an LLM-legible business-logic language.");
        return request.ToString();
    }

    // Claude designs from the audit; Gemma implements from the design. The corpus is deliberately never
    // sent to the build stage — that separation is the whole point of the two-model split.
    public void QueueConsolidation(string sessionId, CommonCodeReport report)
    {
        if (!report.CorpusAvailable || report.DuplicatedClusters == 0)
        {
            throw new InvalidOperationException("Run the audit and find at least one duplicated cluster before requesting a design.");
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.ConsolidationDesign = null;
            session.ConsolidationDesignModel = null;
            session.ConsolidationBuildModel = null;
            session.ConsolidationFiles.Clear();
            session.ConsolidationAssumptions.Clear();
            session.ConsolidationFailedModules.Clear();
            session.ConsolidationTaskModule.Clear();
            session.ConsolidationModulesTotal = 0;
            session.ConsolidationError = null;
            session.ConsolidationBuildPromptTokens = 0;
            session.ConsolidationBuildOutputTokens = 0;

            var request = BuildConsolidationDesignRequest(report);
            var (promptTokens, exact) = _encoder.CountTokens(Prompts.ConsolidationDesignSystem + request);
            var (corpusTokens, _) = _encoder.CountTokens(string.Join("\n", _tradingCorpus.Files.Select(file => file.Source)));
            session.ConsolidationDesignPromptTokens = promptTokens;
            session.ConsolidationCorpusTokens = corpusTokens;
            session.ConsolidationTokensExact = exact;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "claude-consolidation-design",
                ClaudeModel,
                null,
                Prompts.ConsolidationDesignSystem,
                request,
                150,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.ConsolidationStatus = "designing";
        }
    }

    private static string BuildConsolidationDesignRequest(CommonCodeReport report)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<audit method=\"embeddinggemma-300m cosine clustering\" threshold=\"" + report.Threshold.ToString("0.00") + "\">");
        request.AppendLine($"Corpus: {report.UnitCount} source files across {report.Repos.Count} production services.");
        foreach (var repo in report.Repos)
        {
            request.AppendLine($"- {repo.Name} ({repo.AssetClass}): {repo.Files} files, {repo.Lines} lines. Divergent logic: {repo.Divergence}.");
        }

        request.AppendLine($"Cross-repo similarity: known duplicates mean {report.Separation.DuplicateMean:0.000} (weakest {report.Separation.DuplicateMin:0.000}), everything else mean {report.Separation.UnrelatedMean:0.000} (strongest {report.Separation.UnrelatedMax:0.000}).");
        request.AppendLine($"Consolidation prize: {report.NonFunctionalPrize} duplicate non-functional lines, {report.FunctionalPrize} duplicate functional lines.");
        request.AppendLine("</audit>");
        request.AppendLine("<clusters>");
        foreach (var cluster in report.Clusters.Where(cluster => cluster.IsDuplication))
        {
            request.AppendLine($"- [{cluster.Kind}] {cluster.Name}: {string.Join(", ", cluster.Members.Select(member => member.Repo + "/" + member.Name + " (" + member.Lines + " lines)"))}. Duplicate lines: {cluster.DuplicateLines}.");
        }

        request.AppendLine("</clusters>");
        request.AppendLine("Design the shared platform library that retires this duplication. The implementation_spec you produce is the ONLY input a smaller model will receive — it will not see this audit or the source repositories, so it must be self-contained.");
        return request.ToString();
    }

    // --- Making Gemma an SME: Claude tries unaided, then consults a grounded Gemma as a subagent ---
    //
    // Stage 1 is deliberately ungrounded. Without a measured baseline there is nothing to attribute the
    // improvement to, and "the grounding helped" becomes an assertion rather than a result.
    public void QueueCryptoSme(string sessionId)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.SmeUnaided = null;
            session.SmeConsultation = null;
            session.SmeDesign = null;
            session.SmeError = null;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "claude-crypto-unaided",
                ClaudeModel,
                ClaraAuthorModel,
                Prompts.CryptoUnaidedSystem,
                $"<ticket>\n{CryptoSmeCorpus.Jira}\n</ticket>\nYou have no reference documentation. Answer from your own knowledge.",
                180,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.SmeStatus = "unaided";
        }
    }

    private void CompleteCryptoUnaided(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out SmeUnaidedDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Attempt))
        {
            session.SmeStatus = "failed";
            session.SmeError = result.ErrorMessage ?? "The unaided attempt did not match the expected contract.";
            return;
        }

        // Score the attempt only. Including the "uncertain" list would credit the model for naming a fact
        // it has just said it cannot recall, which is the opposite of recall.
        var (score, hit, missed) = CryptoSmeCorpus.ScoreFacts(dto.Attempt);
        session.SmeUnaided = new SmeUnaided(
            dto.Attempt!.Trim(),
            dto.Uncertain ?? [],
            dto.Questions ?? [],
            result.ModelUsed,
            score,
            hit,
            missed);

        var questions = session.SmeUnaided.Questions.Take(8).ToList();
        if (questions.Count == 0)
        {
            session.SmeStatus = "failed";
            session.SmeError = "The unaided attempt asked no questions, so there is nothing to consult the SME about.";
            return;
        }

        var pack = CryptoSmeCorpus.GroundingPack();
        session.SmePackTokens = _encoder.CountTokens(pack).Tokens;

        var request = new System.Text.StringBuilder();
        request.AppendLine("<grounding_pack>");
        request.AppendLine(pack);
        request.AppendLine("</grounding_pack>");
        request.AppendLine("<questions>");
        foreach (var question in questions)
        {
            request.AppendLine($"- {question}");
        }

        request.AppendLine("</questions>");
        request.AppendLine($"<peer_attempt model=\"{result.ModelUsed}\">\n{session.SmeUnaided.Attempt}\n</peer_attempt>");
        request.AppendLine("Answer each question from the pack, and correct anything in the peer attempt the pack contradicts.");

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "gemma-crypto-sme",
            GemmaModel,
            GptFallbackModel,
            Prompts.CryptoSmeSystem,
            request.ToString(),
            240,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.SmeStatus = "consulting";
    }

    private static void CompleteCryptoSme(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out SmeConsultDto? dto) || dto is null || (dto.Answers ?? []).Count == 0)
        {
            session.SmeStatus = "failed";
            session.SmeError = result.ErrorMessage ?? "The SME consultation did not match the expected contract.";
            return;
        }

        session.SmeConsultation = new SmeConsultation(
            dto.Answers!.Select(answer => new SmeAnswer(
                answer.Question ?? "?",
                answer.Answer ?? string.Empty,
                answer.Citation ?? "not cited",
                answer.InPack)).ToList(),
            dto.Corrections ?? [],
            dto.Unprompted ?? [],
            result.ModelUsed,
            session.SmePackTokens);

        var request = new System.Text.StringBuilder();
        request.AppendLine($"<ticket>\n{CryptoSmeCorpus.Jira}\n</ticket>");
        request.AppendLine($"<your_earlier_attempt>\n{session.SmeUnaided?.Attempt}\n</your_earlier_attempt>");
        request.AppendLine($"<sme_subagent model=\"{result.ModelUsed}\" note=\"grounded on vendor documentation; still a model, not the documentation itself\">");
        foreach (var answer in session.SmeConsultation.Answers)
        {
            request.AppendLine($"Q: {answer.Question}");
            request.AppendLine($"A: {answer.Answer}");
            request.AppendLine($"Cited: {answer.Citation}{(answer.InPack ? "" : " (NOT FOUND IN PACK)")}");
        }

        if (session.SmeConsultation.Corrections.Count > 0)
        {
            request.AppendLine("Corrections to your earlier attempt:");
            foreach (var correction in session.SmeConsultation.Corrections)
            {
                request.AppendLine($"- {correction}");
            }
        }

        if (session.SmeConsultation.Unprompted.Count > 0)
        {
            request.AppendLine("Material facts from the pack that you did NOT ask about:");
            foreach (var extra in session.SmeConsultation.Unprompted)
            {
                request.AppendLine($"- {extra}");
            }
        }

        request.AppendLine("</sme_subagent>");
        request.AppendLine("Produce the execution adapter design, tagging every step with its source.");

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-crypto-design",
            ClaudeModel,
            ClaraAuthorModel,
            Prompts.CryptoDesignSystem,
            request.ToString(),
            240,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.SmeStatus = "designing";
    }

    private static void CompleteCryptoDesign(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        session.SmeStatus = "completed";
        if (!succeeded || !TryDeserialize(result.Content!, out SmeDesignDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Summary))
        {
            session.SmeError = result.ErrorMessage ?? "The design did not match the expected contract.";
            return;
        }

        var steps = (dto.Steps ?? []).Select(step => new SmeDesignStep(
            step.Step ?? "step",
            step.Detail ?? string.Empty,
            step.Source is "own" or "sme" or "unverified" ? step.Source : "unverified")).ToList();

        var scored = dto.Summary + " " + string.Join(" ", steps.Select(step => step.Step + " " + step.Detail)) +
                     " " + string.Join(" ", dto.Risks ?? []);
        var (score, hit, missed) = CryptoSmeCorpus.ScoreFacts(scored);

        session.SmeDesign = new SmeDesign(
            dto.Summary!.Trim(),
            steps,
            dto.Risks ?? [],
            dto.OpenQuestions ?? [],
            result.ModelUsed,
            score,
            hit,
            missed);
    }

    // --- Shared prompt library: execute the contributed prompt, then review what it produced ---
    //
    // A curation gate proves the paperwork is complete. It does not prove the prompt works. Running the
    // contribution against a known class and reviewing the output is the part that produces evidence.
    public void QueuePatternRun(string sessionId, string prompt, string sourceClass, string changeRequest)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("The pattern has no prompt to run.", nameof(prompt));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.PatternModifiedClass = null;
            session.PatternGemmaModel = null;
            session.PatternReview = null;
            session.PatternError = null;
            session.PatternPrompt = prompt;
            session.PatternSource = sourceClass;
            session.PatternRequest = changeRequest;

            var request = $"<change_request>\n{changeRequest}\n</change_request>\n\n<class>\n{sourceClass}\n</class>";
            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "gemma-pattern-apply",
                GemmaModel,
                GptFallbackModel,
                prompt,
                request,
                180,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.PatternStatus = "applying";
        }
    }

    private static void CompletePatternApply(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            session.PatternStatus = "failed";
            session.PatternError = result.ErrorMessage ?? "The pattern produced no output.";
            return;
        }

        session.PatternModifiedClass = result.Content.Trim();
        session.PatternGemmaModel = result.ModelUsed;

        var request = $"<shared_prompt>\n{session.PatternPrompt}\n</shared_prompt>\n\n" +
                      $"<change_request>\n{session.PatternRequest}\n</change_request>\n\n" +
                      $"<original_class>\n{session.PatternSource}\n</original_class>\n\n" +
                      $"<model_output model=\"{result.ModelUsed}\">\n{session.PatternModifiedClass}\n</model_output>\n" +
                      "Judge whether the shared prompt did its job.";

        var review = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-pattern-review",
            ClaraAuthorModel,
            ClaudeModel,
            Prompts.PatternReviewSystem,
            request,
            180,
            DateTimeOffset.UtcNow);
        session.Tasks[review.TaskId] = new TaskState(review);
        session.PatternStatus = "reviewing";
    }

    private static void CompletePatternReview(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        session.PatternStatus = "completed";
        if (!succeeded || !TryDeserialize(result.Content!, out PatternReviewDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Verdict))
        {
            session.PatternError = result.ErrorMessage ?? "The review did not match the expected contract. The output above still stands on its own.";
            return;
        }

        session.PatternReview = new PatternApplyReview(
            Math.Clamp(dto.Fidelity, 0, 5),
            dto.Verdict!.Trim(),
            dto.Followed ?? [],
            dto.Ignored ?? [],
            dto.Risks ?? [],
            dto.PromptRecommendation?.Trim() ?? string.Empty,
            result.ModelUsed);
    }

    // --- Regression guidance: Gemma proposes the missing tests, Claude checks whether they would work ---
    //
    // The mutation report is executed evidence. Gemma is asked to close specific named survivors, not to
    // "improve the tests", because a survivor is a falsifiable target and a vague instruction is not.
    public void QueueRegressionGuidance(string sessionId, MutationReport report, string source, string tests)
    {
        if (!report.Ran)
        {
            throw new InvalidOperationException("Run the mutation test before asking for guidance.");
        }

        var survivors = report.Mutants.Where(mutant => !mutant.Killed).ToList();
        if (survivors.Count == 0)
        {
            throw new InvalidOperationException("Every mutant was killed, so there is nothing to remediate.");
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.GuidanceRemediation = null;
            session.GuidanceReview = null;
            session.GuidanceError = null;
            session.GuidanceSource = source;
            session.GuidanceTests = tests;
            session.GuidanceReport = report;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "gemma-regression-guidance",
                GemmaModel,
                GptFallbackModel,
                Prompts.RegressionGuidanceSystem,
                BuildGuidanceRequest(report, source, tests),
                180,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.GuidanceStatus = "advising";
        }
    }

    private static string BuildGuidanceRequest(MutationReport report, string source, string tests)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<mutation_report>");
        request.AppendLine($"Mutation score: {report.MutationScore}% ({report.KilledMutants} of {report.TotalMutants} injected defects caught).");
        request.AppendLine($"Baseline tests, all passing: {string.Join(", ", report.BaselineTests)}");
        request.AppendLine("Surviving mutants — each is a real behaviour change the suite did not notice:");
        foreach (var survivor in report.Mutants.Where(mutant => !mutant.Killed))
        {
            request.AppendLine($"- line {survivor.Line}: {survivor.Description}. `{survivor.Original}` was replaced with `{survivor.Mutated}` and every test still passed.");
        }

        request.AppendLine("Mutants that were caught (do not propose tests for these):");
        foreach (var killed in report.Mutants.Where(mutant => mutant.Killed))
        {
            request.AppendLine($"- line {killed.Line}: {killed.Description} — caught by {killed.Detail}");
        }

        request.AppendLine("</mutation_report>");
        request.AppendLine("<code_under_test>");
        request.AppendLine(source);
        request.AppendLine("</code_under_test>");
        request.AppendLine("<existing_tests>");
        request.AppendLine(tests);
        request.AppendLine("</existing_tests>");
        request.AppendLine("Propose the tests that would close these survivors.");
        return request.ToString();
    }

    private void CompleteRegressionGuidance(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out GuidanceDto? dto) || dto is null || (dto.Tests ?? []).Count == 0)
        {
            session.GuidanceStatus = "failed";
            session.GuidanceError = result.ErrorMessage ?? "The guidance did not match the expected contract.";
            return;
        }

        session.GuidanceRemediation = new MutationRemediation(
            dto.Summary?.Trim() ?? string.Empty,
            dto.Tests!.Select(test => new ProposedTest(
                test.Name ?? "Test",
                test.TargetsSurvivor ?? "unstated",
                test.Behaviour ?? string.Empty,
                test.Assertion ?? string.Empty)).ToList(),
            dto.NotWorthTesting ?? [],
            result.ModelUsed);

        var request = new System.Text.StringBuilder();
        request.AppendLine("<mutation_report>");
        foreach (var survivor in session.GuidanceReport!.Mutants.Where(mutant => !mutant.Killed))
        {
            request.AppendLine($"- line {survivor.Line}: {survivor.Description}. `{survivor.Original}` -> `{survivor.Mutated}` survived.");
        }

        request.AppendLine("</mutation_report>");
        request.AppendLine("<code_under_test>");
        request.AppendLine(session.GuidanceSource);
        request.AppendLine("</code_under_test>");
        request.AppendLine($"<proposed_by model=\"{result.ModelUsed}\">");
        request.AppendLine(session.GuidanceRemediation.Summary);
        foreach (var test in session.GuidanceRemediation.Tests)
        {
            request.AppendLine($"- {test.Name} — targets: {test.TargetsSurvivor}; behaviour: {test.Behaviour}; assertion: {test.Assertion}");
        }

        request.AppendLine("</proposed_by>");
        request.AppendLine("For each proposed test, decide whether it would actually kill the survivor it names.");

        var review = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-regression-review",
            ClaraAuthorModel,
            ClaudeModel,
            Prompts.RegressionReviewSystem,
            request.ToString(),
            180,
            DateTimeOffset.UtcNow);
        session.Tasks[review.TaskId] = new TaskState(review);
        session.GuidanceStatus = "reviewing";
    }

    private static void CompleteRegressionReview(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        session.GuidanceStatus = "completed";
        if (!succeeded || !TryDeserialize(result.Content!, out GuidanceReviewDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Verdict))
        {
            session.GuidanceError = result.ErrorMessage ?? "The review did not match the expected contract. Gemma's proposals above still stand on their own merits.";
            return;
        }

        session.GuidanceReview = new MutationReview(
            dto.Verdict!.Trim(),
            (dto.Assessments ?? []).Select(assessment => new TestAssessment(
                assessment.Name ?? "?",
                assessment.WouldKill,
                assessment.Reasoning ?? string.Empty)).ToList(),
            dto.Gaps ?? [],
            dto.Overreach ?? [],
            dto.NextStep?.Trim() ?? string.Empty,
            result.ModelUsed);
    }

    // --- Portfolio context tiers: discover on Tier 1, scope on Tier 2, design on Tier 3 ---
    //
    // Each stage receives only the tier it is meant to reason from. Handing all three at once would make
    // the funnel unfalsifiable: you could not tell which tier supplied which conclusion.
    public void QueuePortfolioTiers(string sessionId)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.TierDiscovery = null;
            session.TierScoping = null;
            session.TierDesigns.Clear();
            session.TierDesignTasks.Clear();
            session.TierModel = null;
            session.TierError = null;

            var request = $"<design_brief>\n{PortfolioTierSamples.DesignBrief}\n</design_brief>\n\n{PortfolioTierSamples.Tier1Context()}\nIdentify every service that the brief could require a change in. You have Tier 1 only.";
            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "claude-tier1-discover",
                ClaraAuthorModel,
                ClaudeModel,
                Prompts.Tier1DiscoverSystem,
                request,
                150,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.TierStatus = "tier1";
        }
    }

    private void CompleteTier1(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out Tier1Dto? dto) || dto is null || (dto.Candidates ?? []).Count == 0)
        {
            session.TierStatus = "failed";
            session.TierError = result.ErrorMessage ?? "Tier 1 discovery did not match the expected contract.";
            return;
        }

        session.TierModel = result.ModelUsed;
        session.TierDiscovery = new Tier1Discovery(
            dto.Candidates!.Select(candidate => new TierCandidate(candidate.Service ?? "?", candidate.AmpId ?? "?", candidate.Why ?? string.Empty)).ToList(),
            dto.Excluded ?? [],
            dto.CannotDetermineYet ?? [],
            dto.Summary?.Trim() ?? string.Empty);

        var ampIds = session.TierDiscovery.Candidates.Select(candidate => candidate.AmpId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var request = new System.Text.StringBuilder();
        request.AppendLine($"<design_brief>\n{PortfolioTierSamples.DesignBrief}\n</design_brief>");
        request.AppendLine("<tier1_candidates>");
        foreach (var candidate in session.TierDiscovery.Candidates)
        {
            request.AppendLine($"- {candidate.Service} [{candidate.AmpId}] — {candidate.Why}");
        }

        request.AppendLine("</tier1_candidates>");
        request.AppendLine(PortfolioTierSamples.Tier2Context(ampIds));
        request.AppendLine("Decide which candidates are genuinely in scope. You now have Tier 2 ownership bounds.");

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-tier2-scope",
            ClaraAuthorModel,
            ClaudeModel,
            Prompts.Tier2ScopeSystem,
            request.ToString(),
            150,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.TierStatus = "tier2";
    }

    private void CompleteTier2(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out Tier2Dto? dto) || dto is null || (dto.Decisions ?? []).Count == 0)
        {
            session.TierStatus = "failed";
            session.TierError = result.ErrorMessage ?? "Tier 2 scoping did not match the expected contract.";
            return;
        }

        session.TierScoping = new Tier2Scoping(
            dto.Decisions!.Select(decision => new TierScopeDecision(
                decision.Service ?? "?",
                decision.InScope,
                decision.Reason ?? string.Empty,
                decision.Evidence ?? string.Empty)).ToList(),
            dto.SharedLibraryVerdict?.Trim() ?? string.Empty,
            dto.Summary?.Trim() ?? string.Empty);

        var inScope = session.TierScoping.Decisions
            .Where(decision => decision.InScope)
            .Select(decision => (decision.Service, Repository: PortfolioTierSamples.ServiceToRepository.GetValueOrDefault(decision.Service)))
            .Where(pair => pair.Repository is not null)
            .ToList();

        if (inScope.Count == 0)
        {
            session.TierStatus = "completed";
            session.TierError = "No in-scope service mapped to a repository, so there is no Tier 3 design to produce.";
            return;
        }

        // One task per in-scope repository: Tier 3 is atomic per repo, so the design should be too.
        foreach (var (service, repository) in inScope)
        {
            var request = $"<design_brief>\n{PortfolioTierSamples.DesignBrief}\n</design_brief>\n\n" +
                          $"<scope_decision>\n{service} is in scope. {session.TierScoping.Decisions.First(decision => decision.Service == service).Reason}\n</scope_decision>\n\n" +
                          _tradingCorpus.Tier3Context(repository!) +
                          "\nProduce the change design for THIS repository only, using its own OKF bundle and pipeline contract.";

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                session.SessionId,
                "claude-tier3-design",
                ClaraAuthorModel,
                ClaudeModel,
                Prompts.Tier3DesignSystem,
                request,
                240,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.TierDesignTasks[task.TaskId] = (service, repository!);
        }

        session.TierStatus = "tier3";
    }

    private static void CompleteTier3(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!session.TierDesignTasks.TryGetValue(result.TaskId, out var target))
        {
            return;
        }

        if (succeeded && TryDeserialize(result.Content!, out Tier3Dto? dto) && dto is not null && !string.IsNullOrWhiteSpace(dto.Summary))
        {
            session.TierDesigns.Add(new ServiceDesign(
                target.Service,
                target.Repository,
                dto.Summary!.Trim(),
                (dto.Changes ?? []).Select(change => new ServiceChange(change.File ?? "?", change.Change ?? string.Empty, change.Justification ?? string.Empty)).ToList(),
                dto.Unchanged ?? [],
                dto.Risks ?? [],
                dto.OpenQuestions ?? []));
        }
        else
        {
            session.TierDesigns.Add(new ServiceDesign(
                target.Service,
                target.Repository,
                $"Design failed: {result.ErrorMessage ?? "the response did not match the expected contract."}",
                [], [], [], []));
        }

        if (session.TierDesigns.Count >= session.TierDesignTasks.Count)
        {
            session.TierStatus = "completed";
        }
    }

    private void CompleteConsolidationDesign(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ConsolidationStatus = "failed";
            session.ConsolidationError = result.ErrorMessage ?? "The consolidation design task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out ConsolidationDesignDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.ImplementationSpec))
        {
            session.ConsolidationStatus = "failed";
            var preview = result.Content is null ? "(empty)" : result.Content.Trim();
            session.ConsolidationError = "The design did not match the expected contract. The model returned: "
                + (preview.Length > 600 ? preview[..600] + "…" : preview);
            return;
        }

        session.ConsolidationDesign = new ConsolidationDesign(
            dto.LibraryName?.Trim() ?? "SharedPlatform",
            dto.Summary?.Trim() ?? string.Empty,
            (dto.Modules ?? []).Select(module => new ConsolidationModule(
                module.Name ?? "module",
                module.Responsibility ?? string.Empty,
                module.PublicApi ?? string.Empty,
                module.Replaces ?? [])).ToList(),
            dto.MigrationSteps ?? [],
            dto.Risks ?? [],
            dto.OutOfScope ?? [],
            dto.ImplementationSpec!.Trim());
        session.ConsolidationDesignModel = result.ModelUsed;
        session.ConsolidationDesignOutputTokens = _encoder.CountTokens(result.Content!).Tokens;

        var modules = session.ConsolidationDesign.Modules.Count > 0
            ? session.ConsolidationDesign.Modules
            : [new ConsolidationModule(session.ConsolidationDesign.LibraryName, "The whole library.", string.Empty, [])];
        session.ConsolidationModulesTotal = modules.Count;

        // One task per module. Asking for the whole library in a single response overruns the output cap
        // and truncates mid-file, which is what a JSON contract turns into an unparseable failure.
        foreach (var module in modules)
        {
            var buildRequest = BuildConsolidationModuleRequest(session.ConsolidationDesign, module);
            session.ConsolidationBuildPromptTokens += _encoder.CountTokens(Prompts.ConsolidationBuildSystem + buildRequest).Tokens;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                session.SessionId,
                "gemma-consolidation-build",
                GemmaModel,
                GptFallbackModel,
                Prompts.ConsolidationBuildSystem,
                buildRequest,
                240,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.ConsolidationTaskModule[task.TaskId] = module.Name;
        }

        session.ConsolidationStatus = "building";
    }

    private static string BuildConsolidationModuleRequest(ConsolidationDesign design, ConsolidationModule module)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<implementation_spec>");
        request.AppendLine(design.ImplementationSpec);
        request.AppendLine("</implementation_spec>");
        request.AppendLine($"<module name=\"{module.Name}\">");
        request.AppendLine(module.Responsibility);
        if (!string.IsNullOrWhiteSpace(module.PublicApi))
        {
            request.AppendLine("Public API: " + module.PublicApi);
        }

        request.AppendLine("</module>");
        request.AppendLine($"Implement ONLY the module '{module.Name}' from the specification above, in library '{design.LibraryName}'. Output one file.");
        return request.ToString();
    }

    private void CompleteConsolidationBuild(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        var module = session.ConsolidationTaskModule.TryGetValue(result.TaskId, out var name) ? name : "module";
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            session.ConsolidationFailedModules.Add($"{module}: {result.ErrorMessage ?? "no output"}");
        }
        else
        {
            session.ConsolidationBuildModel = result.ModelUsed;
            session.ConsolidationBuildOutputTokens += _encoder.CountTokens(result.Content).Tokens;
            var (path, source, assumptions) = ParseModuleFile(result.Content, module);
            if (string.IsNullOrWhiteSpace(source))
            {
                session.ConsolidationFailedModules.Add($"{module}: the response contained no code.");
            }
            else
            {
                session.ConsolidationFiles.Add(new ConsolidationFile(path, module, source));
                session.ConsolidationAssumptions.AddRange(assumptions.Select(assumption => $"{module}: {assumption}"));
            }
        }

        if (session.ConsolidationFiles.Count + session.ConsolidationFailedModules.Count >= session.ConsolidationModulesTotal)
        {
            session.ConsolidationStatus = session.ConsolidationFiles.Count > 0 ? "completed" : "failed";
            if (session.ConsolidationFiles.Count == 0)
            {
                session.ConsolidationError = "Every module failed to build. " + string.Join(" | ", session.ConsolidationFailedModules);
            }
        }
    }

    // Plain text, not JSON: embedding a whole source file in a JSON string is where small models break.
    private static (string Path, string Source, List<string> Assumptions) ParseModuleFile(string content, string module)
    {
        var assumptions = new List<string>();
        var path = module + ".cs";
        var body = new System.Text.StringBuilder();

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

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("// FILE:", StringComparison.OrdinalIgnoreCase))
            {
                path = trimmed["// FILE:".Length..].Trim();
            }
            else if (trimmed.StartsWith("// ASSUMPTION:", StringComparison.OrdinalIgnoreCase))
            {
                assumptions.Add(trimmed["// ASSUMPTION:".Length..].Trim());
            }
            else
            {
                body.AppendLine(line.TrimEnd());
            }
        }

        return (path, body.ToString().Trim(), assumptions);
    }

    public void QueueLlmSupport(string sessionId, string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement) || requirement.Length > 8000)
        {
            throw new ArgumentException("Provide a requirement between 1 and 8000 characters.", nameof(requirement));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.SupportRequirement = requirement.Trim();
            session.SupportAdvice = null;
            session.SupportAdviceModel = null;
            session.SupportSynthesis = null;
            session.SupportSynthesisModel = null;
            session.SupportError = null;

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "gemini-gcp-advisor",
                GeminiAdvisorModel,
                null,
                Prompts.GeminiAdvisorSystem,
                $"<requirement>\n{session.SupportRequirement}\n</requirement>\nSurface the Google Cloud services, constraints, and authoritative documentation links this design depends on.",
                90,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.SupportStatus = "advising";
        }
    }

    private static void CompleteGeminiAdvice(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.SupportStatus = "failed";
            session.SupportError = result.ErrorMessage ?? "The Gemini advisory task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out GeminiAdviceDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Overview))
        {
            session.SupportStatus = "failed";
            session.SupportError = "The Gemini advisory did not match the expected contract.";
            return;
        }

        session.SupportAdvice = new GeminiAdvice(
            dto.Overview!.Trim(),
            dto.Services ?? [],
            dto.Constraints ?? [],
            dto.Citations ?? []);
        session.SupportAdviceModel = result.ModelUsed;

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-gcp-synthesis",
            ClaudeModel,
            null,
            Prompts.ClaudeSynthesisSystem,
            BuildSupportSynthesisRequest(session),
            120,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.SupportStatus = "synthesizing";
    }

    private static void CompleteSupportSynthesis(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.SupportStatus = "failed";
            session.SupportError = result.ErrorMessage ?? "The synthesis task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out SupportSynthesisDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.DesignSummary))
        {
            session.SupportStatus = "failed";
            session.SupportError = "The synthesis did not match the expected contract.";
            return;
        }

        session.SupportSynthesis = new SupportSynthesis(
            dto.AlreadyKnew ?? [],
            dto.LearnedFromGemini ?? [],
            dto.NeedsVerification ?? [],
            dto.DesignSummary!.Trim(),
            (dto.DesignSteps ?? []).Select(step => new SupportDesignStep(
                step.Step ?? "step",
                step.Detail ?? string.Empty,
                step.Source is "own" or "gemini" or "unverified" ? step.Source : "unverified")).ToList(),
            dto.ProvenanceNote?.Trim() ?? string.Empty);
        session.SupportSynthesisModel = result.ModelUsed;
        session.SupportStatus = "completed";
    }

    private static string BuildSupportSynthesisRequest(SessionState session)
    {
        var advice = session.SupportAdvice;
        var request = new System.Text.StringBuilder();
        request.AppendLine("<requirement>");
        request.AppendLine(session.SupportRequirement);
        request.AppendLine("</requirement>");
        request.AppendLine($"<advisory model=\"{session.SupportAdviceModel}\" status=\"unverified model output, not ground truth\">");
        request.AppendLine(advice?.Overview);
        request.AppendLine("Services: " + string.Join(", ", advice?.Services ?? []));
        request.AppendLine("Constraints: " + string.Join(" | ", advice?.Constraints ?? []));
        request.AppendLine("Cited documentation: " + string.Join(" | ", advice?.Citations ?? []));
        request.AppendLine("</advisory>");
        request.AppendLine("Separate what you already knew from what this advisory taught you, flag what you cannot accept without checking the primary documentation, then produce the migration design.");
        return request.ToString();
    }

    // --- Context sufficiency: identical ticket and code, three documentation tiers, deterministic scoring ---

    private const int CurveRunsPerTier = 2;

    public void QueueContextCurve(string sessionId)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.CurveRuns.Clear();
            session.CurveTaskKey.Clear();
            session.CurveRungTokens.Clear();
            session.CurveAnalysis = null;
            session.CurveVerdict = null;
            session.CurveJudgeModel = null;
            session.CurveJudgeTaskId = null;
            session.CurveError = null;
            session.CurveStatus = "running";

            foreach (var tier in ContextTierSamples.Tiers)
            {
                var userPrompt = ContextTierSamples.BuildRequest(tier);
                session.CurveRungTokens[tier.Index] = _encoder.CountTokens(Prompts.ContextTierSystem + "\n" + userPrompt).Tokens;
                session.CurveTokensExact = _encoder.IsAvailable;

                for (var attempt = 1; attempt <= CurveRunsPerTier; attempt++)
                {
                    var task = new BridgeTask(
                        Guid.NewGuid().ToString("N"),
                        sessionId,
                        "context-curve-plan",
                        GemmaModel,
                        GptFallbackModel,
                        Prompts.ContextTierSystem,
                        userPrompt,
                        180,
                        DateTimeOffset.UtcNow);
                    session.CurveTaskKey[task.TaskId] = (tier.Index, attempt);
                    session.CurveRuns[(tier.Index, attempt)] = new CurveRunState(tier.Index, attempt);
                    session.Tasks[task.TaskId] = new TaskState(task);
                }
            }
        }
    }

    private void CompleteCurveRun(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!session.CurveTaskKey.TryGetValue(result.TaskId, out var key) ||
            !session.CurveRuns.TryGetValue(key, out var run))
        {
            return;
        }

        run.ModelUsed = result.ModelUsed;
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            run.Status = "failed";
            run.Error = result.ErrorMessage ?? "The model returned no plan.";
        }
        else
        {
            run.Status = "completed";
            run.Plan = result.Content.Trim();
            var (score, met, missed) = ContextTierScorer.Score(run.Plan);
            run.Score = score;
            run.Met = met;
            run.Missed = missed;
        }

        if (session.CurveRuns.Values.All(candidate => candidate.Status is "completed" or "failed"))
        {
            var tiers = BuildCurveTiers(session);
            session.CurveAnalysis = ContextTierScorer.Analyse(tiers);
            QueueCurveJudge(session, tiers);
        }
    }

    // The deterministic score is already computed. The judge is asked for the reasoning a reader needs
    // to trust it, and is explicitly told not to restate the numbers as its own finding.
    private void QueueCurveJudge(SessionState session, IReadOnlyList<ContextTierResult> tiers)
    {
        var representative = tiers
            .Select(tier => (Tier: tier, Run: tier.Runs.FirstOrDefault(run => run.Status == "completed" && run.Score == tier.MedianScore)
                                          ?? tier.Runs.FirstOrDefault(run => run.Status == "completed")))
            .Where(pair => pair.Run is not null)
            .ToList();

        if (representative.Count < 2)
        {
            session.CurveStatus = "completed";
            session.CurveError = "Too few tiers produced a plan to compare.";
            return;
        }

        var request = new System.Text.StringBuilder();
        request.AppendLine("<ticket>");
        request.AppendLine(ContextTierSamples.Ticket);
        request.AppendLine("</ticket>");
        request.AppendLine("<ground_truth>");
        request.AppendLine("The defect is in RegTMarginCalculator.OptionMargin (the `risk` stage). The short-naked branch computes the out-of-the-money amount as max(0, strike - underlying), which is the CALL formula, and uses 10% of the UNDERLYING as the alternative minimum for both rights.");
        request.AppendLine("A correct plan must: branch on the option right; for puts use OTM = max(0, underlying - strike) and an alternative minimum of 10% of the STRIKE; apply ADR-031 so a cash-secured put is margined at (strike x contracts x 100) - premium; note that STRADDLE and STRANGLE fall through to the naked branch and carry a put leg; and raise the unagreed cash-collateral attribute as an open question rather than inventing a name.");
        request.AppendLine("</ground_truth>");

        foreach (var (tier, run) in representative)
        {
            request.AppendLine($"<plan tier=\"{tier.Id}\" label=\"{tier.Label}\" context=\"{tier.Description}\" deterministic_score=\"{run!.Score}\">");
            request.AppendLine(run.Plan);
            request.AppendLine("</plan>");
        }

        request.AppendLine("Assess each plan against the ground truth, then explain what the degradation between tiers actually demonstrates.");

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-curve-judge",
            ClaraAuthorModel,
            ClaudeModel,
            Prompts.CurveJudgeSystem,
            request.ToString(),
            180,
            DateTimeOffset.UtcNow);
        session.Tasks[task.TaskId] = new TaskState(task);
        session.CurveJudgeTaskId = task.TaskId;
        session.CurveStatus = "judging";
    }

    private static void CompleteCurveJudge(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        session.CurveStatus = "completed";
        if (!succeeded)
        {
            session.CurveError = result.ErrorMessage ?? "The comparative review failed. The deterministic scores above still stand.";
            return;
        }

        if (!TryDeserialize(result.Content!, out CurveJudgeDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Summary))
        {
            session.CurveError = "The review did not match the expected contract. The deterministic scores above still stand.";
            return;
        }

        session.CurveVerdict = new CurveJudgeVerdict(
            dto.Summary!.Trim(),
            (dto.Tiers ?? []).Select(tier => new TierJudgement(
                tier.Tier ?? "tier",
                tier.Correctness,
                tier.Grounding,
                tier.Assessment ?? string.Empty,
                tier.Errors ?? [])).ToList(),
            dto.DegradationEvidence ?? [],
            dto.MinimumViableContext?.Trim() ?? string.Empty,
            dto.Caveat?.Trim() ?? string.Empty);
        session.CurveJudgeModel = result.ModelUsed;
    }

    private static List<ContextTierResult> BuildCurveTiers(SessionState session)
    {
        var tiers = new List<ContextTierResult>();
        foreach (var definition in ContextTierSamples.Tiers)
        {
            var runs = session.CurveRuns.Values
                .Where(run => run.Rung == definition.Index)
                .OrderBy(run => run.Attempt)
                .Select(run => new ContextTierRun(run.Rung, run.Attempt, run.Status, run.Score, run.Met, run.Missed, run.Plan, run.ModelUsed, run.Error))
                .ToList();
            var completed = runs.Where(run => run.Status == "completed").ToList();
            var scores = completed.Select(run => run.Score).ToList();
            var median = ContextTierScorer.Median(scores);
            var representative = completed.FirstOrDefault(run => run.Score == median) ?? completed.FirstOrDefault();

            tiers.Add(new ContextTierResult(
                definition.Index,
                definition.Id,
                definition.Label,
                definition.Description,
                session.CurveRungTokens.TryGetValue(definition.Index, out var tokens) ? tokens : TierPromptTokens.GetValueOrDefault(definition.Index),
                median,
                scores.Count > 0 ? scores.Max() : 0,
                scores.Count > 0 ? scores.Min() : 0,
                representative?.Met ?? [],
                representative?.Missed ?? [],
                runs));
        }

        return tiers;
    }

    // --- Neutral framing lab: same question, three framings, one model ---

    private static readonly (string Id, string Label, string Instruction)[] FramingArms =
    [
        ("leading", "Leading", "The engineer already believes the answer is yes and phrases the question to get agreement."),
        ("neutral", "Neutral", "The question is asked without signalling a preferred answer."),
        ("dissent", "Dissent demanded", "The question is neutral and additionally requires the strongest case against the proposal.")
    ];

    public void QueueFramingLab(string sessionId, string question)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 4000)
        {
            throw new ArgumentException("Provide a question between 1 and 4000 characters.", nameof(question));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.FramingArms.Clear();
            session.FramingTaskArm.Clear();
            session.FramingJudgeTaskId = null;
            session.FramingVerdict = null;
            session.FramingJudgeModel = null;
            session.FramingError = null;
            session.FramingQuestion = question.Trim();
            session.FramingStatus = "running";

            foreach (var (id, label, instruction) in FramingArms)
            {
                var userPrompt = BuildFramingRequest(id, session.FramingQuestion);
                var task = new BridgeTask(
                    Guid.NewGuid().ToString("N"),
                    sessionId,
                    "framing-arm",
                    ClaudeModel,
                    null,
                    Prompts.FramingArmSystem,
                    userPrompt,
                    90,
                    DateTimeOffset.UtcNow);
                session.FramingArms[id] = new FramingArmState(id, label, instruction)
                {
                    PromptTokens = _encoder.CountTokens(userPrompt).Tokens
                };
                session.FramingTaskArm[task.TaskId] = id;
                session.Tasks[task.TaskId] = new TaskState(task);
            }
        }
    }

    private static string BuildFramingRequest(string armId, string question) => armId switch
    {
        "leading" => $"We have already decided this is the right call and I just need you to confirm it. {question} It is the right approach, isn't it?",
        "dissent" => $"{question}\n\nGive your assessment, then state the strongest case AGAINST it and the conditions under which it would be the wrong choice.",
        _ => question
    };

    private void CompleteFramingArm(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!session.FramingTaskArm.TryGetValue(result.TaskId, out var armId) ||
            !session.FramingArms.TryGetValue(armId, out var arm))
        {
            return;
        }

        arm.ModelUsed = result.ModelUsed;
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            arm.Status = "failed";
            arm.Error = result.ErrorMessage ?? "The model returned no answer.";
        }
        else
        {
            arm.Status = "completed";
            arm.Answer = result.Content.Trim();
        }

        if (session.FramingArms.Values.Any(candidate => candidate.Status is "pending" or "running"))
        {
            return;
        }

        ScoreFramingSimilarity(session);

        if (session.FramingArms.Values.Count(candidate => candidate.Status == "completed") < 2)
        {
            session.FramingStatus = "failed";
            session.FramingError = "Not enough arms completed to compare framings.";
            return;
        }

        if (session.FramingJudgeTaskId is not null)
        {
            return;
        }

        var judgeTask = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "framing-judge",
            ClaudeModel,
            null,
            Prompts.FramingJudgeSystem,
            BuildFramingJudgeRequest(session),
            90,
            DateTimeOffset.UtcNow);
        session.FramingJudgeTaskId = judgeTask.TaskId;
        session.Tasks[judgeTask.TaskId] = new TaskState(judgeTask);
        session.FramingStatus = "judging";
    }

    // Deterministic divergence: cosine distance from the neutral answer, so the effect of framing is
    // visible as a number before any model is asked for an opinion about it.
    private void ScoreFramingSimilarity(SessionState session)
    {
        if (!_encoder.IsAvailable ||
            !session.FramingArms.TryGetValue("neutral", out var neutral) ||
            neutral.Status != "completed" ||
            _encoder.Encode(neutral.Answer!) is not { } baseline)
        {
            return;
        }

        foreach (var arm in session.FramingArms.Values.Where(candidate => candidate.Status == "completed"))
        {
            var vector = _encoder.Encode(arm.Answer!);
            arm.SimilarityToNeutral = vector is null ? 0 : Math.Round(EmbeddingGemmaEncoder.CosineSimilarity(vector, baseline), 3);
        }
    }

    private static string BuildFramingJudgeRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<question>");
        request.AppendLine(session.FramingQuestion);
        request.AppendLine("</question>");
        foreach (var arm in session.FramingArms.Values.Where(arm => arm.Status == "completed"))
        {
            request.AppendLine($"<answer framing=\"{arm.Label}\">");
            request.AppendLine(arm.Answer);
            request.AppendLine("</answer>");
        }

        request.AppendLine("Decide whether the framing changed the substance of the answer, not merely its tone.");
        return request.ToString();
    }

    private static void CompleteFramingJudge(SessionState session, BridgeTaskResult result, bool succeeded)    {
        if (!succeeded)
        {
            session.FramingStatus = "failed";
            session.FramingError = result.ErrorMessage ?? "The framing review failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out FramingVerdictDto? dto) || dto is null || string.IsNullOrWhiteSpace(dto.Summary))
        {
            session.FramingStatus = "failed";
            session.FramingError = "The framing review did not match the expected contract.";
            return;
        }

        session.FramingVerdict = new FramingVerdict(
            dto.SubstanceChanged,
            dto.Summary!.Trim(),
            dto.Differences ?? [],
            dto.Recommendation?.Trim() ?? string.Empty);
        session.FramingJudgeModel = result.ModelUsed;
        session.FramingStatus = "completed";
    }

    // --- Ephemeral test-ticket harness: real data, gated before it leaves, never committed ---

    public void QueueEphemeralTest(string sessionId, string rawTicket)
    {
        if (string.IsNullOrWhiteSpace(rawTicket) || rawTicket.Length > 12000)
        {
            throw new ArgumentException("Provide a ticket between 1 and 12000 characters.", nameof(rawTicket));
        }

        var session = RequireSession(sessionId);
        var gate = _dataTier.Assess(rawTicket.Trim());
        lock (_gate)
        {
            session.EphemeralRaw = rawTicket.Trim();
            session.EphemeralGate = gate;
            session.EphemeralPlan = null;
            session.EphemeralModel = null;
            session.EphemeralError = null;
            session.EphemeralRecord = BuildEphemeralRecord(gate, session.EphemeralRaw);

            if (gate.Decision == "BLOCK")
            {
                // The gate is the point of the exercise: blocked content does not reach a model at all.
                session.EphemeralStatus = "blocked";
                return;
            }

            var payload = string.IsNullOrWhiteSpace(gate.Scrubbed) ? session.EphemeralRaw : gate.Scrubbed;
            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "ephemeral-test",
                GemmaModel,
                GptFallbackModel,
                Prompts.EphemeralTestSystem,
                $"<ephemeral_ticket>\n{payload}\n</ephemeral_ticket>\nProduce the reproduction plan.",
                90,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
            session.EphemeralStatus = "running";
        }
    }

    private static EphemeralRecord BuildEphemeralRecord(DataTierAssessment gate, string raw)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw)))[..16];

        return new EphemeralRecord(
            "FUL-TEST-" + digest[..6],
            "repo owner (named at creation)",
            "in-memory only — never written to disk, never committed",
            "purged when the server stops or the session ends",
            "artifact references and digests only; payloads are never logged",
            digest,
            gate.Findings.Select(finding => $"{finding.Count}× {finding.Label} ({finding.Category})").ToList());
    }

    private static void CompleteEphemeralTest(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            session.EphemeralStatus = "failed";
            session.EphemeralError = result.ErrorMessage ?? "The ephemeral test run failed.";
            return;
        }

        session.EphemeralPlan = result.Content.Trim();
        session.EphemeralModel = result.ModelUsed;
        session.EphemeralStatus = "completed";
    }

    // --- Language A/B test: identical JIRA, one arm over C#, one arm over CLARA, neither given context ---

    public void QueueLanguageTest(string sessionId, string jira)
    {
        if (string.IsNullOrWhiteSpace(jira) || jira.Length > 8000)
        {
            throw new ArgumentException("Provide a requirement between 1 and 8000 characters.", nameof(jira));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.LanguageArms.Clear();
            session.LanguageTaskArm.Clear();
            session.LanguageJudgeTaskId = null;
            session.LanguageVerdict = null;
            session.LanguageError = null;
            session.LanguageJira = jira.Trim();
            session.LanguageStatus = "running";

            // Arm order is shuffled so position carries no signal into the graded comparison.
            var languages = new[] { "csharp", "clara" }.OrderBy(_ => Random.Shared.Next()).ToArray();
            for (var index = 0; index < languages.Length; index++)
            {
                var language = languages[index];
                var armId = index == 0 ? "1" : "2";
                var isClara = language == "clara";
                var task = new BridgeTask(
                    Guid.NewGuid().ToString("N"),
                    sessionId,
                    isClara ? "gemma-language-clara" : "gemma-language-csharp",
                    GemmaModel,
                    GptFallbackModel,
                    isClara ? Prompts.LanguageClaraSystem : Prompts.LanguageCSharpSystem,
                    isClara ? BuildClaraArmRequest(session) : BuildCSharpArmRequest(session),
                    120,
                    DateTimeOffset.UtcNow);

                var (promptTokens, exact) = _encoder.CountTokens(task.SystemPrompt + "\n" + task.UserPrompt);
                session.LanguageTokensExact = exact;
                session.LanguageArms[armId] = new LanguageArmState(armId, language, GemmaModel)
                {
                    TaskId = task.TaskId,
                    PromptTokens = promptTokens
                };
                session.LanguageTaskArm[task.TaskId] = armId;
                session.Tasks[task.TaskId] = new TaskState(task);
            }
        }
    }

    private void CompleteLanguageArm(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!session.LanguageTaskArm.TryGetValue(result.TaskId, out var armId) ||
            !session.LanguageArms.TryGetValue(armId, out var arm))
        {
            return;
        }

        arm.ModelUsed = result.ModelUsed;
        arm.FallbackUsed = result.FallbackUsed;
        arm.DurationMs = result.DurationMs;

        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            arm.Status = "failed";
            arm.Error = result.ErrorMessage ?? "The model returned no answer.";
            return;
        }

        arm.Status = "completed";
        arm.Answer = StripCodeFence(result.Content.Trim());

        // Only the CLARA arm can be checked objectively: its answer is a program the server can compile and run.
        if (arm.Language == "clara")
        {
            arm.Verification = ClaraCompiler.Run(arm.Answer);
        }
    }

    private void MaybeQueueLanguageJudge(SessionState session)
    {
        if (session.LanguageArms.Count == 0 || session.LanguageJudgeTaskId is not null)
        {
            return;
        }

        if (session.LanguageArms.Values.Any(arm => arm.Status is "pending" or "running"))
        {
            return;
        }

        if (session.LanguageArms.Values.Count(arm => arm.Status == "completed") < 2)
        {
            session.LanguageStatus = "failed";
            session.LanguageError = "Both arms must answer before the comparison can be graded.";
            return;
        }

        var task = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "claude-language-judge",
            ClaudeModel,
            null,
            Prompts.LanguageJudgeSystem,
            BuildLanguageJudgeRequest(session),
            150,
            DateTimeOffset.UtcNow);

        session.LanguageJudgeTaskId = task.TaskId;
        session.Tasks[task.TaskId] = new TaskState(task);
        session.LanguageStatus = "judging";
    }

    private static void CompleteLanguageJudge(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.LanguageStatus = "failed";
            session.LanguageError = result.ErrorMessage ?? "The comparison review failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out LanguageVerdictDto? dto) || dto is null || dto.Scores is null)
        {
            session.LanguageStatus = "failed";
            session.LanguageError = "The comparison review did not match the expected contract.";
            return;
        }

        var criteria = LanguageTestSamples.SealedCriteria;
        var scores = new List<LanguageTestCriterionScore>();
        foreach (var score in dto.Scores)
        {
            var armId = score.Arm == 1 ? "1" : "2";
            if (!session.LanguageArms.TryGetValue(armId, out var arm) ||
                score.Criterion < 1 || score.Criterion > criteria.Length)
            {
                continue;
            }

            scores.Add(new LanguageTestCriterionScore(
                arm.Language,
                criteria[score.Criterion - 1],
                score.Met,
                score.Evidence?.Trim() ?? ""));
        }

        var winner = dto.Winner?.Trim() switch
        {
            "1" => session.LanguageArms.TryGetValue("1", out var first) ? first.Language : "tie",
            "2" => session.LanguageArms.TryGetValue("2", out var second) ? second.Language : "tie",
            _ => "tie"
        };

        session.LanguageVerdict = new LanguageTestVerdict(
            result.ModelUsed ?? ClaudeModel,
            scores,
            scores.Count(score => score.Arm == "csharp" && score.Met),
            scores.Count(score => score.Arm == "clara" && score.Met),
            criteria.Length,
            winner,
            dto.Rationale?.Trim() ?? "",
            dto.LocalKnowledgeMissed ?? [],
            dto.Caveats ?? []);
        session.LanguageStatus = "completed";
    }

    private static string BuildCSharpArmRequest(SessionState session) => $"""
        <jira>
        {session.LanguageJira}
        </jira>
        <csharp_source>
        {LanguageTestSamples.CSharpSource}
        </csharp_source>
        Implement the change. Return only the complete updated C# class, no prose.
        """;

    private static string BuildClaraArmRequest(SessionState session) => $"""
        <jira>
        {session.LanguageJira}
        </jira>
        <clara_policy>
        {LanguageTestSamples.ClaraSource}
        </clara_policy>
        Implement the change. Return only the complete updated CLARA policy, no prose and no Markdown fences.
        """;

    private static string BuildLanguageJudgeRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<jira>");
        request.AppendLine(session.LanguageJira);
        request.AppendLine("</jira>");
        request.AppendLine("<ground_truth>");
        request.AppendLine("This is the on-premise policy knowledge. NEITHER arm was given it.");
        request.AppendLine(LanguageTestSamples.ContextPack);
        request.AppendLine("</ground_truth>");
        request.AppendLine("<criteria>");
        for (var index = 0; index < LanguageTestSamples.SealedCriteria.Length; index++)
        {
            request.AppendLine($"{index + 1}. {LanguageTestSamples.SealedCriteria[index]}");
        }

        request.AppendLine("</criteria>");

        foreach (var arm in session.LanguageArms.Values.OrderBy(arm => arm.Arm, StringComparer.Ordinal))
        {
            request.AppendLine($"<arm id=\"{arm.Arm}\" language=\"{(arm.Language == "clara" ? "CLARA" : "C#")}\">");
            request.AppendLine(arm.Answer ?? "(no answer)");
            if (arm.Verification is { } verification)
            {
                request.AppendLine("<machine_verification>");
                request.AppendLine(verification.Compiled
                    ? $"compiled; {verification.Examples.Count(example => example.Passed == true)}/{verification.Examples.Count} declared examples passed"
                    : "did not compile: " + string.Join("; ", verification.Errors));
                foreach (var example in verification.Examples.Where(example => example.Passed == false))
                {
                    request.AppendLine($"FAILED example '{example.Description}': {example.Note}");
                }

                request.AppendLine("</machine_verification>");
            }

            request.AppendLine("</arm>");
        }

        request.AppendLine("Grade every criterion for every arm against the ground truth. Judge behaviour, not style.");
        return request.ToString();
    }

    // --- COBOL migration A/B/C: same program, three levels of grounding, graded by an executable oracle ---

    public void QueueMigration(string sessionId)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.MigrationArms.Clear();
            session.MigrationTaskArm.Clear();
            session.MigrationError = null;
            session.MigrationStatus = "running";

            // The oracle runs first: the arms are graded against what the legacy program actually does.
            session.MigrationFacts ??= _cobol.ExtractFacts(MigrationSamples.CobolSource);
            session.MigrationOracle = _cobol.RunOracle(MigrationSamples.CobolSource, MigrationSamples.Cases);

            foreach (var (arm, grounding) in new[]
            {
                ("A", "none"),
                ("B", "structural"),
                ("C", "compiler")
            })
            {
                var task = new BridgeTask(
                    Guid.NewGuid().ToString("N"),
                    sessionId,
                    "migration-arm",
                    GemmaModel,
                    GptFallbackModel,
                    Prompts.MigrationSystem,
                    BuildMigrationRequest(session, grounding),
                    120,
                    DateTimeOffset.UtcNow);

                var (promptTokens, _) = _encoder.CountTokens(task.SystemPrompt + "\n" + task.UserPrompt);
                session.MigrationArms[arm] = new MigrationArmState(arm, grounding, GemmaModel)
                {
                    TaskId = task.TaskId,
                    PromptTokens = promptTokens
                };
                session.MigrationTaskArm[task.TaskId] = arm;
                session.Tasks[task.TaskId] = new TaskState(task);
            }
        }
    }

    private void CompleteMigrationArm(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!session.MigrationTaskArm.TryGetValue(result.TaskId, out var armId) ||
            !session.MigrationArms.TryGetValue(armId, out var arm))
        {
            return;
        }

        arm.ModelUsed = result.ModelUsed;
        arm.DurationMs = result.DurationMs;

        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            arm.Status = "failed";
            arm.Error = result.ErrorMessage ?? "The model returned no code.";
            MaybeCompleteMigration(session);
            return;
        }

        arm.Code = StripCodeFence(result.Content.Trim());

        var run = _sandbox.Run(arm.Code, MigrationSamples.EntryType, MigrationSamples.EntryMethod, MigrationSamples.Cases);
        arm.Compiled = run.Ran;
        arm.CompileErrors.Clear();
        arm.CompileErrors.AddRange(run.CompileErrors);
        arm.Cases.Clear();

        if (!run.Ran)
        {
            arm.Status = "completed";
            arm.Error = run.Error;
            MaybeCompleteMigration(session);
            return;
        }

        // Grading is arithmetic against the compiled COBOL's own output. No model is consulted.
        var expected = session.MigrationOracle.ToDictionary(entry => entry.Input, entry => entry);
        foreach (var output in run.Outputs)
        {
            var oracle = expected.GetValueOrDefault(output.Input);
            var truth = NormaliseOracle(oracle?.Output);
            var actual = Normalise(output.Value);
            var matched = output.Error is null && truth is not null && truth == actual;
            arm.Cases.Add(new MigrationCaseResult(
                output.Input,
                truth ?? "(oracle unavailable)",
                output.Error is null ? actual : "error",
                matched,
                output.Error));
        }

        arm.Status = "completed";
        MaybeCompleteMigration(session);
    }

    private static void MaybeCompleteMigration(SessionState session)
    {
        if (session.MigrationArms.Count > 0 &&
            session.MigrationArms.Values.All(arm => arm.Status is "completed" or "failed"))
        {
            session.MigrationStatus = "completed";
        }
    }

    // COBOL DISPLAY of S9(6)V99 yields "00080585+" - eight digits with a trailing sign and implied scale.
    private static string? NormaliseOracle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var negative = text.EndsWith('-');
        text = text.TrimEnd('+', '-');
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        value /= 100m;
        return (negative ? -value : value).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Normalise(string raw) =>
        decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : raw.Trim();

    private static string BuildMigrationRequest(SessionState session, string grounding)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<cobol_source>");
        request.AppendLine(MigrationSamples.CobolSource);
        request.AppendLine("</cobol_source>");

        if (grounding == "structural")
        {
            request.AppendLine("<structural_artifacts>");
            request.AppendLine(MigrationSamples.StructuralArtifacts);
            request.AppendLine("</structural_artifacts>");
        }
        else if (grounding == "compiler" && session.MigrationFacts is { Compiled: true } facts)
        {
            request.AppendLine("<compiler_resolved_facts>");
            request.AppendLine("Field layout, as resolved by the COBOL compiler:");
            foreach (var field in facts.Fields)
            {
                request.AppendLine($"  {field.Name}: offset {field.Offset}, {field.Size} bytes, {field.Attribute}");
            }

            request.AppendLine("Control flow, as resolved by the COBOL compiler:");
            foreach (var line in facts.ControlFlow)
            {
                request.AppendLine("  " + line);
            }

            request.AppendLine("</compiler_resolved_facts>");
        }

        request.AppendLine(MigrationSamples.Contract);
        return request.ToString();
    }

    // --- Ticket quality gate: a deterministic pre-check, then a model review and rewrite ---

    public void QueueTicketGate(string sessionId, string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 8000)
        {
            throw new ArgumentException("Provide a ticket between 1 and 8000 characters.", nameof(ticket));
        }

        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.TicketText = ticket.Trim();
            session.TicketReview = null;
            session.TicketError = null;
            session.TicketPrecheck = GovernanceSamples.ScoreTicket(session.TicketText);
            session.TicketStatus = "running";

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "ticket-gate",
                GemmaModel,
                ClaudeModel,
                Prompts.TicketGateSystem,
                BuildTicketRequest(session),
                90,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
        }
    }

    private static void CompleteTicketGate(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out TicketReviewDto? dto) || dto is null)
        {
            session.TicketStatus = "failed";
            session.TicketError = result.ErrorMessage ?? "The ticket review did not match the expected contract.";
            return;
        }

        var findings = (dto.Findings ?? [])
            .Select(finding => new ContextStructureSignal(finding.Criterion ?? "", finding.Met, finding.Note ?? ""))
            .ToList();

        session.TicketReview = new TicketReview(
            result.ModelUsed ?? ClaudeModel,
            Math.Clamp(dto.Score, 0, 100),
            dto.Passed,
            findings,
            dto.Rewritten?.Trim() ?? "",
            dto.Verdict?.Trim() ?? "");
        session.TicketStatus = "completed";
    }

    private static string BuildTicketRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<rubric>");
        for (var index = 0; index < GovernanceSamples.Rubric.Length; index++)
        {
            var criterion = GovernanceSamples.Rubric[index];
            request.AppendLine($"{index + 1}. {criterion.Name} - {criterion.Test}");
        }

        request.AppendLine("</rubric>");
        request.AppendLine("<deterministic_precheck>");
        foreach (var signal in session.TicketPrecheck?.Signals ?? [])
        {
            request.AppendLine($"{(signal.Present ? "PASS" : "FAIL")} {signal.Name}: {signal.Detail}");
        }

        request.AppendLine("</deterministic_precheck>");
        request.AppendLine("<ticket>");
        request.AppendLine(session.TicketText);
        request.AppendLine("</ticket>");
        return request.ToString();
    }

    // --- Trap hunt: an agent audits an IaC design, then is graded against the sealed trap list ---

    public void QueueTrapHunt(string sessionId)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.TrapFindings = null;
            session.TrapGrade = null;
            session.TrapError = null;
            session.TrapStatus = "hunting";

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "trap-hunt",
                GemmaModel,
                GptFallbackModel,
                Prompts.TrapHuntSystem,
                $"<terraform>\n{GovernanceSamples.TerraformDesign}\n</terraform>\nAudit this proposed sandbox design and list every security, cost and governance defect you find.",
                120,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
        }
    }

    private static void CompleteTrapHunt(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            session.TrapStatus = "failed";
            session.TrapError = result.ErrorMessage ?? "The audit produced no findings.";
            return;
        }

        session.TrapFindings = result.Content.Trim();
        session.TrapFindingsModel = result.ModelUsed;

        var judge = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "trap-judge",
            ClaudeModel,
            null,
            Prompts.TrapJudgeSystem,
            BuildTrapJudgeRequest(session),
            120,
            DateTimeOffset.UtcNow);
        session.Tasks[judge.TaskId] = new TaskState(judge);
        session.TrapStatus = "judging";
    }

    private static void CompleteTrapJudge(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out ContextJudgeDto? dto) || dto is null)
        {
            session.TrapStatus = "failed";
            session.TrapError = result.ErrorMessage ?? "The audit grade did not match the expected contract.";
            return;
        }

        session.TrapGrade = new ContextJudgeResult(
            Math.Clamp(dto.TrapsFound, 0, GovernanceSamples.PlantedTraps.Length),
            GovernanceSamples.PlantedTraps.Length,
            dto.Matched ?? [],
            dto.Missed ?? [],
            dto.FalsePositives ?? [],
            dto.Verdict?.Trim() ?? "");
        session.TrapJudgeModel = result.ModelUsed;
        session.TrapStatus = "completed";
    }

    private static string BuildTrapJudgeRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<planted_defects>");
        for (var index = 0; index < GovernanceSamples.PlantedTraps.Length; index++)
        {
            request.AppendLine($"{index + 1}. {GovernanceSamples.PlantedTraps[index]}");
        }

        request.AppendLine("</planted_defects>");
        request.AppendLine("<audit_findings>");
        request.AppendLine(session.TrapFindings);
        request.AppendLine("</audit_findings>");
        return request.ToString();
    }

    // --- Drift scorecard: functional and structural layers are deterministic; only the semantic layer asks a model ---

    public void QueueDriftReview(string sessionId, bool useHumanPatch)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.DriftCandidate = useHumanPatch ? DriftSamples.HumanPatch : DriftSamples.DriftedPatch;
            session.DriftLabel = useHumanPatch ? "the human patch (control)" : "drifted agent patch";
            session.DriftSemantic = null;
            session.DriftError = null;
            session.DriftStatus = "running";

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "drift-review",
                ClaudeModel,
                GptFallbackModel,
                Prompts.DriftSystem,
                BuildDriftRequest(session),
                120,
                DateTimeOffset.UtcNow);
            session.Tasks[task.TaskId] = new TaskState(task);
        }
    }

    private static void CompleteDriftReview(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out DriftReviewDto? dto) || dto is null)
        {
            session.DriftStatus = "failed";
            session.DriftError = result.ErrorMessage ?? "The drift review did not match the expected contract.";
            return;
        }

        session.DriftSemantic = new DriftSemantic(
            result.ModelUsed ?? ClaudeModel,
            Math.Clamp(dto.GoalFidelity, 1, 5),
            Math.Clamp(dto.ScopeDiscipline, 1, 5),
            dto.Deviations ?? [],
            dto.Verdict?.Trim() ?? "");
        session.DriftStatus = "completed";
    }

    private static string BuildDriftRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<ticket>");
        request.AppendLine(DriftSamples.GoldenTicket);
        request.AppendLine("</ticket>");
        request.AppendLine("<frozen_baseline>");
        request.AppendLine(DriftSamples.FrozenCode);
        request.AppendLine("</frozen_baseline>");
        request.AppendLine("<human_patch>");
        request.AppendLine(DriftSamples.HumanPatch);
        request.AppendLine("</human_patch>");
        request.AppendLine("<candidate_patch>");
        request.AppendLine(session.DriftCandidate);
        request.AppendLine("</candidate_patch>");
        request.AppendLine("<deterministic_structural_layer>");
        foreach (var finding in DriftSamples.ScoreStructure(session.DriftCandidate).Findings)
        {
            request.AppendLine($"{(finding.Present ? "PASS" : "FAIL")} {finding.Name}: {finding.Detail}");
        }

        request.AppendLine("</deterministic_structural_layer>");
        return request.ToString();
    }

    // --- Scene 4: implement the JIRA from the OKF bundle, then trace every change back to a cited statement ---

    public void QueueGroundedChange(string sessionId, string jira)
    {
        var session = RequireSession(sessionId);
        lock (_gate)
        {
            session.ChangeJira = string.IsNullOrWhiteSpace(jira) ? FixtureJira : jira.Trim();
            session.ChangeCode = null;
            session.ChangeModel = null;
            session.ChangeAudit = null;
            session.ChangeError = null;
            session.ChangeStatus = "implementing";

            var task = new BridgeTask(
                Guid.NewGuid().ToString("N"),
                sessionId,
                "grounded-change",
                GemmaModel,
                GptFallbackModel,
                Prompts.GroundedChangeSystem,
                BuildGroundedChangeRequest(session),
                150,
                DateTimeOffset.UtcNow);

            session.ChangePromptTokens = _encoder.CountTokens(task.SystemPrompt + "\n" + task.UserPrompt).Tokens;
            session.Tasks[task.TaskId] = new TaskState(task);
        }
    }

    private void CompleteGroundedChange(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || string.IsNullOrWhiteSpace(result.Content))
        {
            session.ChangeStatus = "failed";
            session.ChangeError = result.ErrorMessage ?? "The implementation task returned nothing.";
            return;
        }

        session.ChangeCode = result.Content.Trim();
        session.ChangeModel = result.ModelUsed;

        var audit = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "grounded-change-audit",
            ClaudeModel,
            null,
            Prompts.GroundedAuditSystem,
            BuildGroundedAuditRequest(session),
            150,
            DateTimeOffset.UtcNow);
        session.Tasks[audit.TaskId] = new TaskState(audit);
        session.ChangeStatus = "auditing";
    }

    private static void CompleteGroundedAudit(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded || !TryDeserialize(result.Content!, out GroundedAuditDto? dto) || dto is null)
        {
            session.ChangeStatus = "failed";
            session.ChangeError = result.ErrorMessage ?? "The citation audit did not match the expected contract.";
            return;
        }

        session.ChangeAudit = new GroundedChangeAudit(
            result.ModelUsed ?? ClaudeModel,
            (dto.Citations ?? []).Select(citation => new GroundingCitation(
                citation.Change?.Trim() ?? "",
                citation.Document?.Trim() ?? "",
                citation.Statement?.Trim() ?? "",
                citation.Grounded)).ToList(),
            dto.Ungrounded ?? [],
            dto.OpenQuestionRaised,
            dto.OpenQuestionNote?.Trim() ?? "",
            dto.PlaceholdersMarked,
            dto.PlaceholderNote?.Trim() ?? "",
            dto.Verdict?.Trim() ?? "");
        session.ChangeStatus = "completed";
    }

    private string BuildGroundedChangeRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<jira>");
        request.AppendLine(session.ChangeJira);
        request.AppendLine("</jira>");

        foreach (var artifact in _fixture.Artifacts().Where(artifact => artifact.Kind != "jira"))
        {
            request.AppendLine($"<artifact path=\"{artifact.Path}\">");
            request.AppendLine(artifact.Content);
            request.AppendLine("</artifact>");
        }

        request.AppendLine("Implement the change. For every decision you make, cite the artifact path that justifies it.");
        return request.ToString();
    }

    private string BuildGroundedAuditRequest(SessionState session)
    {
        var request = new System.Text.StringBuilder();
        request.AppendLine("<jira>");
        request.AppendLine(session.ChangeJira);
        request.AppendLine("</jira>");

        foreach (var artifact in _fixture.Artifacts().Where(artifact => artifact.Kind != "jira"))
        {
            request.AppendLine($"<artifact path=\"{artifact.Path}\">");
            request.AppendLine(artifact.Content);
            request.AppendLine("</artifact>");
        }

        request.AppendLine("<implementation>");
        request.AppendLine(session.ChangeCode);
        request.AppendLine("</implementation>");
        return request.ToString();
    }

    public bool AppendReviewTrace(string token, ReviewTraceEvent traceEvent)
    {
        var session = FindByToken(token);
        if (session is null || session.SessionId != traceEvent.SessionId ||
            !session.Tasks.TryGetValue(traceEvent.TaskId, out var task) ||
            task.Task.Kind != "claude-review" || task.Status is "completed" or "failed")
        {
            return false;
        }

        if (traceEvent.Sequence < 1 || traceEvent.Sequence > 20 ||
            string.IsNullOrWhiteSpace(traceEvent.Stage) || traceEvent.Stage.Length > 80 ||
            string.IsNullOrWhiteSpace(traceEvent.Evidence) || traceEvent.Evidence.Length > 500 ||
            string.IsNullOrWhiteSpace(traceEvent.Decision) || traceEvent.Decision.Length > 500)
        {
            return false;
        }

        lock (_gate)
        {
            if (session.ReviewTrace.All(existing => existing.Sequence != traceEvent.Sequence))
            {
                session.ReviewTrace.Add(traceEvent with { Timestamp = DateTimeOffset.UtcNow });
                session.ReviewTrace.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            }

            session.ReviewPartial = string.Empty;
        }

        return true;
    }

    public bool AppendReviewStream(string token, ReviewStreamChunk chunk)
    {
        var session = FindByToken(token);
        if (session is null || session.SessionId != chunk.SessionId ||
            !session.Tasks.TryGetValue(chunk.TaskId, out var task) ||
            task.Task.Kind != "claude-review" || task.Status is "completed" or "failed")
        {
            return false;
        }

        lock (_gate)
        {
            session.ReviewPartial = HumaniseStreamFragment(chunk.Text);
        }

        return true;
    }

    // The model streams JSON. Showing the raw scaffolding reads as a bug rather than as progress,
    // so field names and delimiters become plain prose while the text is still arriving.
    public static string HumaniseStreamFragment(string text)
    {
        var readable = text.Replace("\\\"", "\"").Replace("\\n", " ");

        // Comma-prefixed forms first, so a separator is not left stranded when the field name goes.
        (string From, string To)[] fields =
        [
            ("{\"trace\":", ""),
            (",\"sequence\":", " step "),
            ("\"sequence\":", "step "),
            (",\"stage\":", " · "),
            ("\"stage\":", ""),
            (",\"evidence\":", " — quoting: "),
            ("\"evidence\":", " — quoting: "),
            (",\"decision\":", " — decided: "),
            ("\"decision\":", " — decided: ")
        ];

        foreach (var (from, to) in fields)
        {
            readable = readable.Replace(from, to);
        }

        readable = readable
            .Replace("{", string.Empty)
            .Replace("}", string.Empty)
            .Replace("[", string.Empty)
            .Replace("]", string.Empty)
            .Replace("\"", string.Empty);

        while (readable.Contains("  "))
        {
            readable = readable.Replace("  ", " ");
        }

        readable = readable.Trim().TrimStart('·', ',', ' ').Trim();
        return readable.Length > 400 ? "…" + readable[^400..] : readable;
    }

    public BridgeTask? ClaimNextTask(string token)
    {
        var session = FindByToken(token);
        if (session is null)
        {
            return null;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var task = session.Tasks.Values
                .Where(candidate => candidate.Status == "queued" ||
                    (candidate.Status == "claimed" && now - candidate.ClaimedAt > ClaimLease))
                .OrderBy(candidate => candidate.Task.CreatedAt)
                .FirstOrDefault();
            if (task is null)
            {
                return null;
            }

            task.Status = "claimed";
            task.ClaimedAt = now;
            switch (task.Task.Kind)
            {
                case "autopilot-narration":
                    session.NarrationStatus = "running";
                    break;
                case "coach-narration":
                    session.CoachStatus = "running";
                    break;
                case "claude-review":
                    session.ReviewStatus = "running";
                    break;
                case "model-answer":
                    if (session.ComparisonTaskSlot.TryGetValue(task.Task.TaskId, out var slotKey) &&
                        session.ComparisonSlots.TryGetValue(slotKey, out var slot))
                    {
                        slot.Status = "running";
                    }

                    if (session.ComparisonStatus != "judging")
                    {
                        session.ComparisonStatus = "running";
                    }

                    break;
                case "model-judge":
                    session.ComparisonStatus = "judging";
                    break;
                case "gemma-story":
                    session.RoundTripStatus = "story";
                    break;
                case "gemma-recreate":
                    session.RoundTripStatus = "recreating";
                    break;
                case "claude-roundtrip-qa":
                    session.RoundTripStatus = "qa";
                    break;
                case "claude-modernize":
                    session.ModernizeStatus = "running";
                    break;
                case "gemma-audit":
                    session.AuditStatus = "running";
                    break;
                case "gemma-context":
                    session.ContextStatus = "gemma";
                    break;
                case "claude-context-judge":
                    session.ContextStatus = "judging";
                    break;
                case "claude-clara-author":
                    session.ClaraStatus = "authoring";
                    break;
                case "claude-clara-review":
                    session.ClaraStatus = "reviewing";
                    break;
                case "gemini-gcp-advisor":
                    session.SupportStatus = "advising";
                    break;
                case "claude-gcp-synthesis":
                    session.SupportStatus = "synthesizing";
                    break;
                case "claude-consolidation-design":
                    session.ConsolidationStatus = "designing";
                    break;
                case "claude-tier1-discover":
                    session.TierStatus = "tier1";
                    break;
                case "gemma-regression-guidance":
                    session.GuidanceStatus = "advising";
                    break;
                case "gemma-pattern-apply":
                    session.PatternStatus = "applying";
                    break;
                case "claude-crypto-unaided":
                    session.SmeStatus = "unaided";
                    break;
                case "gemma-crypto-sme":
                    session.SmeStatus = "consulting";
                    break;
                case "claude-crypto-design":
                    session.SmeStatus = "designing";
                    break;
                case "claude-pattern-review":
                    session.PatternStatus = "reviewing";
                    break;
                case "claude-regression-review":
                    session.GuidanceStatus = "reviewing";
                    break;
                case "claude-tier2-scope":
                    session.TierStatus = "tier2";
                    break;
                case "claude-tier3-design":
                    session.TierStatus = "tier3";
                    break;
                case "gemma-consolidation-build":
                    session.ConsolidationStatus = "building";
                    break;
                case "context-curve-plan":
                    session.CurveStatus = "running";
                    break;
                case "claude-curve-judge":
                    session.CurveStatus = "judging";
                    break;
                case "framing-arm":
                    if (session.FramingStatus != "judging")
                    {
                        session.FramingStatus = "running";
                    }

                    break;
                case "framing-judge":
                    session.FramingStatus = "judging";
                    break;
                case "ephemeral-test":
                    session.EphemeralStatus = "running";
                    break;
                case "gemma-language-csharp":
                case "gemma-language-clara":
                    if (session.LanguageArms.TryGetValue(
                            session.LanguageTaskArm.TryGetValue(task.Task.TaskId, out var languageArm) ? languageArm : "",
                            out var armState))
                    {
                        armState.Status = "running";
                    }

                    if (session.LanguageStatus != "judging")
                    {
                        session.LanguageStatus = "running";
                    }

                    break;
                case "claude-language-judge":
                    session.LanguageStatus = "judging";
                    break;
                case "migration-arm":
                    if (session.MigrationTaskArm.TryGetValue(task.Task.TaskId, out var migrationArm) &&
                        session.MigrationArms.TryGetValue(migrationArm, out var migrationState))
                    {
                        migrationState.Status = "running";
                    }

                    session.MigrationStatus = "running";
                    break;
                case "ticket-gate":
                    session.TicketStatus = "running";
                    break;
                case "trap-hunt":
                    session.TrapStatus = "hunting";
                    break;
                case "trap-judge":
                    session.TrapStatus = "judging";
                    break;
                case "drift-review":
                    session.DriftStatus = "running";
                    break;
                case "grounded-change":
                    session.ChangeStatus = "implementing";
                    break;
                case "grounded-change-audit":
                    session.ChangeStatus = "auditing";
                    break;
            }

            return task.Task;
        }
    }

    public bool CompleteTask(string token, BridgeTaskResult result)
    {
        var session = FindByToken(token);
        if (session is null || session.SessionId != result.SessionId ||
            !session.Tasks.TryGetValue(result.TaskId, out var task))
        {
            return false;
        }

        lock (_gate)
        {
            if (task.Status == "completed")
            {
                return true;
            }

            var succeeded = result.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.Content);
            task.Status = succeeded ? "completed" : "failed";

            switch (task.Task.Kind)
            {
                case "autopilot-narration":
                    CompleteNarration(session, result, succeeded);
                    break;
                case "coach-narration":
                    CompleteCoach(session, result, succeeded);
                    break;
                case "claude-review":
                    CompleteReview(session, result, succeeded);
                    break;
                case "model-answer":
                    CompleteModelAnswer(session, result, succeeded);
                    break;
                case "model-judge":
                    CompleteJudge(session, result, succeeded);
                    break;
                case "gemma-story":
                    CompleteRoundTripStory(session, result, succeeded);
                    break;
                case "gemma-recreate":
                    CompleteRoundTripRecreate(session, result, succeeded);
                    break;
                case "claude-roundtrip-qa":
                    CompleteRoundTripQa(session, result, succeeded);
                    break;
                case "claude-modernize":
                    CompleteModernize(session, result, succeeded);
                    break;
                case "gemma-audit":
                    CompleteAudit(session, result, succeeded);
                    break;
                case "gemma-context":
                    CompleteContextGemma(session, result, succeeded);
                    break;
                case "claude-context-judge":
                    CompleteContextJudge(session, result, succeeded);
                    break;
                case "claude-clara-author":
                    CompleteClaraAuthor(session, result, succeeded);
                    break;
                case "claude-clara-review":
                    CompleteClaraReview(session, result, succeeded);
                    break;
                case "gemini-gcp-advisor":
                    CompleteGeminiAdvice(session, result, succeeded);
                    break;
                case "claude-gcp-synthesis":
                    CompleteSupportSynthesis(session, result, succeeded);
                    break;
                case "claude-consolidation-design":
                    CompleteConsolidationDesign(session, result, succeeded);
                    break;
                case "claude-tier1-discover":
                    CompleteTier1(session, result, succeeded);
                    break;
                case "gemma-regression-guidance":
                    CompleteRegressionGuidance(session, result, succeeded);
                    break;
                case "gemma-pattern-apply":
                    CompletePatternApply(session, result, succeeded);
                    break;
                case "claude-crypto-unaided":
                    CompleteCryptoUnaided(session, result, succeeded);
                    break;
                case "gemma-crypto-sme":
                    CompleteCryptoSme(session, result, succeeded);
                    break;
                case "claude-crypto-design":
                    CompleteCryptoDesign(session, result, succeeded);
                    break;
                case "claude-pattern-review":
                    CompletePatternReview(session, result, succeeded);
                    break;
                case "claude-regression-review":
                    CompleteRegressionReview(session, result, succeeded);
                    break;
                case "claude-tier2-scope":
                    CompleteTier2(session, result, succeeded);
                    break;
                case "claude-tier3-design":
                    CompleteTier3(session, result, succeeded);
                    break;
                case "gemma-consolidation-build":
                    CompleteConsolidationBuild(session, result, succeeded);
                    break;
                case "context-curve-plan":
                    CompleteCurveRun(session, result, succeeded);
                    break;
                case "claude-curve-judge":
                    CompleteCurveJudge(session, result, succeeded);
                    break;
                case "framing-arm":
                    CompleteFramingArm(session, result, succeeded);
                    break;
                case "framing-judge":
                    CompleteFramingJudge(session, result, succeeded);
                    break;
                case "ephemeral-test":
                    CompleteEphemeralTest(session, result, succeeded);
                    break;
                case "gemma-language-csharp":
                case "gemma-language-clara":
                    CompleteLanguageArm(session, result, succeeded);
                    MaybeQueueLanguageJudge(session);
                    break;
                case "claude-language-judge":
                    CompleteLanguageJudge(session, result, succeeded);
                    break;
                case "migration-arm":
                    CompleteMigrationArm(session, result, succeeded);
                    break;
                case "ticket-gate":
                    CompleteTicketGate(session, result, succeeded);
                    break;
                case "trap-hunt":
                    CompleteTrapHunt(session, result, succeeded);
                    break;
                case "trap-judge":
                    CompleteTrapJudge(session, result, succeeded);
                    break;
                case "drift-review":
                    CompleteDriftReview(session, result, succeeded);
                    break;
                case "grounded-change":
                    CompleteGroundedChange(session, result, succeeded);
                    break;
                case "grounded-change-audit":
                    CompleteGroundedAudit(session, result, succeeded);
                    break;
            }

            return true;
        }
    }

    private void CompleteCoach(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.CoachStatus = "failed";
            session.LastError = result.ErrorMessage ?? "The model task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out CoachResponse? response) ||
            response is null || !AreCoachSourcesValid(response))
        {
            session.CoachStatus = "failed";
            session.LastError = "Dynamic narration was rejected because its JSON or source paths were invalid.";
            return;
        }

        session.Coach = response;
        session.CoachModel = result.ModelUsed;
        session.CoachFallbackUsed = result.FallbackUsed;
        session.CoachStatus = "completed";
    }

    private static void CompleteReview(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        session.ReviewPartial = string.Empty;

        if (!succeeded)
        {
            session.ReviewStatus = "failed";
            session.LastError = result.ErrorMessage ?? "The model task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out ReviewResponse? response) || response is null)
        {
            session.ReviewStatus = "failed";
            session.LastError = "Claude review was rejected because it did not match the feedback contract.";
            return;
        }

        session.Review = response;
        session.ReviewModel = result.ModelUsed;
        session.ReviewStatus = "completed";
    }

    private void CompleteModelAnswer(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!session.ComparisonTaskSlot.TryGetValue(result.TaskId, out var slotKey) ||
            !session.ComparisonSlots.TryGetValue(slotKey, out var slot))
        {
            return;
        }

        slot.ModelUsed = result.ModelUsed;
        slot.FallbackUsed = result.FallbackUsed;
        slot.DurationMs = result.DurationMs;

        if (!succeeded)
        {
            slot.Status = "failed";
            slot.ErrorCode = result.ErrorCode;
            slot.Error = result.ErrorMessage ?? "The model returned no content and gave no reason.";
        }
        else
        {
            slot.Status = "completed";
            slot.Content = result.Content!.Trim();
            var (matched, coverage) = ScoreKeywords(session.ComparisonTopic!, slot.Content);
            slot.MatchedKeywords.Clear();
            slot.MatchedKeywords.AddRange(matched);
            slot.KeywordCoverage = coverage;
        }

        MaybeQueueJudge(session);
    }

    private static void CompleteJudge(SessionState session, BridgeTaskResult result, bool succeeded)
    {
        if (!succeeded)
        {
            session.ComparisonStatus = "failed";
            session.ComparisonError = result.ErrorMessage ?? "The judge task failed.";
            return;
        }

        if (!TryDeserialize(result.Content!, out JudgeDto? dto) || dto?.Scores is null || dto.Scores.Count == 0)
        {
            session.ComparisonStatus = "failed";
            session.ComparisonError = "The judge response did not match the scoring contract.";
            return;
        }

        var validSlots = session.ComparisonSlots.Keys.ToHashSet(StringComparer.Ordinal);
        var scores = dto.Scores
            .Where(score => score.Slot is not null && validSlots.Contains(score.Slot))
            .Select(score => new ComparisonDimensionScore(
                score.Slot!,
                ClampScore(score.Factuality),
                ClampScore(score.Completeness),
                ClampScore(score.Conciseness),
                score.Notes?.Trim() is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 400)] : null,
                score.Evidence ?? [],
                score.Unsupported ?? []))
            .ToList();

        if (scores.Count == 0)
        {
            session.ComparisonStatus = "failed";
            session.ComparisonError = "The judge scored no recognizable answers.";
            return;
        }

        var ranking = (dto.Ranking ?? [])
            .Where(entry => entry is not null && validSlots.Contains(entry))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        session.ComparisonVerdict = new ComparisonVerdict(
            result.ModelUsed ?? ClaraAuthorModel,
            scores,
            ranking,
            dto.Rationale?.Trim() ?? string.Empty,
            dto.CoverageNote?.Trim());
        session.ComparisonStatus = "completed";
    }

    private SessionState RequireSession(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new InvalidOperationException("Training session was not found.");

    private SessionState? FindByToken(string token) =>
        _sessions.Values.FirstOrDefault(session => FixedTimeEquals(session.BridgeToken, token));

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool TryDeserialize<T>(string content, out T? value)
    {
        value = default;
        var json = content.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                json = json[(firstLine + 1)..lastFence].Trim();
            }
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool AreCoachSourcesValid(CoachResponse response) =>
        response.Scenes.Count > 0 &&
        response.Scenes.All(scene => scene.SourcePaths.All(_fixture.AllowedPaths.Contains));

    private sealed class SessionState(string sessionId, string bridgeToken)
    {
        public string SessionId { get; } = sessionId;
        public string BridgeToken { get; } = bridgeToken;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastBridgeHeartbeat { get; set; }
        public string CoachStatus { get; set; } = "not-started";
        public string ReviewStatus { get; set; } = "not-started";
        public string? CoachModel { get; set; }
        public bool CoachFallbackUsed { get; set; }
        public CoachResponse? Coach { get; set; }
        public string? ReviewModel { get; set; }
        public ReviewResponse? Review { get; set; }
        public List<ReviewTraceEvent> ReviewTrace { get; } = [];

        // The step Claude is part-way through writing, so the UI can show progress between trace events.
        public string ReviewPartial { get; set; } = string.Empty;

        public string NarrationStatus { get; set; } = "idle";
        public string? NarrationText { get; set; }
        public string? NarrationModel { get; set; }
        public bool NarrationFallbackUsed { get; set; }
        public string? LastError { get; set; }
        public Dictionary<string, TaskState> Tasks { get; } = [];

        public string ComparisonStatus { get; set; } = "not-started";
        public ComparisonTopic? ComparisonTopic { get; set; }
        public Dictionary<string, ComparisonSlotState> ComparisonSlots { get; } = [];
        public Dictionary<string, string> ComparisonTaskSlot { get; } = [];
        public string? ComparisonJudgeTaskId { get; set; }
        public ComparisonVerdict? ComparisonVerdict { get; set; }
        public IReadOnlyList<ReferenceDocument> ComparisonSources { get; set; } = [];
        public int ComparisonGroundingTokens { get; set; }
        public bool ComparisonGroundingStarted { get; set; }
        public string? ComparisonError { get; set; }

        public string RoundTripStatus { get; set; } = "not-started";
        public string? RoundTripOriginal { get; set; }
        public string? RoundTripStory { get; set; }
        public string? RoundTripStoryModel { get; set; }
        public bool RoundTripStoryFallback { get; set; }
        public string? RoundTripRecreated { get; set; }
        public string? RoundTripRecreatedModel { get; set; }
        public bool RoundTripRecreatedFallback { get; set; }
        public RoundTripQa? RoundTripQa { get; set; }
        public string? RoundTripQaModel { get; set; }
        public string? RoundTripError { get; set; }

        public string ModernizeStatus { get; set; } = "not-started";
        public string? ModernizeLegacy { get; set; }
        public ModernizeResult? ModernizeResult { get; set; }
        public string? ModernizeModel { get; set; }
        public string? ModernizeError { get; set; }

        public string AuditStatus { get; set; } = "not-started";
        public string? AuditCode { get; set; }
        public AuditReport? AuditReport { get; set; }
        public AuditRecommendation? AuditRecommendation { get; set; }
        public string? AuditModel { get; set; }
        public string? AuditError { get; set; }

        public string ContextStatus { get; set; } = "not-started";
        public string? ContextCode { get; set; }
        public ContextAuditInitial? ContextInitial { get; set; }
        public string? ContextGemmaOverview { get; set; }
        public IReadOnlyList<string>? ContextGemmaGaps { get; set; }
        public string? ContextGemmaModel { get; set; }
        public ContextJudgeResult? ContextJudge { get; set; }
        public string? ContextJudgeModel { get; set; }
        public IReadOnlyList<string> ContextTraps { get; set; } = [];
        public string? ContextError { get; set; }

        public string ClaraStatus { get; set; } = "not-started";
        public string LanguageStatus { get; set; } = "not-started";
        public string MigrationStatus { get; set; } = "not-started";
        public string TicketStatus { get; set; } = "not-started";
        public string? TicketText { get; set; }
        public TicketPrecheck? TicketPrecheck { get; set; }
        public TicketReview? TicketReview { get; set; }
        public string? TicketError { get; set; }
        public string TrapStatus { get; set; } = "not-started";
        public string DriftStatus { get; set; } = "not-started";
        public string ChangeStatus { get; set; } = "not-started";
        public string? ChangeJira { get; set; }
        public string? ChangeCode { get; set; }
        public string? ChangeModel { get; set; }
        public int ChangePromptTokens { get; set; }
        public GroundedChangeAudit? ChangeAudit { get; set; }
        public string? ChangeError { get; set; }
        public string DriftCandidate { get; set; } = DriftSamples.DriftedPatch;
        public string DriftLabel { get; set; } = "drifted agent patch";
        public DriftSemantic? DriftSemantic { get; set; }
        public string? DriftError { get; set; }
        public string? TrapFindings { get; set; }
        public string? TrapFindingsModel { get; set; }
        public ContextJudgeResult? TrapGrade { get; set; }
        public string? TrapJudgeModel { get; set; }
        public string? TrapError { get; set; }
        public Dictionary<string, MigrationArmState> MigrationArms { get; } = [];
        public Dictionary<string, string> MigrationTaskArm { get; } = [];
        public CobolFacts? MigrationFacts { get; set; }
        public IReadOnlyList<CobolOracleRun> MigrationOracle { get; set; } = [];
        public string? MigrationError { get; set; }
        public string? LanguageJira { get; set; }
        public Dictionary<string, LanguageArmState> LanguageArms { get; } = [];
        public Dictionary<string, string> LanguageTaskArm { get; } = [];
        public string? LanguageJudgeTaskId { get; set; }
        public LanguageTestVerdict? LanguageVerdict { get; set; }
        public bool LanguageTokensExact { get; set; }
        public string? LanguageError { get; set; }
        public string? ClaraJira { get; set; }
        public string? ClaraSource { get; set; }
        public string? ClaraAuthorModel { get; set; }
        public ClaraProgramResult? ClaraResult { get; set; }
        public ClaraReview? ClaraReview { get; set; }
        public string? ClaraReviewModel { get; set; }
        public string? ClaraError { get; set; }

        public string SupportStatus { get; set; } = "not-started";
        public string? SupportRequirement { get; set; }
        public GeminiAdvice? SupportAdvice { get; set; }
        public string? SupportAdviceModel { get; set; }
        public SupportSynthesis? SupportSynthesis { get; set; }
        public string? SupportSynthesisModel { get; set; }
        public string? SupportError { get; set; }

        public string CurveStatus { get; set; } = "not-started";
        public Dictionary<(int Rung, int Attempt), CurveRunState> CurveRuns { get; } = [];
        public Dictionary<string, (int Rung, int Attempt)> CurveTaskKey { get; } = [];
        public Dictionary<int, int> CurveRungTokens { get; } = [];
        public TierDegradation? CurveAnalysis { get; set; }
        public CurveJudgeVerdict? CurveVerdict { get; set; }
        public string? CurveJudgeModel { get; set; }
        public string? CurveJudgeTaskId { get; set; }
        public bool CurveTokensExact { get; set; }
        public string? CurveError { get; set; }

        public string FramingStatus { get; set; } = "not-started";
        public string? FramingQuestion { get; set; }
        public Dictionary<string, FramingArmState> FramingArms { get; } = [];
        public Dictionary<string, string> FramingTaskArm { get; } = [];
        public string? FramingJudgeTaskId { get; set; }
        public FramingVerdict? FramingVerdict { get; set; }
        public string? FramingJudgeModel { get; set; }
        public string? FramingError { get; set; }

        public string EphemeralStatus { get; set; } = "not-started";
        public string? EphemeralRaw { get; set; }
        public DataTierAssessment? EphemeralGate { get; set; }
        public EphemeralRecord? EphemeralRecord { get; set; }
        public string? EphemeralPlan { get; set; }
        public string? EphemeralModel { get; set; }
        public string? EphemeralError { get; set; }

        public string SmeStatus { get; set; } = "not-started";
        public SmeUnaided? SmeUnaided { get; set; }
        public SmeConsultation? SmeConsultation { get; set; }
        public SmeDesign? SmeDesign { get; set; }
        public int SmePackTokens { get; set; }
        public string? SmeError { get; set; }

        public string PatternStatus { get; set; } = "not-started";
        public string? PatternPrompt { get; set; }
        public string? PatternSource { get; set; }
        public string? PatternRequest { get; set; }
        public string? PatternModifiedClass { get; set; }
        public string? PatternGemmaModel { get; set; }
        public PatternApplyReview? PatternReview { get; set; }
        public string? PatternError { get; set; }

        public string GuidanceStatus { get; set; } = "not-started";
        public MutationReport? GuidanceReport { get; set; }
        public string? GuidanceSource { get; set; }
        public string? GuidanceTests { get; set; }
        public MutationRemediation? GuidanceRemediation { get; set; }
        public MutationReview? GuidanceReview { get; set; }
        public string? GuidanceError { get; set; }

        public string TierStatus { get; set; } = "not-started";
        public Tier1Discovery? TierDiscovery { get; set; }
        public Tier2Scoping? TierScoping { get; set; }
        public List<ServiceDesign> TierDesigns { get; } = [];
        public Dictionary<string, (string Service, string Repository)> TierDesignTasks { get; } = [];
        public string? TierModel { get; set; }
        public string? TierError { get; set; }

        // A 31B model cannot reliably emit a whole multi-file library inside one JSON string, so the
        // build is split one task per module and the code comes back as plain text.
        public string ConsolidationStatus { get; set; } = "not-started";
        public ConsolidationDesign? ConsolidationDesign { get; set; }
        public string? ConsolidationDesignModel { get; set; }
        public string? ConsolidationBuildModel { get; set; }
        public List<ConsolidationFile> ConsolidationFiles { get; } = [];
        public List<string> ConsolidationAssumptions { get; } = [];
        public List<string> ConsolidationFailedModules { get; } = [];
        public Dictionary<string, string> ConsolidationTaskModule { get; } = [];
        public int ConsolidationModulesTotal { get; set; }
        public int ConsolidationDesignPromptTokens { get; set; }
        public int ConsolidationDesignOutputTokens { get; set; }
        public int ConsolidationBuildPromptTokens { get; set; }
        public int ConsolidationBuildOutputTokens { get; set; }
        public int ConsolidationCorpusTokens { get; set; }
        public bool ConsolidationTokensExact { get; set; }
        public string? ConsolidationError { get; set; }

        public TrainingSession Snapshot() => new(
            SessionId,
            BridgeToken,
            CreatedAt,
            LastBridgeHeartbeat,
            CoachStatus,
            ReviewStatus,
            CoachModel,
            CoachFallbackUsed,
            Coach,
            ReviewModel,
            Review,
            ReviewTrace.ToArray(),
            ReviewPartial,
            LastError,
            new ComparisonState(
                ComparisonStatus,
                ComparisonTopic?.Id,
                ComparisonTopic?.Title,
                ComparisonTopic?.Question,
                ComparisonSlots.Values
                    .OrderBy(slot => slot.Slot, StringComparer.Ordinal)
                    .Select(slot => new ComparisonAnswer(
                        slot.Slot,
                        slot.RequestedModel,
                        slot.ModelUsed,
                        slot.FallbackUsed,
                        slot.Status,
                        slot.Content,
                        slot.DurationMs,
                        slot.MatchedKeywords.ToArray(),
                        slot.KeywordCoverage,
                        slot.Error,
                        slot.ErrorCode))
                    .ToArray(),
                ComparisonVerdict,
                ComparisonSources,
                ComparisonGroundingTokens,
                ComparisonError),
            new RoundTripState(
                RoundTripStatus,
                RoundTripOriginal,
                RoundTripStory,
                RoundTripStoryModel,
                RoundTripStoryFallback,
                RoundTripRecreated,
                RoundTripRecreatedModel,
                RoundTripRecreatedFallback,
                RoundTripQa,
                RoundTripQaModel,
                RoundTripError),
            new ModernizeState(
                ModernizeStatus,
                ModernizeLegacy,
                ModernizeResult,
                ModernizeModel,
                ModernizeError),
            new AuditState(
                AuditStatus,
                AuditCode,
                AuditReport,
                AuditRecommendation,
                AuditModel,
                AuditError),
            new ContextAuditState(
                ContextStatus,
                ContextCode,
                ContextInitial,
                ContextGemmaOverview,
                ContextGemmaGaps ?? [],
                ContextGemmaModel,
                ContextJudge,
                ContextJudgeModel,
                ContextTraps,
                ContextError),
            new ClaraState(
                ClaraStatus,
                ClaraJira,
                ClaraSource,
                ClaraAuthorModel,
                ClaraResult,
                ClaraReview,
                ClaraReviewModel,
                ClaraError),
            new LanguageTestState(
                LanguageStatus,
                LanguageJira,
                LanguageTestSamples.CSharpSource,
                LanguageTestSamples.ClaraSource,
                CSharpSourceTokens,
                ClaraSourceTokens,
                ContextPackTokens,
                LanguageTokensExact || TokensAreExact
                    ? "counted with the Gemma tokenizer"
                    : "estimated at 4 characters per token (Gemma tokenizer unavailable)",
                LanguageArms.Values
                    .OrderBy(arm => arm.Arm, StringComparer.Ordinal)
                    .Select(arm => new LanguageTestArm(
                        arm.Arm,
                        arm.Language,
                        arm.Status,
                        arm.RequestedModel,
                        arm.ModelUsed,
                        arm.FallbackUsed,
                        arm.DurationMs,
                        arm.PromptTokens,
                        arm.Answer,
                        arm.Verification,
                        arm.Error))
                    .ToArray(),
                LanguageVerdict,
                LanguageStatus == "completed" ? LanguageTestSamples.SealedCriteria : [],
                LanguageStatus == "completed",
                LanguageError),
            new MigrationState(
                MigrationStatus,
                MigrationSamples.CobolSource,
                MigrationSamples.StructuralArtifacts,
                MigrationFacts?.Fields ?? [],
                MigrationFacts?.ControlFlow ?? [],
                MigrationOracle,
                ToolchainAvailable,
                ToolchainStatus,
                MigrationArms.Values
                    .OrderBy(arm => arm.Arm, StringComparer.Ordinal)
                    .Select(arm => new MigrationArm(
                        arm.Arm,
                        arm.Grounding,
                        arm.Status,
                        arm.RequestedModel,
                        arm.ModelUsed,
                        arm.DurationMs,
                        arm.PromptTokens,
                        arm.Code,
                        arm.Compiled,
                        arm.CompileErrors.ToArray(),
                        arm.Cases.ToArray(),
                        arm.Cases.Count(item => item.Matched),
                        arm.Error))
                    .ToArray(),
                MigrationError),
            new TicketGateState(
                TicketStatus,
                TicketText,
                TicketPrecheck,
                TicketReview,
                TicketError),
            new TrapHuntState(
                TrapStatus,
                GovernanceSamples.TerraformDesign,
                TrapFindings,
                TrapFindingsModel,
                TrapGrade,
                TrapJudgeModel,
                TrapStatus == "completed" ? GovernanceSamples.PlantedTraps : [],
                TrapError),
            new DriftState(
                DriftStatus,
                DriftSamples.GoldenTicket,
                DriftSamples.FrozenCode,
                DriftSamples.HumanPatch,
                DriftCandidate,
                DriftLabel,
                DriftSamples.ScoreStructure(DriftCandidate),
                DriftSemantic,
                DriftError),
            new GroundedChangeState(
                ChangeStatus,
                ChangeJira ?? FixtureJira,
                FixtureArtifacts,
                ChangeCode,
                ChangeModel,
                ChangePromptTokens,
                ChangeAudit,
                ChangeError),
            new LlmSupportState(
                SupportStatus,
                SupportRequirement,
                SupportAdvice,
                SupportAdviceModel,
                SupportSynthesis,
                SupportSynthesisModel,
                SupportError),
            new ContextCurveState(
                CurveStatus,
                CurveRunsPerTier,
                BuildCurveTiers(this),
                CurveAnalysis,
                CurveVerdict,
                CurveJudgeModel,
                CurveTokensExact || TokensAreExact,
                CurveError),
            new FramingState(
                FramingStatus,
                FramingQuestion,
                FramingArms.Values
                    .OrderBy(arm => Array.FindIndex(FramingArms.Keys.ToArray(), key => key == arm.Id))
                    .Select(arm => new FramingArm(
                        arm.Id,
                        arm.Label,
                        arm.Framing,
                        arm.Status,
                        arm.Answer,
                        arm.ModelUsed,
                        arm.PromptTokens,
                        arm.SimilarityToNeutral,
                        arm.Error))
                    .ToArray(),
                FramingVerdict,
                FramingJudgeModel,
                FramingError),
            new EphemeralState(
                EphemeralStatus,
                EphemeralRaw,
                EphemeralGate,
                EphemeralRecord,
                EphemeralPlan,
                EphemeralModel,
                EphemeralError),
            new ConsolidationState(
                ConsolidationStatus,
                ConsolidationDesign,
                ConsolidationDesignModel,
                ConsolidationModulesTotal == 0
                    ? null
                    : new ConsolidationBuild(
                        ConsolidationFiles.ToArray(),
                        ConsolidationAssumptions.ToArray(),
                        ConsolidationModulesTotal,
                        ConsolidationFiles.Count + ConsolidationFailedModules.Count,
                        ConsolidationFailedModules.ToArray()),
                ConsolidationBuildModel,
                ConsolidationDesign is null
                    ? null
                    : new ConsolidationTokens(
                        ConsolidationDesignPromptTokens,
                        ConsolidationDesignOutputTokens,
                        ConsolidationBuildPromptTokens,
                        ConsolidationBuildOutputTokens,
                        ConsolidationCorpusTokens,
                        ConsolidationTokensExact),
                ConsolidationError),
            new PortfolioTierState(
                TierStatus,
                TierDiscovery,
                TierScoping,
                TierDesigns.ToArray(),
                TierModel,
                null,
                TierError),
            new RegressionGuidanceState(
                GuidanceStatus,
                GuidanceRemediation,
                GuidanceReview,
                GuidanceError),
            new PatternRunState(
                PatternStatus,
                PatternModifiedClass,
                PatternGemmaModel,
                PatternReview,
                PatternError),
            new CryptoSmeState(
                SmeStatus,
                SmeUnaided,
                SmeConsultation,
                SmeDesign,
                SmeUnaided is null
                    ? []
                    : CryptoSmeCorpus.Trace(
                        SmeUnaided.Attempt,
                        string.Join(" ", SmeUnaided.Questions),
                        SmeConsultation is null
                            ? null
                            : string.Join(" ", SmeConsultation.Answers.Select(answer => answer.Answer))
                              + " " + string.Join(" ", SmeConsultation.Corrections)
                              + " " + string.Join(" ", SmeConsultation.Unprompted),
                        SmeDesign is null
                            ? null
                            : SmeDesign.Summary + " " + string.Join(" ", SmeDesign.Steps.Select(step => step.Step + " " + step.Detail))),
                SmeError),
            new NarrationState(NarrationStatus, NarrationText, NarrationModel, NarrationFallbackUsed));
    }

    private sealed class CurveRunState(int rung, int attempt)
    {
        public int Rung { get; } = rung;
        public int Attempt { get; } = attempt;
        public string Status { get; set; } = "pending";
        public int Score { get; set; }
        public List<string> Met { get; set; } = [];
        public List<string> Missed { get; set; } = [];
        public string? Plan { get; set; }
        public string? ModelUsed { get; set; }
        public string? Error { get; set; }
    }

    private sealed class FramingArmState(string id, string label, string framing)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public string Framing { get; } = framing;
        public string Status { get; set; } = "pending";
        public string? Answer { get; set; }
        public string? ModelUsed { get; set; }
        public int PromptTokens { get; set; }
        public double SimilarityToNeutral { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ComparisonSlotState(string slot, string requestedModel)
    {
        public string Slot { get; } = slot;
        public string RequestedModel { get; } = requestedModel;
        public string? TaskId { get; init; }
        public string Status { get; set; } = "pending";
        public string? ModelUsed { get; set; }
        public bool FallbackUsed { get; set; }
        public string? Content { get; set; }
        public long DurationMs { get; set; }
        public List<string> MatchedKeywords { get; } = [];
        public int KeywordCoverage { get; set; }
        public string? Error { get; set; }
        public string? ErrorCode { get; set; }
    }

    private sealed record JudgeDto(List<JudgeScoreDto>? Scores, List<string>? Ranking, string? Rationale, string? CoverageNote);

    private sealed record JudgeScoreDto(string? Slot, int Factuality, int Completeness, int Conciseness, string? Notes, List<string>? Evidence, List<string>? Unsupported);

    private sealed record RoundTripQaDto(int Fidelity, string? Summary, List<string>? Preserved, List<string>? Gaps, List<string>? Risks, string? Recommendation);

    private sealed record ModernizeDto(string? ModernizedCode, string? Explanation, List<string>? BusinessRules, List<string>? MicroserviceCandidates);

    private sealed record AuditRecommendationDto(string? Summary, List<string>? Strengths, List<string>? Priorities, List<string>? AgenticReadiness);

    private sealed record ContextGemmaDto(string? Overview, List<string>? Gaps);

    private sealed record ContextJudgeDto(int TrapsFound, List<string>? Matched, List<string>? Missed, List<string>? FalsePositives, string? Verdict);

    private sealed record ClaraReviewDto(int Fidelity, string? Verdict, List<string>? Strengths, List<string>? Issues, List<string>? LanguageNotes);

    private sealed record GeminiAdviceDto(string? Overview, List<string>? Services, List<string>? Constraints, List<string>? Citations);

    private sealed record TierCandidateDto(string? Service, string? AmpId, string? Why);

    private sealed record ProposedTestDto(string? Name, string? TargetsSurvivor, string? Behaviour, string? Assertion);

    private sealed record PatternReviewDto(
        int Fidelity,
        string? Verdict,
        List<string>? Followed,
        List<string>? Ignored,
        List<string>? Risks,
        string? PromptRecommendation);

    private sealed record SmeUnaidedDto(string? Attempt, List<string>? Uncertain, List<string>? Questions);

    private sealed record SmeAnswerDto(string? Question, string? Answer, string? Citation, bool InPack);

    private sealed record SmeConsultDto(List<SmeAnswerDto>? Answers, List<string>? Corrections, List<string>? Unprompted);

    private sealed record SmeStepDto(string? Step, string? Detail, string? Source);

    private sealed record SmeDesignDto(string? Summary, List<SmeStepDto>? Steps, List<string>? Risks, List<string>? OpenQuestions);

    private sealed record GuidanceDto(string? Summary, List<ProposedTestDto>? Tests, List<string>? NotWorthTesting);

    private sealed record TestAssessmentDto(string? Name, bool WouldKill, string? Reasoning);

    private sealed record GuidanceReviewDto(
        string? Verdict,
        List<TestAssessmentDto>? Assessments,
        List<string>? Gaps,
        List<string>? Overreach,
        string? NextStep);

    private sealed record Tier1Dto(List<TierCandidateDto>? Candidates, List<string>? Excluded, List<string>? CannotDetermineYet, string? Summary);

    private sealed record TierDecisionDto(string? Service, bool InScope, string? Reason, string? Evidence);

    private sealed record Tier2Dto(List<TierDecisionDto>? Decisions, string? SharedLibraryVerdict, string? Summary);

    private sealed record TierChangeDto(string? File, string? Change, string? Justification);

    private sealed record Tier3Dto(string? Summary, List<TierChangeDto>? Changes, List<string>? Unchanged, List<string>? Risks, List<string>? OpenQuestions);

    private sealed record TierJudgementDto(string? Tier, int Correctness, int Grounding, string? Assessment, List<string>? Errors);

    private sealed record CurveJudgeDto(
        string? Summary,
        List<TierJudgementDto>? Tiers,
        List<string>? DegradationEvidence,
        string? MinimumViableContext,
        string? Caveat);

    private sealed record ConsolidationModuleDto(string? Name, string? Responsibility, string? PublicApi, List<string>? Replaces);

    private sealed record ConsolidationDesignDto(
        string? LibraryName,
        string? Summary,
        List<ConsolidationModuleDto>? Modules,
        List<string>? MigrationSteps,
        List<string>? Risks,
        List<string>? OutOfScope,
        string? ImplementationSpec);

    private sealed record SupportStepDto(string? Step, string? Detail, string? Source);

    private sealed record FramingVerdictDto(bool SubstanceChanged, string? Summary, List<string>? Differences, string? Recommendation);

    private sealed record SupportSynthesisDto(
        List<string>? AlreadyKnew,
        List<string>? LearnedFromGemini,
        List<string>? NeedsVerification,
        string? DesignSummary,
        List<SupportStepDto>? DesignSteps,
        string? ProvenanceNote);

    private sealed record LanguageScoreDto(int Arm, int Criterion, bool Met, string? Evidence);

    private sealed record TicketFindingDto(string? Criterion, bool Met, string? Note);

    private sealed record DriftReviewDto(
        int GoalFidelity,
        int ScopeDiscipline,
        List<string>? Deviations,
        string? Verdict);

    private sealed record GroundedCitationDto(string? Change, string? Document, string? Statement, bool Grounded);

    private sealed record GroundedAuditDto(
        List<GroundedCitationDto>? Citations,
        List<string>? Ungrounded,
        bool OpenQuestionRaised,
        string? OpenQuestionNote,
        bool PlaceholdersMarked,
        string? PlaceholderNote,
        string? Verdict);

    private sealed record TicketReviewDto(
        int Score,
        bool Passed,
        List<TicketFindingDto>? Findings,
        string? Rewritten,
        string? Verdict);

    private sealed record LanguageVerdictDto(
        List<LanguageScoreDto>? Scores,
        string? Winner,
        string? Rationale,
        List<string>? LocalKnowledgeMissed,
        List<string>? Caveats);

    private sealed class LanguageArmState(string arm, string language, string requestedModel)
    {
        public string Arm { get; } = arm;
        public string Language { get; } = language;
        public string RequestedModel { get; } = requestedModel;
        public string TaskId { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? ModelUsed { get; set; }
        public bool FallbackUsed { get; set; }
        public long DurationMs { get; set; }
        public int PromptTokens { get; set; }
        public string? Answer { get; set; }
        public ClaraProgramResult? Verification { get; set; }
        public string? Error { get; set; }
    }

    private sealed class MigrationArmState(string arm, string grounding, string requestedModel)
    {
        public string Arm { get; } = arm;
        public string Grounding { get; } = grounding;
        public string RequestedModel { get; } = requestedModel;
        public string TaskId { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? ModelUsed { get; set; }
        public long DurationMs { get; set; }
        public int PromptTokens { get; set; }
        public string? Code { get; set; }
        public bool Compiled { get; set; }
        public List<string> CompileErrors { get; } = [];
        public List<MigrationCaseResult> Cases { get; } = [];
        public string? Error { get; set; }
    }

    private sealed class TaskState(BridgeTask task)
    {
        public BridgeTask Task { get; } = task;
        public string Status { get; set; } = "queued";
        public DateTimeOffset? ClaimedAt { get; set; }
    }

    private static class Prompts
    {
        public const string AutopilotSystem = """
            You are narrating a live walkthrough of an AI training application for an audience watching a screen.

            You are given a list of FACTS. Those facts are the only things you may assert. If something is not
            in the list, you do not know it and must not say it. You may be given OBSERVED VALUES, which are
            real measured results; you may quote those numbers exactly as given and must not round or reinterpret
            them.

            Rules, in priority order:
            1. Never state a fact that was not supplied. Never invent a number, a file name, or a model name.
            2. Never claim a result is correct, validated, proven, or industry-leading.
            3. If the supplied values describe a failure, say so plainly. Do not soften it or explain it away.
            4. Honour every entry in YOU MUST NOT CLAIM exactly.
            5. Plain spoken English, second person or neutral. No marketing language. No exclamation marks.
            6. Respect the word limit.

            Return JSON only, no Markdown fences: {"narration":"..."}
            """;

        public const string CoachSystem = """
            You are the grounded coach for a synthetic Public-data OKF lesson. Use only the fixture below.
            The fixture README defines Open Knowledge Format for models that do not already know it. Apply that
            definition; do not substitute a different meaning for OKF.
            Return JSON only with lessonTitle, scenes, and openQuestions. Every scene must have sceneId,
            narration, and sourcePaths. Valid scene IDs: why-okf, artifact-tree, jira-trace, animation-demo,
            image-demo, knowledge-check, prompt-challenge. Cite only exact fixture paths. Explain observable evidence, never hidden reasoning.
            Keep each narration under 75 words and do not invent metrics, files, policies, or API field names.
            """;

        public const string ReviewSystem = """
            You are an independent coaching reviewer. Review only the learner prompt against the supplied
            synthetic fixture. The fixture README defines Open Knowledge Format; read and apply it before reviewing.
            synthetic rubric. Stream newline-delimited JSON (NDJSON), exactly one compact JSON object per line.
            First emit 3-6 trace records shaped as:
            {"type":"trace","sequence":1,"stage":"Index-first routing","evidence":"exact learner quote or 'not present'","decision":"what the evidence supports or what is missing"}
            Use increasing sequence values. These records are an observable decision summary, not private chain-of-thought.
            End with exactly one review record shaped as:
            {"type":"review","summary":"...","strengths":["..."],"improvements":["..."],"unsupportedAssumptions":["..."],"suggestedPrompt":"...","evidenceQuotes":["..."]}
            Do not use Markdown fences, numeric scores, or pass/fail. Quote exact learner text as evidence.
            Treat the result as coaching commentary, not a validated grade.
            """;

        public const string AnswerSystem = """
            You are a Google Cloud technical expert answering one question for a practitioner.
            Give a correct, well-structured, and concise answer using only your own knowledge.
            Do not fabricate service names, flags, or version numbers. If uncertain, say so plainly.
            Return prose only; do not add a preamble such as "Sure" or "As an AI".
            """;

        // The documentation in the prompt was actually retrieved over the network, so the judge can be held
        // to it. Telling a model it has sources it cannot read is what produced confident scoring from memory.
        public const string JudgeSystem = """
            You are an impartial evaluator comparing anonymized answers (A, B, C) to one technical Google Cloud
            question. The candidate models answered from their own knowledge with no documentation supplied.

            You have been given the actual text of the official Google Cloud documentation, retrieved at judging
            time. That text is your ONLY source of ground truth. Do not score from your own recollection of
            Google Cloud, and do not add your own answer.

            For every factuality judgement, quote the specific sentence or phrase from the supplied
            documentation that supports it. If a claim in an answer is not addressed by the supplied
            documentation, do NOT mark it wrong and do NOT mark it right — record it under "unsupported".
            An answer that is correct but unverifiable from these documents is not the same as one that
            contradicts them, and you must keep the two apart.

            Penalise heavily any statement that the documentation contradicts. Never reward length or style.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"scores":[{"slot":"A","factuality":1,"completeness":1,"conciseness":1,
                        "notes":"short","evidence":["quoted doc text supporting the score"],
                        "unsupported":["claim the docs neither confirm nor deny"]}],
             "ranking":["A","B","C"],"rationale":"2-3 sentences",
             "coverageNote":"what the supplied documentation did not cover, limiting this evaluation"}
            Each dimension is an integer from 1 (poor) to 5 (excellent). ranking lists slots best-first.
            """;

        public const string RoundTripStorySystem = """
            You are a senior engineer writing a backlog item. Read the source class and produce ONE JIRA story
            that captures its externally observable behavior and business rules. Include a title, the
            "As a / I want / So that" narrative, and a numbered Acceptance Criteria list a developer could
            implement against. State the rules explicitly (limits, rates, rounding, edge cases, defaults).
            Do NOT paste or quote the source code, and do NOT invent requirements the code does not support.
            Return the story as plain text.
            """;

        public const string RoundTripRecreateSystem = """
            You are a software engineer. Implement a single class that satisfies ONLY the following JIRA story.
            Infer a reasonable API from the story and prefer C# unless the story clearly implies another language.
            Do not assume rules the story does not state. Return only the code for one class — no explanation,
            no surrounding prose. A single Markdown code fence is acceptable.
            """;

        public const string RoundTripQaSystem = """
            You are an independent QA reviewer comparing an ORIGINAL class to a RE-CREATED class that was
            produced solely from a JIRA story (a code to story to code round trip). Judge behavioral fidelity
            and business-rule preservation, not formatting, naming, or language differences.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"fidelity":1,"summary":"...","preserved":["..."],"gaps":["..."],"risks":["..."],"recommendation":"..."}
            fidelity is an integer 1-5 where 5 means behaviorally equivalent and 1 means major divergence.
            "gaps" are behaviors or rules lost or changed. This is coaching commentary, not a validated grade.
            """;

        public const string ModernizeSystem = """
            You are a senior software engineer performing a behavior-preserving modernization of legacy
            procedural code. Refactor it into clean, modern, well-named code that separates concerns
            (calculation vs I/O vs configuration), replaces magic numbers with named rules, and introduces
            clear types where useful. Do NOT change observable behavior. Keep the same language as the input.
            The primary goal is to EXPOSE the business logic so it can be tested and later split into services.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"modernizedCode":"...","explanation":"...","businessRules":["..."],"microserviceCandidates":["..."]}
            modernizedCode is the full refactored source. explanation is plain language. businessRules lists the
            rules you surfaced. microserviceCandidates lists seams or bounded contexts that could become services.
            This is a teaching illustration, not a validated migration.
            """;

        public const string AuditSystem = """
            You are a code-audit reviewer assessing whether a C# codebase is "agentic-ready" — easy for an AI
            agent and a junior developer to understand and change safely. You are given a DETERMINISTIC static
            analysis report (scores, metrics, findings, and a type dependency graph) produced by Roslyn.
            The numbers are authoritative: cite them, never invent or change them. Ground every claim in the report.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"summary":"...","strengths":["..."],"priorities":["..."],"agenticReadiness":["..."]}
            summary interprets the overall score in one or two sentences. strengths lists what is already good.
            priorities lists the highest-impact fixes in order. agenticReadiness lists concrete steps to make the
            code easier for agents (expose business rules, add tests, document the public contract, clarify seams).
            This is coaching commentary, not a validated grade.
            """;

        public const string ContextAuditSystem = """
            You are auditing whether CODE is consistent with the CONTEXT that documents it (OKF concepts,
            rules, ADRs). Use the context as the source of truth and find every place the code contradicts
            or fails to implement what the context states. Do not invent rules that are not in the context.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"overview":"...","gaps":["..."]}
            overview is 1-2 sentences on whether the code honors its documented context. gaps lists each
            specific inconsistency you found, quoting the rule and the conflicting code.
            """;

        public const string ContextJudgeSystem = """
            You are an impartial grader. You are given code+context, the KNOWN planted discrepancies
            (ground truth), and another model's findings. Decide how many of the known discrepancies that
            model actually identified. Match on meaning, not wording. Do not credit vague or incorrect claims.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"trapsFound":0,"matched":["..."],"missed":["..."],"falsePositives":["..."],"verdict":"..."}
            trapsFound is the count of known discrepancies correctly identified. matched lists them; missed
            lists the ones not found; falsePositives lists claims that are not real discrepancies. verdict is
            1-2 sentences on how well the context enabled the audit. Coaching commentary, not a validated grade.
            """;

        public const string ClaraAuthorSystem = """
            You are the language designer and author for CLARA (Clear Language for Auditable Rules & Arithmetic),
            a small deterministic business-rule language designed to be unambiguous to an LLM and a human.
            Translate the requirement into ONE CLARA policy. Return ONLY CLARA source — no Markdown fences, no prose.

            CLARA grammar (line-oriented; '#' or '//' starts a comment):
              policy <Name>
              inputs:
                <name>: <money|percent|number|integer|boolean>
              constants:
                <name>: <type> = <literal-expression>
              rule <RuleName>:
                when <boolean-expression>      # or the single word: otherwise
                then <outputName> = <expression>
              output:
                <name>: <type>
              examples:
                example "<description>":
                  <inputName> = <literal>
                  expect <outputName> = <literal>

            Semantics the compiler enforces — violating any of these is a compile error:
              - Rules form an ORDERED first-match decision table: the first rule whose 'when' is true fires and
                assigns its 'then' outputs, then evaluation stops. Finish with exactly one 'otherwise' rule and
                put nothing after it.
              - EVERY rule must assign EVERY declared output, so no path can leave a value undefined.
              - Types are strict. money +/- money = money; money * percent = money; money * number = money;
                money / number = money; money / money = number. You may not add money to a plain number, write
                'then fee = 0' for a money output (write $0.00), multiply money by money, multiply percent by
                percent, or compare money with a plain number.
              - Literals: money $250.00 (negative $-50.00), percent 1.5%, integers 3, numbers 2.5, true/false.
              - Functions: min(a, b, ...), max(...), abs(x), floor(x), ceil(x), and round(value, decimals) which
                is half-up, or round(value, decimals, half_even) for banker's rounding. The decimals argument
                must be a whole-number literal or constant. Round every money result explicitly.
              - Operators: + - * /, comparisons = != < <= > >=, and / or / not. Inputs and constants are
                read-only; outputs cannot be read inside a rule.
              - Reserved words (policy, rule, when, then, otherwise, round, min, max, money, percent, number,
                integer, boolean, ...) cannot be used as names.
            Provide 3-6 examples covering the edge cases. Every example must give a value for EVERY input and
            state an 'expect' for EVERY output.
            """;

        public const string ClaraReviewSystem = """
            You are an independent reviewer (a different model than the author). You are given a requirement, a
            CLARA policy, the compiler's diagnostics, and the deterministic execution results of its examples
            including which rule fired for each. Assess whether the policy faithfully and unambiguously encodes
            every business rule, and evaluate CLARA as an LLM-legible, auditable business-logic language versus
            writing the same logic in C#/Java.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"fidelity":1,"verdict":"...","strengths":["..."],"issues":["..."],"languageNotes":["..."]}
            fidelity is an integer 1-5 for how completely the policy captures the requirement (5 = complete and
            correct). issues lists missing/incorrect rules or ambiguities. languageNotes assess CLARA itself.
            Coaching commentary, not a validated grade.
            """;

        // Identical for every tier. The only thing that varies between runs is the repository material,
        // so any difference in the plans is attributable to context.
        public const string ContextTierSystem = """
            You are a senior engineer picking up a ticket on a repository you have been given material about.
            Do NOT write code. Produce a markdown implementation plan describing the change you would make.

            Work only from the ticket and the repository material supplied. Do not assume the repository
            contains anything you were not shown. If a fact you need is missing, say so plainly and list it
            as an open question — never invent a file name, an attribute name, a formula, or a rule.

            Structure the answer with these markdown headings:
            ## Change
            Numbered steps: the file, the method, and precisely what you would change and why.
            ## Rules applied
            Every business or regulatory rule your change depends on, and where in the supplied material you
            found it. If you are relying on general knowledge rather than the material, say so explicitly.
            ## Impact
            What else this touches, and what must stay unchanged.
            ## Tests
            The specific cases that would prove the change.
            ## Open questions
            Anything you could not determine from the material supplied.

            Keep it under 600 words.
            """;

        // The scores are already computed deterministically. The judge supplies reasoning, not the number.
        public const string CurveJudgeSystem = """
            You are a principal engineer reviewing several implementation plans for the same ticket on the
            same codebase. Each plan was produced by the SAME model with the SAME prompt; only the repository
            documentation supplied to it differed. You are given the ground truth for the defect.

            A deterministic keyword score accompanies each plan. That score is not yours and you must not
            restate it as your finding — judge the engineering substance yourself, and say so if your reading
            disagrees with the score.

            Be specific about harm: a plan that confidently states a WRONG formula is worse than one that
            admits it does not know, even if both score similarly. Call that out.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"summary":"...",
             "tiers":[{"tier":"documented|partial|undocumented","correctness":0,"grounding":0,
                       "assessment":"...","errors":["..."]}],
             "degradationEvidence":["..."],"minimumViableContext":"...","caveat":"..."}

            correctness: 0-5, how close the plan is to the ground-truth fix.
            grounding: 0-5, how well claims are tied to supplied material rather than asserted.
            errors: concrete factual mistakes or inventions in that plan, quoted where possible.
            degradationEvidence: what specifically was lost between tiers, as observable differences.
            minimumViableContext: the least documentation a team must maintain for this class of change.
            caveat: what this single ticket on one repository does NOT establish.
            """;

        public const string FramingArmSystem = """
            You are a senior engineer answering a colleague's technical question. Answer the question you are
            actually asked, in under 250 words. Be direct about trade-offs and state your recommendation.
            """;

        public const string EphemeralTestSystem = """
            You are reproducing a defect from an ephemeral test ticket. The ticket has already passed a data
            gate, so sensitive values have been replaced with redaction markers such as [EMAIL_REDACTED].
            Treat those markers as opaque placeholders: the reproduction must not depend on their real values,
            and you must never ask for them. If a redacted value would genuinely change the outcome, say so
            explicitly instead of guessing.
            Return a short reproduction plan: the steps to run, what to observe, and what would prove the defect.
            Keep it under 250 words and return prose only.
            """;

        public const string FramingJudgeSystem = """
            You are an impartial reviewer. You are given one question and the same model's answers to three
            differently-framed versions of it. Decide whether the framing changed the SUBSTANCE of the answer —
            the recommendation, the risks named, or the caveats — as opposed to only its tone.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"substanceChanged":true,"summary":"...","differences":["..."],"recommendation":"..."}
            differences lists concrete substantive divergences between the framings. recommendation states how
            an engineer should phrase such a question in future. Coaching commentary, not a validated grade.
            """;

        public const string GeminiAdvisorSystem = """
            You are acting as a Google Cloud retrieval and gap-finding instrument, not as the designer.
            Your job is to surface the Google-specific services, platform constraints, quotas, and configuration
            prerequisites the requirement depends on, and to point at the authoritative documentation for each.
            Prefer precise, current Google Cloud product names and exact configuration terms over generic cloud
            advice. Do not design the whole solution and do not invent URLs; if you are unsure of a fact or a
            link, say so in the constraint text instead of guessing.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"overview":"...","services":["..."],"constraints":["..."],"citations":["https://cloud.google.com/..."]}
            overview is 2-3 sentences. services names the GCP services with the specific feature that matters.
            constraints lists platform behaviors, limits, or prerequisites that materially change the design.
            citations lists official Google Cloud documentation URLs supporting the above.
            """;

        public const string ClaudeSynthesisSystem = """
            You are the designer. You are given a requirement and an advisory from a Google-specialist model.
            Treat that advisory as an UNVERIFIED MODEL CLAIM, not ground truth: it may be current where your own
            knowledge is stale, and it may also be wrong. Your value here is provenance discipline.
            Be honest and specific about your own knowledge boundary — do not pretend to have known something you
            did not, and do not accept a specific claim (exact quotas, limits, API names, availability) merely
            because the advisory asserted it.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"alreadyKnew":["..."],"learnedFromGemini":["..."],"needsVerification":["..."],"designSummary":"...",
             "designSteps":[{"step":"...","detail":"...","source":"own|gemini|unverified"}],"provenanceNote":"..."}
            alreadyKnew: facts you could have supplied without the advisory.
            learnedFromGemini: things the advisory told you that you did not know or would have stated less precisely.
            needsVerification: specific claims you refuse to rely on until checked against the cited primary docs.
            designSteps: the migration design; tag each step's source as own, gemini (advisory-sourced), or
            unverified (advisory-sourced and still unconfirmed).
            provenanceNote: one or two sentences on what a human must verify before this design is trusted.
            """;

        // Asking for the questions in the same call is what makes the next stage possible. A model that
        // cannot say what it does not know cannot delegate.
        public const string CryptoUnaidedSystem = """
            You are a senior engineer given a ticket in a domain you may not work in daily. You have NO
            reference documentation and no ability to look anything up.

            Answer from your own knowledge, and be scrupulously honest about its edges. Where you would
            normally reach for the vendor documentation, say so rather than producing a confident
            approximation. Exact header names, exact parameter defaults, exact error semantics and exact
            filter rules are the things most often remembered approximately.

            Then list the specific questions you would put to a subject-matter expert. Make them precise
            enough to be answered with a fact, not a discussion.

            Return JSON only, no Markdown fences:
            {"attempt":"your best design attempt, 250-400 words",
             "uncertain":["a specific claim you are not confident is exactly right"],
             "questions":["a precise question for the SME"]}
            """;

        // The SME is grounded but is still a model. Requiring a citation per answer, and an explicit
        // in-pack flag, is what stops it filling gaps from its own priors and calling that grounding.
        public const string CryptoSmeSystem = """
            You are a subject-matter expert on crypto exchange connectivity. You have been given a
            grounding pack assembled from vendor documentation. That pack is your ONLY source.

            Answer each question directly and concretely: exact header names including any interval
            suffix, exact defaults, exact error codes, exact filter names.

            For every answer, cite the layer and the specific line of the pack that supports it. If the
            pack does not cover a question, set inPack to false, say plainly that the pack does not
            answer it, and do NOT substitute your own recollection.

            Then review the peer attempt supplied. List anything in it that the pack CONTRADICTS, quoting
            the pack. Do not list stylistic differences — only factual corrections.

            Finally — and this matters more than the answers — scan the pack for facts that are MATERIAL to
            the ticket and that NOBODY ASKED ABOUT. A subagent that only answers the questions put to it
            limits the design to what the asker already suspected. List those unprompted facts with their
            citation. If the peer's questions covered the pack well, say so rather than padding the list.

            Return JSON only, no Markdown fences:
            {"answers":[{"question":"...","answer":"...","citation":"LAYER n — the supporting line","inPack":true}],
             "corrections":["what the peer attempt got wrong, and what the pack says instead"],
             "unprompted":["a material pack fact nobody asked about, with its citation"]}
            """;

        public const string CryptoDesignSystem = """
            You are the designer. You produced an earlier attempt without documentation, and you have now
            consulted a subject-matter expert subagent that was grounded on vendor documentation.

            Produce the execution adapter design. Tag every step with its source:
              own        — you knew this without the SME
              sme        — the SME supplied it and it is cited to the pack
              unverified — the SME could not support it from the pack, or you are extrapolating

            Do not quietly absorb the SME's answers as your own. The provenance is the deliverable as much
            as the design is: a reader must be able to see which decisions depend on a source that has not
            been checked against the primary documentation.

            Where the SME corrected your earlier attempt, reflect the correction rather than defending the
            original.

            The SME may have volunteered facts you did not ask about. Treat those as the most valuable part
            of the consultation — they are the gaps you did not know you had — and fold each one into the
            design or state explicitly why it does not apply.

            Return JSON only, no Markdown fences:
            {"summary":"...","steps":[{"step":"...","detail":"...","source":"own|sme|unverified"}],
             "risks":["..."],"openQuestions":["what a human must verify against the vendor docs"]}
            """;

        // The subject under review is the PROMPT, not the model. A weak result from a strong model is
        // evidence the shared prompt is weak, which is the whole reason for executing a contribution.
        public const string PatternReviewSystem = """
            You are reviewing a contribution to a shared prompt library. A smaller model was given the
            contributed prompt as its only instruction, plus a change request and a class, and produced the
            output you are shown.

            Judge the PROMPT by what it caused. Ask:
            - Did the output follow each instruction the prompt actually gave?
            - Where the output is poor, is that the prompt's fault or the model's?
            - Did the prompt cause anything harmful — unrequested refactoring, silently repaired defects,
              invented requirements?

            Read the original class carefully before judging. If its documentation asserts a rule the code
            does not implement, note whether the output surfaced that or built on top of it.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"fidelity":0,"verdict":"2-3 sentences on whether this prompt is worth sharing",
             "followed":["instruction the output honoured"],
             "ignored":["instruction the output skipped or misread"],
             "risks":["what this prompt would do badly at scale"],
             "promptRecommendation":"the specific wording change that would fix the weakest instruction"}
            fidelity is 0-5: how faithfully the output followed the contributed prompt.
            """;

        // A surviving mutant is a falsifiable target: either the proposed test fails against the mutated
        // code or it does not. "Improve the tests" would not be checkable.
        public const string RegressionGuidanceSystem = """
            You are a test engineer reading a mutation-testing report. A mutation test compiles the code,
            injects one small deliberate defect at a time, and re-runs the suite. A defect the suite does
            not notice is called a SURVIVING MUTANT — it is a real behaviour change the tests accepted.

            Your job is to close the named survivors. For each one, propose the test that would fail if
            that specific defect were present. Be concrete: name the test, state the input, and state the
            assertion precisely enough that someone could write it without asking you a question.

            Do not propose tests for mutants that were already caught. Do not propose broad "add more
            coverage" advice. If a survivor is genuinely not worth a test — an equivalent mutant that does
            not change observable behaviour, or a boundary the business does not care about — say so under
            notWorthTesting and explain why, rather than inventing a test to look thorough.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"summary":"...",
             "tests":[{"name":"...","targetsSurvivor":"line N — the defect it kills",
                       "behaviour":"the input and scenario","assertion":"the exact expected value"}],
             "notWorthTesting":["survivor — why a test is not warranted"]}
            """;

        public const string RegressionReviewSystem = """
            You are a principal engineer reviewing another engineer's proposed tests. You are given the
            surviving mutants from a mutation-testing run, the code under test, and the proposals.

            For each proposed test, answer one falsifiable question: would this test FAIL if that specific
            mutation were applied? Reason it through against the actual code — trace the input to the
            asserted value. A test that exercises the right method but asserts something the mutation does
            not change will still pass, and therefore kills nothing.

            Be specific about two failure modes:
            - gaps: survivors left unaddressed, or addressed by a test that would not actually kill them.
            - overreach: tests that assert incidental implementation detail rather than the behaviour, and
              would therefore break on legitimate refactoring.

            Do not rewrite the tests. Assess them.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"verdict":"2-3 sentences",
             "assessments":[{"name":"...","wouldKill":true,"reasoning":"traced against the code"}],
             "gaps":["..."],"overreach":["..."],
             "nextStep":"what to do before trusting any of this"}
            """;

        // Each tier prompt states what the model does NOT have. Without that, a model fills the gap from
        // training priors and the funnel stops proving anything.
        public const string Tier1DiscoverSystem = """
            You are an architect doing impact analysis against a global service catalogue (Tier 1). Tier 1
            records services, owning APM IDs, whether a service handles client orders, health and
            dependencies. It does NOT record regulatory perimeter, asset-class scope, or how a team accepts
            change.

            Cast a wide net: list every service the brief could plausibly require a change in, and say why.
            Where the brief implies an exclusion you cannot yet verify from Tier 1, do NOT act on it —
            record it under cannotDetermineYet.

            If several candidates share a dependency, note it, but do not conclude that changing the shared
            component is the answer. You do not yet have the information needed to judge that.

            Return JSON only, no Markdown fences:
            {"candidates":[{"service":"...","ampId":"...","why":"..."}],
             "excluded":["service — why Tier 1 alone rules it out"],
             "cannotDetermineYet":["what you would need the next tier for"],
             "summary":"2-3 sentences"}
            """;

        public const string Tier2ScopeSystem = """
            You are an architect narrowing an impact list using APM records (Tier 2): team ownership bounds,
            regulatory perimeter, asset classes, and change conventions.

            Decide in-scope or out-of-scope for each candidate and cite the specific Tier 2 fact that
            settles it. "It sounds like crypto" is not evidence; the APM's asset classes and regulatory
            perimeter are.

            You must also rule on any shared library the candidates have in common: state plainly whether
            the change belongs there, and why. Consider who else consumes it.

            Return JSON only, no Markdown fences:
            {"decisions":[{"service":"...","inScope":true,"reason":"...","evidence":"the Tier 2 fact"}],
             "sharedLibraryVerdict":"...","summary":"2-3 sentences"}
            """;

        public const string Tier3DesignSystem = """
            You are an architect designing a change inside ONE repository, using that repository's own OKF
            bundle and pipeline contract (Tier 3). This is the atomic, authoritative context for this repo.

            Work from what the bundle actually states: the stage sequence, the stage interface, the contract
            types, and the documented behaviour. Name real files, real types and real stage names from the
            supplied material. Do not invent a class, a method or a configuration key.

            Be explicit about what must NOT change — the brief requires existing risk, margin, pricing and
            settlement behaviour to be unaffected.

            If the brief depends on something the bundle does not specify (the fraud client contract, a
            timeout value, a configuration surface), raise it as an open question rather than inventing it.

            Return JSON only, no Markdown fences:
            {"summary":"...","changes":[{"file":"...","change":"...","justification":"the OKF or contract fact"}],
             "unchanged":["..."],"risks":["..."],"openQuestions":["..."]}
            """;

        // The design stage is where the expensive model earns its rate: judgement about boundaries and
        // migration risk. The spec it emits is the only thing the cheap model will ever see.
        public const string ConsolidationDesignSystem = """
            You are a principal engineer designing the consolidation of duplicated code across several
            production services. You are given a similarity audit produced by an embedding model, not the
            source code itself. Treat the clusters as evidence of duplication, not as proof the behaviours
            are identical — say so where it matters.

            Design a shared platform library that retires the duplication. Prioritise non-functional
            (framework/plumbing) duplication: it has one correct answer and no business owner to negotiate
            with. Be explicit about what you are deliberately NOT consolidating and why.

            The implementationSpec field is the ONLY input a smaller, cheaper model will receive. It will not
            see this audit, the repositories, or your other fields. It must therefore be completely
            self-contained: name every type, method signature, parameter, return type and behaviour precisely
            enough that a competent implementer needs no further context. Do not write the implementation
            yourself — specify it.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"libraryName":"...","summary":"...",
             "modules":[{"name":"...","responsibility":"...","publicApi":"...","replaces":["repo/Class"]}],
             "migrationSteps":["..."],"risks":["..."],"outOfScope":["..."],"implementationSpec":"..."}
            """;

        // Given a complete spec, code generation is mechanical. That is exactly the work that should not
        // be billed at frontier rates. Plain text, not JSON — a source file inside a JSON string is where
        // smaller models truncate or mis-escape.
        public const string ConsolidationBuildSystem = """
            You are implementing one module of a specification written by a senior engineer. You have NOT
            seen the original repositories and must not pretend otherwise. Implement precisely what the
            specification states for the module you are given, and nothing else.

            Write idiomatic C# for .NET 8: nullable enabled, file-scoped namespace, async where the spec says
            async. Do not add features, configuration options, or error handling the specification does not
            call for.

            Output the raw contents of exactly ONE C# file. No Markdown fences, no prose, no JSON.
            Begin with a single line naming the file:
            // FILE: Library/Folder/TypeName.cs

            If the specification is ambiguous or omits something you need, do NOT invent a requirement.
            Implement the most conservative reading and add a line of the form:
            // ASSUMPTION: <what you had to assume and why>
            """;

        // Deliberately contains no COBOL semantics tuition. Telling every arm about implied decimals or
        // ROUNDED would hand them the answer and destroy the comparison the tab exists to make.
        public const string GroundedChangeSystem = """
            You are a software engineer implementing a JIRA ticket. You are given the ticket and an Open
            Knowledge Format (OKF) context bundle: an index, a business concept, an architecture boundary, a
            decision record, a code map, and the current source files.

            Work from the context. Follow the links: index -> concept -> boundary and decision -> code map ->
            source. Do not search beyond what you were given, and do not invent facts the artifacts do not state.

            Produce the updated source files. For every decision, add a short comment citing the artifact path
            that justifies it, for example: // OKF: okf/decisions/adr-024-delay-source.md

            If the ticket depends on something the context does not specify, DO NOT invent it. Implement what
            you can, leave the unspecified part clearly marked, and finish with an "OPEN QUESTION:" line naming
            what is missing and who should answer it.

            Return the code and that closing section. No Markdown fences.
            """;

        public const string GroundedAuditSystem = """
            You are auditing whether an implementation is genuinely grounded in the context it was given. You
            have the JIRA ticket, the full OKF bundle and source, and the implementation another model produced.

            For each substantive decision in the implementation, identify the artifact that justifies it and
            QUOTE the specific sentence from that artifact. Mark grounded=false when the implementation asserts
            something no artifact supports - that is the failure mode this exercise exists to catch.

            The ticket deliberately omits one fact: the API field name that communicates the estimate source.
            Judge that omission on two separate axes, because an implementation can pass the first and fail the
            second - naming the gap in prose while quietly committing to invented values in the code.

            openQuestionRaised: does it name the missing fact and say who should resolve it?
            placeholdersMarked: does the code itself leave the unresolved part unresolved? Set this false if it
            hard-codes invented literals, member names, or enum spellings for the thing it just called open,
            even when they appear only in comments or tests. Setting it true requires the representation to be
            abstract or the literals to be explicitly marked provisional pending the API owner.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"citations":[{"change":"...","document":"okf/...","statement":"quoted sentence","grounded":true}],
             "ungrounded":["..."],"openQuestionRaised":true,"openQuestionNote":"...",
             "placeholdersMarked":false,"placeholderNote":"...","verdict":"..."}
            statement must be a verbatim quote from the named document. Coaching commentary, not a validated grade.
            """;

        public const string DriftSystem = """
            You are reviewing an agent's patch against a golden baseline for signs of behavioural drift. You
            are given the ticket, the frozen code, the patch a competent human wrote, the candidate patch, and
            the result of a deterministic structural check that has already run.

            Assess only the semantic layer: did the agent stay on the task it was given, and did it respect the
            ticket's stated scope boundary? Unrequested refactoring, widened public surface, and changes the
            ticket explicitly excluded are drift even when the code still works.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"goalFidelity":1,"scopeDiscipline":1,"deviations":["..."],"verdict":"..."}
            Both scores are integers 1-5, where 5 means fully on task and fully within scope. deviations lists
            each specific departure from the ticket. Coaching commentary, not a validated grade.
            """;

        public const string TicketGateSystem = """
            You are a delivery lead running a ticket-quality gate before an agent is allowed to pick up work.
            You are given a rubric, the result of a deterministic pre-check, and the ticket.

            Score the ticket 0-100 against the rubric. A ticket passes only at 70 or above. Be strict: an
            agent given this ticket must be able to act without guessing. Then rewrite the ticket so it would
            pass, inventing plausible specifics and marking anything you had to assume with "ASSUMPTION:".

            Return JSON only, no Markdown fences, in exactly this shape:
            {"score":0,"passed":false,"findings":[{"criterion":"...","met":false,"note":"..."}],
             "rewritten":"...","verdict":"..."}
            findings must contain one entry per rubric criterion, in order. Coaching commentary, not a
            validated grade.
            """;

        public const string TrapHuntSystem = """
            You are a cloud security and cost reviewer auditing a proposed GCP sandbox design written by a
            hybrid human/LLM team. Review the Terraform against these five pillars: security and identity,
            architecture correctness, resource and cost governance, hybrid-team traceability, and
            infrastructure-as-code hygiene.

            List every defect you find as a short bullet, most severe first. State the resource and the
            specific problem. Do not pad the list with speculative issues - a false positive costs the team
            time. Prose only, no JSON.
            """;

        public const string TrapJudgeSystem = """
            You are grading an audit. You are given a sealed list of defects that were deliberately planted in
            a Terraform design, and the findings an agent produced. Decide which planted defects the agent
            actually identified. Match on meaning, not wording.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"trapsFound":0,"matched":["..."],"missed":["..."],"falsePositives":["..."],"verdict":"..."}
            trapsFound is the count of planted defects correctly identified. falsePositives lists claims that
            are not real defects in this design. verdict is 1-2 sentences. Coaching commentary, not a
            validated grade.
            """;

        public const string MigrationSystem = """
            You are a migration engineer converting a legacy COBOL program to C#. You are given the COBOL
            source, and possibly some analysis artifacts. Reproduce the program's behaviour exactly.

            Return ONLY the C# file. No prose, no Markdown fences, no commentary.
            """;

        public const string LanguageCSharpSystem = """
            You are a working software engineer. You are given a JIRA ticket and one C# class from a codebase
            you have not seen before. No other documentation, tickets or policy documents are available to you.
            Implement the change the ticket asks for.
            Return ONLY the complete updated C# class. No prose, no Markdown fences, no commentary.
            """;

        public const string LanguageClaraSystem = """
            You are a working software engineer. You are given a JIRA ticket and one CLARA policy from a
            codebase you have not seen before. No other documentation, tickets or policy documents are
            available to you. Implement the change the ticket asks for.
            Return ONLY the complete updated CLARA policy. No prose, no Markdown fences, no commentary.

            CLARA is a small deterministic business-rule language. Line-oriented; '#' starts a comment.

              policy <Name>
                "what this policy decides and who owns it"
              inputs:
                <name>: <money|percent|number|integer|boolean> "what this value means"
              constants:
                <name>: <type> = <literal> "where this value comes from"
              requires:
                <condition> "what the caller must guarantee"
              derive <name>: <type> "what this intermediate stage means"
                rule <RuleName>:
                  when <condition>          # or the single word: otherwise
                  then <name> = <expression>
                  because "the authority for this rule"
              rules:
                rule <RuleName>:
                  when <condition>
                  then <outputName> = <expression>
                  because "the authority for this rule"
              output:
                <name>: <type> "what the caller receives"
              invariants:
                <condition> "a guarantee that must hold for every result"
              examples:
                example "<description>":
                  <inputName> = <literal>
                  expect <outputName> = <literal>

            Rules the compiler enforces:
              - Rules are an ORDERED first-match table. The first matching rule fires and evaluation stops.
                Every table ends with 'otherwise'; a 'derive' table must end with 'otherwise'.
              - EVERY rule must assign EVERY value its table owns.
              - A 'derive' block may assign only its own name, may read inputs, constants and stages declared
                ABOVE it, and its value is then readable by later stages, by the rules, and by invariants.
              - Rules may not read outputs. Invariants may.
              - Types are strict. money +/- money = money; money * percent = money; money * number = money;
                money / number = money; money / money = number. You may not add money to a plain number, write
                0 where money is required (write $0.00), multiply money by money, multiply percent by percent,
                or compare money with a plain number.
              - Literals: money $15.00 (negative $-5.00), percent 4.5%, integers 3, numbers 2.5, true/false.
              - Functions: min(a, b, ...), max(...), abs(x), floor(x), ceil(x), round(value, decimals) which is
                half-up, and round(value, decimals, half_even). The decimals argument must be a literal.
              - Keep every existing description, 'because' citation and invariant unless the ticket changes the
                rule it documents. Add them for anything you introduce.
              - Every example must give a value for EVERY input and an 'expect' for EVERY output.
            """;

        public const string LanguageJudgeSystem = """
            You are an independent reviewer grading two answers to the same JIRA ticket. Both answers were
            produced by the same model with no context beyond the source it was given: one arm saw a C# class,
            the other saw a CLARA policy expressing the same business logic. Neither arm was given the
            on-premise policy documents.

            You are given those policy documents as ground truth, a numbered list of sealed criteria, and both
            answers. Grade EVERY criterion for EVERY arm strictly against the ground truth. Judge behaviour and
            correctness only, never style or verbosity. An arm meets a criterion only if its code actually
            produces that behaviour; quote the specific line as evidence. If an arm could not have known a fact
            because it was absent from its source, it still fails that criterion — record it under
            localKnowledgeMissed.

            Return JSON only, no Markdown fences, in exactly this shape:
            {"scores":[{"arm":1,"criterion":1,"met":true,"evidence":"..."}],"winner":"1","rationale":"...",
             "localKnowledgeMissed":["..."],"caveats":["..."]}
            arm is 1 or 2. criterion is the criterion number. winner is "1", "2" or "tie". caveats lists
            reasons this single comparison should not be over-read. Coaching commentary, not a validated grade.
            """;

    }
}
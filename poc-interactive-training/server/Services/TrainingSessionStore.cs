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
    private static readonly string[] ComparisonModels = ["GPT-5.6 Sol", "Claude Opus 5.0", "Gemini 3.7 Flash"];
    private const string JudgeModel = "GPT-5.6 Sol";
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly object _gate = new();
    private readonly TrainingFixtureProvider _fixture;
    private readonly CSharpAuditAnalyzer _auditAnalyzer;
    private readonly ContextAuditService _contextAudit;
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

    private static readonly string[] ContextTraps =
    [
        "Daily rate is 2.5% in code but the context specifies 1.5%.",
        "Fee cap is $500 in code but the context specifies $250.",
        "The balance <= 0 guard required by rule 3 is missing.",
        "The 3-day grace period required by rule 5 is not implemented.",
        "The fee is rounded to whole dollars, but rule 6 requires 2 decimal places."
    ];

    public TrainingSessionStore(TrainingFixtureProvider fixture, CSharpAuditAnalyzer auditAnalyzer, ContextAuditService contextAudit, EmbeddingGemmaEncoder encoder, CobolToolchain cobol, MigrationSandbox sandbox)
    {
        _fixture = fixture;
        _auditAnalyzer = auditAnalyzer;
        _contextAudit = contextAudit;
        _encoder = encoder;
        _cobol = cobol;
        _sandbox = sandbox;

        var (csharpTokens, exact) = _encoder.CountTokens(LanguageTestSamples.CSharpSource);
        CSharpSourceTokens = csharpTokens;
        ClaraSourceTokens = _encoder.CountTokens(LanguageTestSamples.ClaraSource).Tokens;
        ContextPackTokens = _encoder.CountTokens(LanguageTestSamples.ContextPack).Tokens;
        TokensAreExact = exact;
        ToolchainAvailable = _cobol.IsAvailable;
        ToolchainStatus = _cobol.StatusMessage;
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
        if (session.ComparisonSlots.Count == 0 || session.ComparisonJudgeTaskId is not null)
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

        var judgeTask = new BridgeTask(
            Guid.NewGuid().ToString("N"),
            session.SessionId,
            "model-judge",
            JudgeModel,
            null,
            Prompts.JudgeSystem,
            BuildJudgeRequest(session),
            90,
            DateTimeOffset.UtcNow);
        session.ComparisonJudgeTaskId = judgeTask.TaskId;
        session.Tasks[judgeTask.TaskId] = new TaskState(judgeTask);
        session.ComparisonStatus = "judging";
    }

    private static string BuildAnswerRequest(ComparisonTopic topic) => $"""
        <question>
        {topic.Question}
        </question>
        Answer accurately and concisely for a technical practitioner. Prefer correct, specific detail over length.
        If you are not certain of a fact, say so rather than inventing it.
        """;

    private static string BuildJudgeRequest(SessionState session)
    {
        var topic = session.ComparisonTopic!;
        var request = new System.Text.StringBuilder();
        request.AppendLine("<question>");
        request.AppendLine(topic.Question);
        request.AppendLine("</question>");
        request.AppendLine("<reference_checklist>");
        request.AppendLine("Authoritative sources for ground truth:");
        foreach (var url in topic.ReferenceUrls)
        {
            request.AppendLine($"- {url}");
        }

        request.AppendLine("Key points an ideal answer should cover:");
        foreach (var keyword in topic.MustIncludeKeywords)
        {
            request.AppendLine($"- {keyword}");
        }

        request.AppendLine("</reference_checklist>");

        foreach (var slot in session.ComparisonSlots.Values
            .Where(slot => slot.Status == "completed" && !string.IsNullOrWhiteSpace(slot.Content))
            .OrderBy(slot => slot.Slot, StringComparer.Ordinal))
        {
            request.AppendLine($"<answer id=\"{slot.Slot}\">");
            request.AppendLine(slot.Content);
            request.AppendLine("</answer>");
        }

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
        }

        return true;
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
                score.Notes?.Trim() is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 240)] : null))
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
            result.ModelUsed ?? JudgeModel,
            scores,
            ranking,
            dto.Rationale?.Trim() ?? string.Empty);
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
        public string? LastError { get; set; }
        public Dictionary<string, TaskState> Tasks { get; } = [];

        public string ComparisonStatus { get; set; } = "not-started";
        public ComparisonTopic? ComparisonTopic { get; set; }
        public Dictionary<string, ComparisonSlotState> ComparisonSlots { get; } = [];
        public Dictionary<string, string> ComparisonTaskSlot { get; } = [];
        public string? ComparisonJudgeTaskId { get; set; }
        public ComparisonVerdict? ComparisonVerdict { get; set; }
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
                        slot.KeywordCoverage))
                    .ToArray(),
                ComparisonVerdict,
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
                MigrationError));
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
    }

    private sealed record JudgeDto(List<JudgeScoreDto>? Scores, List<string>? Ranking, string? Rationale);

    private sealed record JudgeScoreDto(string? Slot, int Factuality, int Completeness, int Conciseness, string? Notes);

    private sealed record RoundTripQaDto(int Fidelity, string? Summary, List<string>? Preserved, List<string>? Gaps, List<string>? Risks, string? Recommendation);

    private sealed record ModernizeDto(string? ModernizedCode, string? Explanation, List<string>? BusinessRules, List<string>? MicroserviceCandidates);

    private sealed record AuditRecommendationDto(string? Summary, List<string>? Strengths, List<string>? Priorities, List<string>? AgenticReadiness);

    private sealed record ContextGemmaDto(string? Overview, List<string>? Gaps);

    private sealed record ContextJudgeDto(int TrapsFound, List<string>? Matched, List<string>? Missed, List<string>? FalsePositives, string? Verdict);

    private sealed record ClaraReviewDto(int Fidelity, string? Verdict, List<string>? Strengths, List<string>? Issues, List<string>? LanguageNotes);

    private sealed record LanguageScoreDto(int Arm, int Criterion, bool Met, string? Evidence);

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

        public const string JudgeSystem = """
            You are an impartial evaluator comparing anonymized answers (A, B, C) to one technical
            Google Cloud question. Score each answer only against the reference checklist and general
            correctness — never reward length or style. Penalize hallucinated facts heavily under factuality.
            Judge only the answers provided; do not add your own answer.
            Return JSON only, no Markdown fences, in exactly this shape:
            {"scores":[{"slot":"A","factuality":1,"completeness":1,"conciseness":1,"notes":"short"}],"ranking":["A","B","C"],"rationale":"2-3 sentences"}
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

        // Deliberately contains no COBOL semantics tuition. Telling every arm about implied decimals or
        // ROUNDED would hand them the answer and destroy the comparison the tab exists to make.
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
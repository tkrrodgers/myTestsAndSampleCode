using PocInteractiveTraining.Server.Components;
using PocInteractiveTraining.Server.Models;
using PocInteractiveTraining.Server.Services;

// Proves the migration harness works end to end without any model: the oracle produces ground truth,
// a correct reference passes every case, and a plausible-but-wrong one is caught.
if (args.Contains("--migration-selftest"))
{
    var toolchain = new CobolToolchain();
    Console.WriteLine(toolchain.StatusMessage);
    if (!toolchain.IsAvailable)
    {
        return 2;
    }

    var facts = toolchain.ExtractFacts(MigrationSamples.CobolSource);
    Console.WriteLine($"Compiler resolved {facts.Fields.Count} fields, {facts.ControlFlow.Count} control-flow facts.");
    foreach (var field in facts.Fields)
    {
        Console.WriteLine($"  {field.Name,-16} offset {field.Offset,2}  {field.Size} bytes  {field.Attribute}");
    }

    foreach (var fact in facts.ControlFlow)
    {
        Console.WriteLine($"  {fact}");
    }

    var oracle = toolchain.RunOracle(MigrationSamples.CobolSource, MigrationSamples.Cases);
    Console.WriteLine("Oracle:");
    foreach (var run in oracle)
    {
        Console.WriteLine($"  {run.Input} -> {(run.Error is null ? run.Output : "ERROR " + run.Error)}");
    }

    var sandbox = new MigrationSandbox();
    var correct = sandbox.Run(MigrationReference.Correct, MigrationSamples.EntryType, MigrationSamples.EntryMethod, MigrationSamples.Cases);
    var naive = sandbox.Run(MigrationReference.Naive, MigrationSamples.EntryType, MigrationSamples.EntryMethod, MigrationSamples.Cases);

    int Score(MigrationRunResult run) => run.Ran
        ? run.Outputs.Count(output => MigrationReference.Matches(oracle, output))
        : -1;

    var correctScore = Score(correct);
    var naiveScore = Score(naive);
    Console.WriteLine($"Reference implementation: {correctScore}/{MigrationSamples.Cases.Length} cases match the legacy program.");
    Console.WriteLine($"Naive implementation:     {naiveScore}/{MigrationSamples.Cases.Length} (expected to be lower - it ignores the implied decimals).");

    var healthy = correctScore == MigrationSamples.Cases.Length && naiveScore < correctScore && facts.Fields.Count > 0;
    Console.WriteLine(healthy
        ? "Harness OK: it grades a correct migration as correct and catches a wrong one."
        : "HARNESS FAILURE: the grader does not discriminate.");
    return healthy ? 0 : 1;
}

// Prints the measured cross-repo similarity distribution so the clustering threshold is chosen from
// the data rather than guessed. Run this before pointing the audit at a different estate.
if (args.Contains("--calibrate-corpus"))
{
    var corpus = new TradingCorpus(null);
    Console.WriteLine(corpus.StatusMessage);
    if (!corpus.IsAvailable)
    {
        return 2;
    }

    using var calibrationEncoder = new EmbeddingGemmaEncoder(string.Empty);
    Console.WriteLine(calibrationEncoder.StatusMessage);
    var pairs = new CommonCodeAuditService(calibrationEncoder, corpus).AllPairs();
    var crossRepo = pairs.Where(pair => pair.CrossRepo).ToList();

    Console.WriteLine($"\n{crossRepo.Count} cross-repo pairs. Top 25:");
    foreach (var pair in crossRepo.Take(25))
    {
        Console.WriteLine($"  {pair.Similarity:0.0000}  {pair.Left}  ~  {pair.Right}");
    }

    // Same-name pairs across repos are the known duplicates; everything else is the noise floor.
    static string ClassOf(string label) => label.Split('/')[^1];
    var duplicates = crossRepo.Where(pair => ClassOf(pair.Left) == ClassOf(pair.Right)).ToList();
    var unrelated = crossRepo.Where(pair => ClassOf(pair.Left) != ClassOf(pair.Right)).ToList();
    Console.WriteLine($"\nKnown duplicates ({duplicates.Count}): min {duplicates.Min(pair => pair.Similarity):0.0000}  mean {duplicates.Average(pair => pair.Similarity):0.0000}  max {duplicates.Max(pair => pair.Similarity):0.0000}");
    Console.WriteLine($"Unrelated       ({unrelated.Count}): min {unrelated.Min(pair => pair.Similarity):0.0000}  mean {unrelated.Average(pair => pair.Similarity):0.0000}  max {unrelated.Max(pair => pair.Similarity):0.0000}");

    var floor = duplicates.Min(pair => pair.Similarity);
    var ceiling = unrelated.Max(pair => pair.Similarity);
    Console.WriteLine(floor > ceiling
        ? $"\nSeparable. Gap [{ceiling:0.0000}, {floor:0.0000}] — midpoint threshold {(floor + ceiling) / 2:0.0000}"
        : $"\nNOT cleanly separable: the worst duplicate ({floor:0.0000}) scores below the best unrelated pair ({ceiling:0.0000}). Any single threshold will misclassify.");
    return 0;
}

// Scores a saved model output against the SME fact list. Signal changes must be checked against real
// model text; loose signals silently inflate both sides of the comparison and hide the difference.
if (args.Length >= 2 && args[0] == "--score-facts")
{
    if (!File.Exists(args[1]))
    {
        Console.WriteLine($"No such file: {args[1]}");
        return 2;
    }

    var (factScore, factHit, factMissed) = CryptoSmeCorpus.ScoreFacts(File.ReadAllText(args[1]));
    Console.WriteLine($"score {factScore}%  ({factHit.Count}/{CryptoSmeCorpus.Facts.Count})");
    foreach (var name in factHit)
    {
        Console.WriteLine("  HIT     " + name);
    }

    foreach (var name in factMissed)
    {
        Console.WriteLine("  missing " + name);
    }

    return 0;
}

// Prints what the judge would actually read from a reference page. HTML-to-text extraction that quietly
// returns navigation chrome would poison the grounding without failing anything.
if (args.Length >= 2 && args[0] == "--fetch-doc")
{
    using var probeHttp = new HttpClient();
    var probe = await new DocumentationFetcher(probeHttp).FetchAsync([args[1]], CancellationToken.None);
    foreach (var document in probe)
    {
        Console.WriteLine($"retrieved={document.Retrieved} title={document.Title}");
        Console.WriteLine(document.Retrieved
            ? $"{document.Characters:N0} characters supplied{(document.Truncated ? " (truncated)" : "")}\n---\n{document.Text[..Math.Min(document.Text.Length, 2500)]}"
            : $"error: {document.Error}");
    }

    return probe.Any(document => document.Retrieved) ? 0 : 2;
}

// Compiles one CLARA file, runs its examples, and reports diagnostics. Usable as a CI gate.
if (args.Length >= 2 && args[0] == "--clara-check")
{
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.WriteLine($"No such file: {path}");
        return 2;
    }

    var checkResult = ClaraCompiler.Run(File.ReadAllText(path));
    foreach (var diagnostic in checkResult.Diagnostics)
    {
        Console.WriteLine($"{(diagnostic.Severity == ClaraSeverity.Error ? "error  " : "warning")} {diagnostic.Format()}");
    }

    Console.WriteLine(checkResult.Compiled
        ? $"compiled policy '{checkResult.PolicyName}' in {checkResult.CompileMilliseconds:N2} ms · {checkResult.RuleCount} rules · {checkResult.Derived.Count} derived stages · {checkResult.Invariants.Count} invariants"
        : "did not compile");

    foreach (var example in checkResult.Examples)
    {
        var status = example.Passed switch { true => "PASS", false => "FAIL", _ => "----" };
        Console.WriteLine($"  [{status}] {example.Description} => {string.Join(", ", example.Outputs)} (rule: {example.MatchedRule ?? "none"}){(example.Note is null ? "" : " — " + example.Note)}");
    }

    var failed = checkResult.Examples.Count(example => example.Passed == false);
    Console.WriteLine($"{checkResult.Examples.Count(example => example.Passed == true)}/{checkResult.Examples.Count} examples passed.");
    return checkResult.Compiled && failed == 0 ? 0 : 1;
}

// Headless gate: proves the CLARA compiler's guarantees and measures it against hand-written C#.
if (args.Contains("--clara-selftest"))
{
    var conformance = ClaraConformance.Run();
    foreach (var testCase in conformance.Cases.Where(item => !item.Passed))
    {
        Console.WriteLine($"FAIL  {testCase.Name}: {testCase.Detail}");
    }

    Console.WriteLine($"CLARA conformance: {conformance.Passed}/{conformance.Total} cases in {conformance.DurationMilliseconds:N0} ms.");

    var measurement = new ClaraBenchmark().Run();
    Console.WriteLine($"Runtime: {measurement.Runtime}");
    Console.WriteLine($"Compile: {measurement.CompileMilliseconds:N2} ms · {measurement.Iterations:N0} calls x {measurement.Rounds} rounds (best round)");
    foreach (var row in measurement.Rows)
    {
        Console.WriteLine($"  {row.Engine,-30} {row.NanosecondsPerCall,8:N1} ns/call  {row.MillionCallsPerSecond,8:N2} M/s  {row.RelativeToCSharp,6:N2}x  {row.Detail}");
    }

    Console.WriteLine(measurement.Summary);
    return conformance.Passed == conformance.Total && measurement.ParityMismatches == 0 ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);

// This POC is run with `dotnet run -c Release` from the project directory, which resolves to the
// Production environment - and Production does not load the static web assets manifest. Without this,
// the scoped-CSS bundle 404s and every `.razor.css` rule silently vanishes.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<TrainingFixtureProvider>();
builder.Services.AddSingleton<CSharpAuditAnalyzer>();
builder.Services.AddSingleton<AgentRegistryService>();
builder.Services.AddSingleton<DataTierClassifier>();
builder.Services.AddSingleton<ActionAuthorityService>();
builder.Services.AddSingleton<RegressionAdequacyService>();
builder.Services.AddSingleton<PortfolioContextService>();
builder.Services.AddSingleton(_ => new TradingCorpus(builder.Configuration["TradingCorpusRoot"]));
builder.Services.AddHttpClient<DocumentationFetcher>();
builder.Services.AddSingleton<CommonCodeAuditService>();
builder.Services.AddSingleton<TokenEconomicsService>();
builder.Services.AddSingleton<PatternLibraryService>();
builder.Services.AddSingleton<AutopilotContract>();
builder.Services.AddSingleton(_ => new EmbeddingGemmaEncoder(
    Path.Combine(builder.Environment.ContentRootPath, "..", "models", "embeddinggemma-300m-onnx")));
builder.Services.AddSingleton<ContextAuditService>();
builder.Services.AddSingleton<CobolDomainService>();
builder.Services.AddSingleton<ClaraBenchmark>();
builder.Services.AddSingleton<CobolToolchain>();
builder.Services.AddSingleton<MigrationSandbox>();
builder.Services.AddSingleton<ContextClassifierService>();
builder.Services.AddSingleton<LlmShootoutService>();
builder.Services.AddSingleton(_ => new LocalInferenceService(Path.Combine(builder.Environment.ContentRootPath, "..")));
builder.Services.AddSingleton<TrainingSessionStore>();

var app = builder.Build();

// Surface embeddinggemma load status and force the context classifier to train at startup.
app.Logger.LogInformation("Context audit encoder: {Status}", app.Services.GetRequiredService<EmbeddingGemmaEncoder>().StatusMessage);
app.Logger.LogInformation("COBOL toolchain: {Status}", app.Services.GetRequiredService<CobolToolchain>().StatusMessage);
var contextAudit = app.Services.GetRequiredService<ContextAuditService>();
var contextProbe = contextAudit.Analyze(RoundTripSamples.ContextBundle);
app.Logger.LogInformation("Context audit self-check: {Label}, score {Score}, confidence {Confidence}%.", contextProbe.PredictedLabel, contextProbe.Score, contextProbe.Confidence);

// The classifier and the oracle must both hold before the Classify Context scene is shown to anyone.
app.Logger.LogInformation("Classify Context self-check: {Detail}", app.Services.GetRequiredService<ContextClassifierService>().SelfCheck());
app.Logger.LogInformation("LLM shootout self-check: {Detail}", app.Services.GetRequiredService<LlmShootoutService>().SelfCheck());
var specEnv = app.Services.GetRequiredService<LocalInferenceService>().Environment();
app.Logger.LogInformation("Local inference: {Status} target {Target} ({TargetMb} MB), draft {Draft} ({DraftMb} MB), {Cores} cores, {Free}/{Total} MB RAM free.", specEnv.Status, specEnv.TargetModel, specEnv.TargetMb, specEnv.DraftModel, specEnv.DraftMb, specEnv.LogicalCores, specEnv.FreeRamMb, specEnv.TotalRamMb);

var claraProbe = ClaraCompiler.Run(RoundTripSamples.ClaraSample);
app.Logger.LogInformation("CLARA compiler self-check: compiled={Compiled} in {Milliseconds:N2} ms, {Passed}/{Total} examples passed, {Warnings} warnings.",
    claraProbe.Compiled, claraProbe.CompileMilliseconds, claraProbe.Examples.Count(example => example.Passed == true), claraProbe.Examples.Count, claraProbe.Warnings.Count);
foreach (var diagnostic in claraProbe.Errors.Concat(claraProbe.Warnings))
{
    app.Logger.LogWarning("CLARA self-check: {Diagnostic}", diagnostic);
}

// Phase 0 gates must hold with no model available; prove both on a known-bad payload at startup.
var tierProbe = app.Services.GetRequiredService<AgentRegistryService>().Evaluate(new AgentRegistration
{
    Name = "probe", Owner = "", ReleaseId = "1", Model = "claude-opus-5@2026-08",
    BlastRadius = "Shared-prod", Autonomy = "Acts-autonomously", DataTier = "Restricted",
    AgentStatus = "Active", ToolEnabled = true
});
app.Logger.LogInformation("Agent registry self-check: {TierName}, registrable={Registrable}, {Missing} missing field(s).",
    tierProbe.TierName, tierProbe.Registrable, tierProbe.MissingFields.Count);

var gateProbe = app.Services.GetRequiredService<DataTierClassifier>().Assess(RoundTripSamples.EphemeralTestPayload);
app.Logger.LogInformation("Data-tier gate self-check: {Tier} -> {Decision}, {Findings} finding type(s), incident {Severity}.",
    gateProbe.TierName, gateProbe.Decision, gateProbe.Findings.Count, gateProbe.Incident.Severity);

// The tier scorer must separate a grounded plan from a plausible one, or the degradation is meaningless.
var richPlan = "Fix RegTMarginCalculator.OptionMargin in the risk stage. Branch on right1: for a put the OTM " +
    "amount is underlying - strike, and the alternative minimum is 10% of the strike, not the underlying. " +
    "Apply ADR-031 so a cash-secured put margins at strike x contracts x 100 minus premium. STRADDLE and " +
    "STRANGLE fall through to the naked branch and carry a put leg. Add a test per right. Open question: the " +
    "cash-collateral attribute name is not agreed — do not invent it.";
var barePlan = "Read the ticket, increase the margin for short puts, and ship it.";
app.Logger.LogInformation("Context-tier scorer self-check: grounded plan {Rich}/100, bare plan {Bare}/100.",
    ContextTierScorer.Score(richPlan).Score, ContextTierScorer.Score(barePlan).Score);

// Mutation testing is only meaningful if surviving mutants are actually found; the fixture is built to
// leave the enhanced behaviour uncovered, so a 100% score here would mean the harness is broken.
var auditProbe = app.Services.GetRequiredService<RegressionAdequacyService>().Audit(
    RegressionSamples.Baseline,
    RegressionSamples.Source,
    RegressionSamples.Tests,
    RegressionSamples.TestVectors,
    RegressionSamples.IntendedChanges);
var mutationProbe = auditProbe.Mutation;
app.Logger.LogInformation("Regression adequacy self-check: ran={Ran}, score {Score}%, {Killed}/{Total} mutants killed.{Error}",
    mutationProbe.Ran, mutationProbe.MutationScore, mutationProbe.KilledMutants, mutationProbe.TotalMutants,
    mutationProbe.Error is null ? string.Empty : " " + mutationProbe.Error);
app.Logger.LogInformation(
    "Differential self-check: {Vectors} vectors, {Identical} identical, {Intended} intended, {Regression} regression(s), {Adjudicate} to adjudicate.{Error}",
    auditProbe.Differential.VectorCount, auditProbe.Differential.Identical, auditProbe.Differential.Intended,
    auditProbe.Differential.Unintended, auditProbe.Differential.NotImplemented,
    auditProbe.Differential.Error is null ? string.Empty : " " + auditProbe.Differential.Error);
app.Logger.LogInformation(
    "Condition coverage self-check: {Full}/{Total} predicates driven both ways ({Percent}%), {Gaps} gap(s), {Skipped} skipped.{Error}",
    auditProbe.Coverage.FullyExercised, auditProbe.Coverage.Predicates, auditProbe.Coverage.CoveragePercent,
    auditProbe.Coverage.Gaps.Count, auditProbe.Coverage.SkippedPredicates,
    auditProbe.Coverage.Error is null ? string.Empty : " " + auditProbe.Coverage.Error);

var portfolioProbe = app.Services.GetRequiredService<PortfolioContextService>().Build();
app.Logger.LogInformation("Portfolio tier self-check: {Services} services, {Amps} APM records, {Repos} repo(s) with OKF, funnel {InScope} vs {All} tokens, shared-dependency trap detected={Trap}.",
    portfolioProbe.Services.Count, portfolioProbe.Amps.Count, portfolioProbe.Repositories.Count(repo => repo.Available),
    portfolioProbe.Cost.Tier3TokensInScope, portfolioProbe.Cost.Tier3TokensEverything,
    portfolioProbe.SharedDependencyWarning.Count > 0);

app.Logger.LogInformation("Trading corpus: {Status}", app.Services.GetRequiredService<TradingCorpus>().StatusMessage);

// The judge is grounded on live documentation, so a retrieval failure must surface here rather than as
// a mysterious comparison failure later. Network-dependent, so it must not gate startup.
_ = Task.Run(async () =>
{
    var probeUrls = ComparisonCatalog.Topics.Select(topic => topic.ReferenceUrls[0]).ToList();
    var fetched = await app.Services.GetRequiredService<DocumentationFetcher>().FetchAsync(probeUrls, CancellationToken.None);
    var ok = fetched.Count(document => document.Retrieved);
    app.Logger.LogInformation("Documentation grounding self-check: {Ok}/{Total} reference pages retrieved, {Characters} characters of ground truth.",
        ok, fetched.Count, fetched.Where(document => document.Retrieved).Sum(document => document.Characters));
    foreach (var failure in fetched.Where(document => !document.Retrieved))
    {
        app.Logger.LogWarning("Documentation grounding: {Url} could not be retrieved — {Error}", failure.Url, failure.Error);
    }
});

// Embedding 30+ real source files takes minutes on CPU, so this self-check must not gate startup.
_ = Task.Run(() =>
{
    var commonCodeProbe = app.Services.GetRequiredService<CommonCodeAuditService>().Analyse();
    app.Logger.LogInformation("Common-code audit self-check: embeddings={Embeddings}, {Duplicated} duplicated cluster(s) across {Units} files, duplicates {DuplicateMean:0.000} vs unrelated {UnrelatedMean:0.000} (margin {Margin:0.000}, {Hits}/{Total} nearest-neighbour hits), prize {Functional} functional / {NonFunctional} non-functional lines.",
        commonCodeProbe.UsedEmbeddings, commonCodeProbe.DuplicatedClusters, commonCodeProbe.UnitCount,
        commonCodeProbe.Separation.DuplicateMean, commonCodeProbe.Separation.UnrelatedMean, commonCodeProbe.Separation.Margin,
        commonCodeProbe.Separation.NearestNeighbourHits, commonCodeProbe.Separation.NearestNeighbourTotal,
        commonCodeProbe.FunctionalPrize, commonCodeProbe.NonFunctionalPrize);
});

// Phase 4. The economics probe asserts the interesting case: the rung that is cheapest per attempt
// is not the rung that is cheapest per successful outcome.
var economicsProbe = app.Services.GetRequiredService<TokenEconomicsService>().Build(4200, 700,
    [("open-weight", 35), ("open-weight-grounded", 78), ("mid", 72), ("frontier", 91)]);
app.Logger.LogInformation("Token economics self-check: cheapest/attempt {Attempt}, cheapest/success {Success}, rate card misleads={Misleads}.",
    economicsProbe.CheapestPerAttempt, economicsProbe.CheapestPerSuccess, economicsProbe.RateCardMisleads);

// A deliberately thin submission must be held, or the gate is decorative.
var libraryProbe = app.Services.GetRequiredService<PatternLibraryService>().Submit(new PatternSubmission
{
    Title = "Great prompt",
    Problem = "It works really well and the team likes it.",
    Approach = "Ask the model to fix the bug and it usually does the right thing."
});
app.Logger.LogInformation("Pattern library self-check: thin submission admitted={Admitted}, score {Score}%, {Failed} check(s) failed.",
    libraryProbe.Admitted, libraryProbe.Score, libraryProbe.Checks.Count(check => !check.Passed));

var planProbe = PlanFirstChecker.Review(PlanFirstSamples.GoodPlan);
var codeFirstProbe = PlanFirstChecker.Review("Here is the fix:\n```csharp\nvar x = 1;\n```");
app.Logger.LogInformation("Plan-first self-check: good plan {Good}%, code-first plan {CodeFirst}%.",
    planProbe.Score, codeFirstProbe.Score);

var streamProbe = TrainingSessionStore.HumaniseStreamFragment(
    "{\"trace\":{\"sequence\":1,\"stage\":\"Index first\",\"evidence\":\"Start at okf/index.md\",\"decision\":\"Correctly forces index-first");
app.Logger.LogInformation("Review stream humaniser self-check: clean={Clean}, sample=\"{Sample}\".",
    !streamProbe.Contains('{') && !streamProbe.Contains('"') && !streamProbe.Contains("\":"), streamProbe);

// The domain map is only as good as its coverage, so the coverage numbers are printed at startup rather
// than discovered during a demonstration.
var cobolDomainProbe = app.Services.GetRequiredService<CobolDomainService>().Run();
app.Logger.LogInformation(
    "COBOL domain self-check: {Status}; {Programs} program(s), {Nodes} node(s), {Edges} edge(s), {Neurons} neuron(s), {Synapses} synapse(s); coverage {Coverage}; front end = {FrontEnd}.",
    cobolDomainProbe.Available ? "fixture loaded" : cobolDomainProbe.StatusMessage,
    cobolDomainProbe.Programs.Count, cobolDomainProbe.Nodes.Count, cobolDomainProbe.Edges.Count,
    cobolDomainProbe.Neurons.Count, cobolDomainProbe.Synapses.Count,
    string.Join(", ", cobolDomainProbe.Coverage.Select(metric => $"{metric.Name} {metric.Percent}%")),
    CobolFrontEnd.FrontEndName);

foreach (var check in cobolDomainProbe.Checks.Where(check => !check.Passed))
{
    app.Logger.LogWarning("COBOL domain stage {Stage} check failed: {Name} — {Detail}", check.Stage, check.Name, check.Detail);
}

if (cobolDomainProbe.CopyOracle is { } copyOracle)
{
    app.Logger.LogInformation("COBOL COPY oracle: {Result} — {Detail}",
        copyOracle.Passed ? "resolvers agree" : "DISAGREEMENT", copyOracle.Detail);
}

app.Logger.LogInformation("COBOL field cards: {Cards} synthesised, {Bound} bound to a DB2 column, {Conditions} carrying level-88 states. Sample: {Sample}",
    cobolDomainProbe.FieldCards.Count,
    cobolDomainProbe.FieldCards.Count(card => card.ColumnBinding is not null),
    cobolDomainProbe.FieldCards.Count(card => card.Conditions.Count > 0),
    cobolDomainProbe.FieldCards.FirstOrDefault(card => card.ColumnBinding is not null)?.CardText ?? "none");

var autopilotProbe = app.Services.GetRequiredService<AutopilotContract>().Verify();
app.Logger.LogInformation(
    "Autopilot manifest self-check: {Steps} steps, {Selectors} selectors, {Resolved} resolved, {Missing} missing, {Violations} invariant violation(s), manifest v{Version}.",
    autopilotProbe.Steps, autopilotProbe.Selectors, autopilotProbe.Resolved,
    autopilotProbe.Missing.Count, autopilotProbe.Violations.Count, AutopilotManifest.Version);

foreach (var problem in autopilotProbe.Missing.Concat(autopilotProbe.Violations))
{
    app.Logger.LogWarning("Autopilot manifest problem: {Problem}", problem);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("This POC accepts loopback requests only.");
        return;
    }

    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

var bridge = app.MapGroup("/api/bridge");

bridge.MapPost("/heartbeat", (HttpRequest request, TrainingSessionStore store) =>
{
    return TryToken(request, store, out var token)
        ? Results.Ok(store.RecordHeartbeat(token!))
        : Results.Unauthorized();
});

bridge.MapGet("/tasks/next", (HttpRequest request, TrainingSessionStore store) =>
{
    if (!TryToken(request, store, out var token))
    {
        return Results.Unauthorized();
    }

    var task = store.ClaimNextTask(token!);
    return task is null ? Results.NoContent() : Results.Ok(task);
});

bridge.MapPost("/results", (HttpRequest request, BridgeTaskResult result, TrainingSessionStore store) =>
{
    if (!TryToken(request, store, out var token))
    {
        return Results.Unauthorized();
    }

    return store.CompleteTask(token!, result)
        ? Results.Accepted()
        : Results.BadRequest(new { error = "Unknown, expired, or already completed task." });
});

bridge.MapPost("/events", (HttpRequest request, ReviewTraceEvent traceEvent, TrainingSessionStore store) =>
{
    if (!TryToken(request, store, out var token))
    {
        return Results.Unauthorized();
    }

    return store.AppendReviewTrace(token!, traceEvent)
        ? Results.Accepted()
        : Results.BadRequest(new { error = "Trace event does not belong to the active review task." });
});

bridge.MapPost("/events/stream", (HttpRequest request, ReviewStreamChunk chunk, TrainingSessionStore store) =>
{
    if (!TryToken(request, store, out var token))
    {
        return Results.Unauthorized();
    }

    return store.AppendReviewStream(token!, chunk)
        ? Results.Accepted()
        : Results.BadRequest(new { error = "Stream chunk does not belong to the active review task." });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run("http://127.0.0.1:5000");

return 0;

static bool TryToken(HttpRequest request, TrainingSessionStore store, out string? token)
{
    token = request.Headers["X-Training-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(token) && store.IsValidToken(token);
}

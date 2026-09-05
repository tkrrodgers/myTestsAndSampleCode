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
builder.Services.AddSingleton(_ => new EmbeddingGemmaEncoder(
    Path.Combine(builder.Environment.ContentRootPath, "..", "models", "embeddinggemma-300m-onnx")));
builder.Services.AddSingleton<ContextAuditService>();
builder.Services.AddSingleton<ClaraBenchmark>();
builder.Services.AddSingleton<CobolToolchain>();
builder.Services.AddSingleton<MigrationSandbox>();
builder.Services.AddSingleton<TrainingSessionStore>();

var app = builder.Build();

// Surface embeddinggemma load status and force the context classifier to train at startup.
app.Logger.LogInformation("Context audit encoder: {Status}", app.Services.GetRequiredService<EmbeddingGemmaEncoder>().StatusMessage);
app.Logger.LogInformation("COBOL toolchain: {Status}", app.Services.GetRequiredService<CobolToolchain>().StatusMessage);
var contextAudit = app.Services.GetRequiredService<ContextAuditService>();
var contextProbe = contextAudit.Analyze(RoundTripSamples.ContextBundle);
app.Logger.LogInformation("Context audit self-check: {Label}, score {Score}, confidence {Confidence}%.", contextProbe.PredictedLabel, contextProbe.Score, contextProbe.Confidence);

var claraProbe = ClaraCompiler.Run(RoundTripSamples.ClaraSample);
app.Logger.LogInformation("CLARA compiler self-check: compiled={Compiled} in {Milliseconds:N2} ms, {Passed}/{Total} examples passed, {Warnings} warnings.",
    claraProbe.Compiled, claraProbe.CompileMilliseconds, claraProbe.Examples.Count(example => example.Passed == true), claraProbe.Examples.Count, claraProbe.Warnings.Count);
foreach (var diagnostic in claraProbe.Errors.Concat(claraProbe.Warnings))
{
    app.Logger.LogWarning("CLARA self-check: {Diagnostic}", diagnostic);
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run("http://127.0.0.1:5000");

return 0;

static bool TryToken(HttpRequest request, TrainingSessionStore store, out string? token)
{
    token = request.Headers["X-Training-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(token) && store.IsValidToken(token);
}

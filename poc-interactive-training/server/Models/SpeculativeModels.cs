namespace PocInteractiveTraining.Server.Models;

// Speculative decoding measured on this machine: llama.cpp runs the same greedy prompt three ways and
// reports its own timings. No model judges anything here; every number is a stopwatch or a counter.

public sealed record SpecArmResult(
    string Arm,
    string Label,
    string Status,
    int TokensOut,
    double DecodeTokPerSec,
    int DecodeMs,
    double PromptTokPerSec,
    int Drafted,
    int Accepted,
    int? AcceptRatePercent,
    int WorkingSetMb,
    string? Sha,
    string? Text,
    bool? IdenticalToBaseline,
    int? DivergesAtChar,
    string? Error);

public sealed record SpecPromptRun(
    string PromptKind,
    string PromptTitle,
    string Prompt,
    string Status,
    IReadOnlyList<SpecArmResult> Arms,
    long FreeRamMbBefore,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

public sealed record SpecEnvironment(
    bool Available,
    string Status,
    string ServerPath,
    string BackendLabel,
    string TargetModel,
    long TargetMb,
    string DraftModel,
    long DraftMb,
    int LogicalCores,
    long TotalRamMb,
    long FreeRamMb);

public sealed record SpeculativeState(
    string Status,
    SpecEnvironment Environment,
    IReadOnlyList<SpecPromptRun> Runs,
    string? Error);

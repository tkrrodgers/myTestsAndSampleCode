namespace PocInteractiveTraining.Server.Models;

// "LLM in the Room": a local Gemma 4 answers spoken questions on one topic with no cloud model and no
// bridge. Every latency figure is a stopwatch or llama.cpp's own counter; the transcript is the record.

public sealed record RoomEnvironment(
    bool Available,
    string Status,
    string ModelFile,
    long ModelMb,
    string WhisperModel,
    long WhisperMb,
    int Threads,
    long TotalRamMb,
    long FreeRamMb);

public sealed record RoomTurn(
    int Id,
    string Role,
    string Speaker,
    string Text,
    string Status,
    DateTimeOffset At,
    int SttMs,
    int FirstTokenMs,
    int TotalMs,
    int PromptTokens,
    int CachedTokens,
    int OutputTokens,
    double TokPerSec,
    string? Error);

public sealed record RoomNotes(
    string Markdown,
    int Version,
    DateTimeOffset? UpdatedAt,
    string Status,
    string? Error);

public sealed record RoomState(
    string Status,
    RoomEnvironment Environment,
    IReadOnlyList<RoomTurn> Turns,
    RoomNotes Notes,
    string Topic,
    int GroundingTokens,
    string? Error);

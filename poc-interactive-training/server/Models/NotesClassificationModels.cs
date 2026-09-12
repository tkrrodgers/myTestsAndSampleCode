namespace PocInteractiveTraining.Server.Models;

// Can EmbeddingGemma + ML.NET reproduce the BankDemo domain classification from in-code notes, and do
// call-graph edges recover the accuracy that cross-cutting concerns cost? Every number is a cosine or a
// held-out prediction; no model judges anything.

public sealed record NotesTestScore(string Name, string Method, int Top1, int Top3, int Total, string Detail);

public sealed record NotesProgramRow(
    string Name,
    string Layer,
    string TruthDomain,
    string TruthTitle,
    string Function,
    int NoteCount,
    int DistinctiveCount,
    string RetrievalPred,
    int RetrievalTruthRank,
    string StructuralPred,
    bool CrossCutting,
    int ClusterId);

public sealed record NotesCrossCutting(string Program, int FanIn, IReadOnlyList<string> CallerDomains, string Role);

public sealed record NotesDomainScore(string Number, string Title, int Programs, int CorrectRetrieval, int CorrectStructural);

public sealed record NotesClusterRow(int ClusterId, string PredictedDomain, string PredictedTitle, IReadOnlyList<string> Members);

public sealed record NotesClassificationResult(
    bool Available,
    string Status,
    string BankRoot,
    int ProgramCount,
    int DomainCount,
    int BoilerplatePhrases,
    int EdgeCount,
    NotesTestScore Retrieval,
    NotesTestScore RetrievalNoHelp,
    NotesTestScore Centroid,
    NotesTestScore Supervised,
    NotesTestScore Structural,
    IReadOnlyList<NotesProgramRow> Rows,
    IReadOnlyList<NotesCrossCutting> CrossCutting,
    IReadOnlyList<NotesDomainScore> DomainScores,
    IReadOnlyList<NotesClusterRow> Clusters);

public sealed record NotesClassificationState(
    string Status,
    bool Available,
    string EnvironmentStatus,
    string BankRoot,
    NotesClassificationResult? Result,
    string? Error);

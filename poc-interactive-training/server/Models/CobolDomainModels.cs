namespace PocInteractiveTraining.Server.Models;

// Domain-segmentation pipeline over a COBOL estate. Every record here is produced by executed code;
// nothing on this path is authored by a model. See migrateCOBOLprojectToJAVA.md for the design.

// ---------------------------------------------------------------- S0 inventory

public sealed record CobolMember(
    string Name,
    string Kind,
    string RelativePath,
    string Source,
    int RawLines,
    string Sha256);

// ---------------------------------------------------------------- S1 normalisation

/// <summary>One source line after fixed-format normalisation, still carrying its origin.</summary>
public sealed record CobolLine(
    int Index,
    int OriginalLine,
    string Member,
    string Text,
    bool IsComment,
    string IdArea,
    int JoinedFrom);

public sealed record NormalizeReport(
    string Member,
    int RawLines,
    int CodeLines,
    int CommentLines,
    int ContinuationsJoined,
    int IdAreaPopulated,
    bool RoundTripOk,
    string Detail);

// ---------------------------------------------------------------- S2 preprocessor

public sealed record CopyDirective(
    string Program,
    string Requested,
    string? Library,
    bool HasReplacing,
    bool Resolved,
    string Reason,
    int OriginalLine);

/// <summary>
/// What GnuCOBOL's own preprocessor made of the same COPY statements. Substituted members are ones cobc
/// satisfied from its bundled copy library rather than from the estate's, which is not the same thing.
/// </summary>
public sealed record CopyResolution(
    bool Ran,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> Substituted,
    IReadOnlyList<string> Missing,
    string Message);

public sealed record ExecBlock(
    string Program,
    string Dialect,
    string Body,
    int OriginalLine,
    int Lines);

public sealed record CompilerOptionFinding(
    string Member,
    string Option,
    string Value,
    string Origin,
    int OriginalLine);

// ---------------------------------------------------------------- S3/S4 AST

public sealed record CobolDataItem(
    string Program,
    int Level,
    string Name,
    string? Picture,
    string Usage,
    string? Redefines,
    int Occurs,
    int Offset,
    int Length,
    string Section,
    string Origin,
    int OriginalLine);

public sealed record CobolStatement(
    string Program,
    string Paragraph,
    int Ordinal,
    string Verb,
    string Text,
    int Depth,
    int ConditionOrdinal,
    int OriginalLine);

public sealed record CobolParagraph(
    string Program,
    string Name,
    int OriginalLine,
    int StatementCount);

public sealed record CobolProgramAst(
    string Program,
    string? ProgramId,
    IReadOnlyList<CobolParagraph> Paragraphs,
    IReadOnlyList<CobolDataItem> DataItems,
    IReadOnlyList<CobolStatement> Statements,
    IReadOnlyList<string> Divisions,
    int NodeCount,
    bool Parsed,
    string Detail);

// ---------------------------------------------------------------- S5-S8 graph

public sealed record GraphNode(
    string Id,
    string Kind,
    string Name,
    string Detail);

/// <summary>An edge is a defect unless it can name the member and line that produced it.</summary>
public sealed record GraphEdge(
    string Source,
    string Target,
    string Kind,
    string Confidence,
    double Weight,
    string EvidenceMember,
    int EvidenceLine,
    string RuleId);

public sealed record CrudEntry(
    string Program,
    string Artifact,
    string ArtifactKind,
    string Access,
    string Confidence,
    string EvidenceMember,
    int EvidenceLine);

public sealed record DataFlowEdge(
    string Program,
    string From,
    string To,
    string Via,
    int OriginalLine);

/// <summary>
/// One statement of fact from a JCL job or a CICS resource-definition (CSD) deck: which step runs which
/// program, which transaction starts which program, which tables the job creates. Reported beside the
/// graph as a cross-check on the source; not weighted into the partition at fixture scale.
/// </summary>
public sealed record JclFact(
    string Member,
    string Kind,
    string Subject,
    string Target,
    string Detail,
    int OriginalLine);

public sealed record PdgNode(
    string Program,
    string Paragraph,
    int Ordinal,
    string Verb,
    string Text,
    int ControlParent,
    string ControlCondition,
    int Depth,
    int OriginalLine);

public sealed record PdgResult(
    string Program,
    IReadOnlyList<PdgNode> Nodes,
    IReadOnlyList<(int From, int To)> ControlEdges,
    IReadOnlyList<DataFlowEdge> DataEdges,
    int MaxDepth,
    bool StructuredOnly,
    IReadOnlyList<string> Caveats);

// ---------------------------------------------------------------- S9 comments

public sealed record CommentBlock(
    string Member,
    string Kind,
    string Text,
    int OriginalLine,
    int Lines,
    bool Duplicated);

public sealed record MaintenanceEntry(
    string Member,
    string RawLine,
    string? Date,
    string? Author,
    string? Ticket,
    string Description,
    int OriginalLine);

public sealed record CommentCorpusReport(
    int TotalBlocks,
    int Boilerplate,
    int CommentedOutCode,
    int Header,
    int Paragraph,
    int Inline,
    int MaintenanceEntries,
    int UsableBlocks,
    double DedupRatio);

// ---------------------------------------------------------------- S10 field cards

/// <summary>
/// A data item rewritten as prose so it can be retrieved by meaning. "05 CUST-ID PIC X(10)" embeds as
/// noise: it is a token, not language, and vector search clusters it with every other PIC X(10). The card
/// is synthesised deterministically from the group path, the DCLGEN column binding, level-88 value
/// domains and an abbreviation glossary. No model is involved in writing it.
/// </summary>
public sealed record CobolFieldCard(
    string Program,
    string Field,
    string Declaration,
    string GroupPath,
    string ExpandedName,
    string TypeProse,
    string? ColumnBinding,
    IReadOnlyList<string> Conditions,
    string Origin,
    int OriginalLine,
    string CardText,
    IReadOnlyList<string> Sources);

public sealed record ColumnBinding(
    string Table,
    string Column,
    string SqlType);

// ---------------------------------------------------------------- S12 partition

public sealed record NeuronMember(
    string Program,
    double Stability,
    bool BoundaryObject);

public sealed record Neuron(
    int Id,
    string Label,
    string LabelSource,
    IReadOnlyList<NeuronMember> Members,
    IReadOnlyList<string> OwnedArtifacts,
    IReadOnlyList<string> ReadArtifacts,
    double Modularity);

public sealed record Synapse(
    string FromNeuron,
    string ToNeuron,
    string Artifact,
    string ArtifactKind,
    string Direction,
    string Writer,
    IReadOnlyList<string> Readers,
    string Cost);

public sealed record PartitionRun(
    double Resolution,
    int Communities,
    double Modularity);

public sealed record BoundaryObjectFinding(
    string Artifact,
    string Kind,
    IReadOnlyList<string> TouchedBy,
    double CoAssignment,
    string Why);

// ---------------------------------------------------------------- S13 cortex

public sealed record CortexHit(
    string Kind,
    string Title,
    string Snippet,
    string Member,
    int Line,
    double LexicalScore,
    double VectorScore,
    double GraphScore,
    double Fused,
    string Neuron);

public sealed record CortexAnswer(
    string Query,
    IReadOnlyList<CortexHit> Hits,
    bool UsedEmbeddings,
    bool UsedGraphPredicate,
    string Method,
    string? GraphAnswer);

// ---------------------------------------------------------------- coverage + report

public sealed record CoverageMetric(
    string Name,
    int Resolved,
    int Total,
    string Consequence)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Resolved / Total);
}

public sealed record StageCheck(
    string Stage,
    string Name,
    bool Passed,
    string Detail);

public sealed record OkfPreviewFile(
    string Path,
    string Frontmatter,
    string Body,
    bool Generated);

public sealed record CobolDomainReport(
    bool Available,
    string StatusMessage,
    string FixtureRoot,
    IReadOnlyList<CobolMember> Members,
    IReadOnlyList<NormalizeReport> Normalisation,
    IReadOnlyList<CopyDirective> Copies,
    IReadOnlyList<ExecBlock> ExecBlocks,
    IReadOnlyList<CompilerOptionFinding> CompilerOptions,
    IReadOnlyList<CobolProgramAst> Programs,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<CrudEntry> Crud,
    IReadOnlyList<JclFact> Jcl,
    IReadOnlyList<CommentBlock> Comments,
    IReadOnlyList<MaintenanceEntry> Maintenance,
    CommentCorpusReport CommentReport,
    IReadOnlyList<CobolFieldCard> FieldCards,
    IReadOnlyList<Neuron> Neurons,
    IReadOnlyList<Synapse> Synapses,
    IReadOnlyList<PartitionRun> Sweep,
    IReadOnlyList<BoundaryObjectFinding> BoundaryObjects,
    IReadOnlyList<CoverageMetric> Coverage,
    IReadOnlyList<StageCheck> Checks,
    StageCheck? CopyOracle,
    IReadOnlyList<OkfPreviewFile> Okf,
    IReadOnlyList<string> Findings,
    bool UsedEmbeddings,
    string EmbeddingStatus,
    long ElapsedMs);

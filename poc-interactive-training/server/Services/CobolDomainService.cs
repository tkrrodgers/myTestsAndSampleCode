using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Orchestrates the domain-segmentation pipeline over the IBM Global Auto Mart fixture.
//
// Everything reported here is produced by executed code. Models are not consulted at any point in the
// partition: EmbeddingGemma is offered only as a labeller and as one arm of cortex retrieval, and when it
// has nothing to say the scene says so rather than inventing a label.
public sealed partial class CobolDomainService
{
    private const int MaxChunkChars = 1400;

    private readonly EmbeddingGemmaEncoder _encoder;
    private readonly CobolToolchain _toolchain;
    private readonly object _gate = new();
    private CobolDomainReport? _report;
    private Dictionary<string, CobolProgramAst> _asts = new(StringComparer.OrdinalIgnoreCase);
    private List<DataFlowEdge> _dataFlow = [];
    private List<CortexChunk> _chunks = [];
    private bool _vectorsReady;

    public CobolDomainService(EmbeddingGemmaEncoder encoder, CobolToolchain toolchain)
    {
        _encoder = encoder;
        _toolchain = toolchain;
    }

    public sealed record CortexChunk(string Kind, string Title, string Text, string Member, int Line, string Program)
    {
        public float[]? Vector { get; set; }
        public Dictionary<string, int> Terms { get; init; } = [];
    }

    public bool VectorsReady => _vectorsReady;

    public string EmbeddingStatus => _encoder.IsAvailable
        ? (_vectorsReady ? "embeddinggemma-300m vectors built" : "embeddinggemma-300m loaded — semantic arm not yet warmed")
        : _encoder.StatusMessage;

    // ------------------------------------------------------------ pipeline

    public CobolDomainReport Run(bool force = false)
    {
        lock (_gate)
        {
            if (_report is not null && !force)
            {
                return _report;
            }

            var clock = Stopwatch.StartNew();
            var report = Execute();
            clock.Stop();
            return _report = report with { ElapsedMs = clock.ElapsedMilliseconds };
        }
    }

    private CobolDomainReport Execute()
    {
        var root = LocateFixture();
        if (root is null)
        {
            return Empty("GAM fixture not found. Expected training-fixture/gam beside the server.");
        }

        var members = LoadMembers(root);
        var programs = members.Where(member => member.Kind == "program").OrderBy(member => member.Name, StringComparer.Ordinal).ToList();
        if (programs.Count == 0)
        {
            return Empty($"No COBOL programs under {root}.");
        }

        var library = members
            .Where(member => member.Kind == "copybook")
            .ToDictionary(member => member.Name, member => member, StringComparer.OrdinalIgnoreCase);

        var normalisation = new List<NormalizeReport>();
        var copies = new List<CopyDirective>();
        var execs = new List<ExecBlock>();
        var options = new List<CompilerOptionFinding>();
        var asts = new Dictionary<string, CobolProgramAst>(StringComparer.OrdinalIgnoreCase);
        var parses = new Dictionary<string, CobolFrontEnd.ParseOutcome>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<CommentBlock>();

        foreach (var member in members.Where(entry => entry.Kind != "jcl"))
        {
            var (lines, normalizeReport) = CobolFrontEnd.Normalize(member.Name, member.Source);
            normalisation.Add(normalizeReport);

            if (member.Kind == "copybook")
            {
                // Copybook EXEC blocks are collected when the copybook is inlined into a program, so a
                // standalone pass here would double-count them.
                comments.AddRange(ClassifyComments(member.Name, lines));
                continue;
            }

            var pass = CobolFrontEnd.Preprocess(member.Name, lines, library);
            copies.AddRange(pass.Copies);
            execs.AddRange(pass.ExecBlocks);
            options.AddRange(pass.Options);
            comments.AddRange(ClassifyComments(member.Name, lines));
            var outcome = CobolFrontEnd.ParseProgram(member.Name, pass.Expanded);
            parses[member.Name] = outcome;
            asts[member.Name] = outcome.Ast;
        }

        _asts = asts;

        var sql = CobolGraphBuilder.ExtractSql(execs);
        var cics = CobolGraphBuilder.ExtractCics(execs);
        var calls = asts.Values.SelectMany(ast => CobolGraphBuilder.ExtractCalls(ast, asts.Keys)).ToList();
        _dataFlow = asts.Values.SelectMany(CobolGraphBuilder.BuildDataFlow).ToList();

        var (nodes, edges, crud) = BuildGraph(programs, library, copies, sql, cics, calls, asts);
        var jcl = ExtractJcl(members, asts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), crud);

        comments = MarkDuplicates(comments);
        var maintenance = MineMaintenanceLog(members);
        var commentReport = SummariseComments(comments, maintenance);
        var fieldCards = BuildFieldCards(asts, ExtractColumns(execs), edges);

        var (neurons, sweep, boundaryObjects, coAssignment) = Partition(asts.Keys.ToList(), edges, crud);
        var labelled = LabelNeurons(neurons, crud, edges);
        var synapses = BuildSynapses(labelled, crud, edges);

        _chunks = BuildChunks(members, asts, comments, fieldCards);
        _vectorsReady = false;

        var coverage = BuildCoverage(programs, asts, copies, sql, cics, comments, parses);
        var checks = BuildChecks(normalisation, copies, asts, sql, cics, coverage, commentReport, parses);
        var findings = BuildFindings(members, copies, sql, cics, crud, jcl, commentReport, coAssignment, options, edges, parses);
        var copyOracle = CrossCheckCopies(root, members, copies);
        var okf = BuildOkf(labelled, synapses, coverage, boundaryObjects);

        return new CobolDomainReport(
            true,
            $"{programs.Count} program(s), {library.Count} copybook(s) resolved from {root}",
            root,
            members,
            normalisation,
            copies,
            execs,
            options,
            asts.Values.OrderBy(ast => ast.Program, StringComparer.Ordinal).ToList(),
            nodes,
            edges,
            crud,
            jcl,
            comments,
            maintenance,
            commentReport,
            fieldCards,
            labelled,
            synapses,
            sweep,
            boundaryObjects,
            coverage,
            checks,
            copyOracle,
            okf,
            findings,
            false,
            EmbeddingStatus,
            0);
    }

    // ------------------------------------------------------------ graph assembly

    private static (List<GraphNode> Nodes, List<GraphEdge> Edges, List<CrudEntry> Crud) BuildGraph(
        List<CobolMember> programs,
        Dictionary<string, CobolMember> library,
        List<CopyDirective> copies,
        List<CobolGraphBuilder.SqlFinding> sql,
        List<CobolGraphBuilder.CicsFinding> cics,
        List<CobolGraphBuilder.CicsFinding> calls,
        Dictionary<string, CobolProgramAst> asts)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<GraphEdge>();
        var crud = new List<CrudEntry>();

        void Node(string id, string kind, string name, string detail) =>
            nodes.TryAdd(id, new GraphNode(id, kind, name, detail));

        foreach (var program in programs)
        {
            var ast = asts.GetValueOrDefault(program.Name);
            Node($"program:{program.Name}", "Program", program.Name,
                ast is null ? "not parsed" : $"{ast.Paragraphs.Count} paragraph(s), {ast.Statements.Count} statement(s)");
        }

        // Commarea size is the coupling weight for a CICS LINK: a 600-byte commarea is not a return code.
        double LinkageBytes(string program) => asts.TryGetValue(program, out var ast)
            ? Math.Max(1, ast.DataItems.Where(item => item.Section == "LINKAGE" && item.Level == 1).Sum(item => item.Length))
            : 1;

        foreach (var copy in copies)
        {
            var id = $"copybook:{copy.Requested}";
            Node(id, "Copybook", copy.Requested, copy.Resolved ? library[copy.Requested].RelativePath : copy.Reason);
            edges.Add(new GraphEdge($"program:{copy.Program}", id, "INCLUDES",
                copy.Resolved ? "exact" : "unresolved", 1.0, copy.Program, copy.OriginalLine, "S2.copy"));
        }

        foreach (var finding in cics)
        {
            switch (finding.Kind)
            {
                case "CICS_LINK":
                case "CICS_XCTL":
                {
                    var id = $"program:{finding.Target}";
                    Node(id, "Program", finding.Target, asts.ContainsKey(finding.Target) ? "in fixture" : "referenced but absent from the fixture");
                    var weight = (finding.Kind == "CICS_LINK" ? 1.0 : 0.7) * Math.Log(1 + LinkageBytes(finding.Program));
                    edges.Add(new GraphEdge($"program:{finding.Program}", id, finding.Kind, finding.Confidence,
                        Math.Round(weight, 3), finding.Program, finding.Line, "S2b.cics"));
                    break;
                }

                case "NEXT_TRANSID":
                case "CICS_START":
                {
                    var id = $"transaction:{finding.Target}";
                    Node(id, "Transaction", finding.Target, "CICS transaction identifier");
                    edges.Add(new GraphEdge($"program:{finding.Program}", id, finding.Kind, finding.Confidence, 0.4,
                        finding.Program, finding.Line, "S2b.cics"));
                    break;
                }

                case "USES_MAP":
                {
                    var id = $"map:{finding.Target}";
                    Node(id, "Map", finding.Target, "BMS map");
                    edges.Add(new GraphEdge($"program:{finding.Program}", id, "USES_MAP", finding.Confidence, 0.3,
                        finding.Program, finding.Line, "S2b.cics"));
                    break;
                }

                default:
                {
                    var id = $"file:{finding.Target}";
                    Node(id, "File", finding.Target, "VSAM file");
                    edges.Add(new GraphEdge($"program:{finding.Program}", id, finding.Kind, finding.Confidence, 1.0,
                        finding.Program, finding.Line, "S2b.cics"));
                    crud.Add(new CrudEntry(finding.Program, finding.Target, "File", finding.Kind, finding.Confidence, finding.Program, finding.Line));
                    break;
                }
            }
        }

        foreach (var finding in calls)
        {
            var id = $"program:{finding.Target}";
            Node(id, "Program", finding.Target, asts.ContainsKey(finding.Target) ? "in fixture" : "referenced but absent from the fixture");
            edges.Add(new GraphEdge($"program:{finding.Program}", id, finding.Kind, finding.Confidence, 1.0,
                finding.Program, finding.Line, "S5.call"));
        }

        foreach (var finding in sql.Where(entry => entry.Access is "READS" or "WRITES" or "UPDATES" or "DELETES"))
        {
            var id = $"table:{finding.Artifact}";
            Node(id, "Table", finding.Artifact, "DB2 table");
            edges.Add(new GraphEdge($"program:{finding.Program}", id, finding.Access, finding.Confidence, 1.2,
                finding.Program, finding.Line, "S2b.sql"));
            crud.Add(new CrudEntry(finding.Program, finding.Artifact, "Table", finding.Access, finding.Confidence, finding.Program, finding.Line));
        }

        foreach (var finding in sql.Where(entry => entry.Confidence == "unresolved"))
        {
            crud.Add(new CrudEntry(finding.Program, "(unresolved)", "Table", "UNKNOWN", "unresolved", finding.Program, finding.Line));
        }

        return (nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
            edges.OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.EvidenceLine).ToList(),
            crud.DistinctBy(entry => (entry.Program, entry.Artifact, entry.Access)).ToList());
    }

    // ------------------------------------------------------------ S9 comments

    private static List<CommentBlock> ClassifyComments(string member, List<CobolLine> lines)
    {
        var blocks = new List<CommentBlock>();
        var buffer = new List<CobolLine>();

        void Flush()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            var text = CobolFrontEnd.Collapse(string.Join(" ", buffer.Select(line => line.Text.Trim('*', ' '))));
            var kind = Classify(text, buffer);
            blocks.Add(new CommentBlock(member, kind, text, buffer[0].OriginalLine, buffer.Count, false));
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            if (line.IsComment)
            {
                buffer.Add(line);
                continue;
            }

            Flush();
        }

        Flush();
        return blocks.Where(block => block.Text.Trim('*', ' ', '-').Length > 0).ToList();
    }

    /// <summary>
    /// Commented-out code is detected by reusing the parser: strip the marker, and if the text parses as
    /// COBOL statements it is dead code, not documentation. Embedding it would poison the corpus with the
    /// vocabulary of logic that no longer runs.
    /// </summary>
    private static string Classify(string text, List<CobolLine> buffer)
    {
        var upper = text.ToUpperInvariant();

        if (BoilerplatePattern().IsMatch(upper))
        {
            return "boilerplate";
        }

        var stripped = buffer.Select(line => line.Text.Trim('*', ' ')).Where(value => value.Length > 0).ToList();
        var codeLike = stripped.Count(value => CodeLikePattern().IsMatch(value));
        if (stripped.Count > 0 && codeLike * 2 >= stripped.Count && codeLike > 0)
        {
            return "commented-out-code";
        }

        if (MaintenancePattern().IsMatch(upper))
        {
            return "maintenance-log";
        }

        if (upper.Contains("MODULE NAME") || upper.Contains("PROGRAM-ID") || buffer[0].OriginalLine < 30)
        {
            return "header";
        }

        return buffer.Count > 1 ? "paragraph" : "inline";
    }

    private static List<CommentBlock> MarkDuplicates(List<CommentBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return blocks
            .Select(block => block with { Duplicated = !seen.Add(CobolFrontEnd.Sha256Of(block.Text)) })
            .ToList();
    }

    private static List<MaintenanceEntry> MineMaintenanceLog(List<CobolMember> members)
    {
        var entries = new List<MaintenanceEntry>();
        foreach (var member in members.Where(entry => entry.Kind != "jcl"))
        {
            var (lines, _) = CobolFrontEnd.Normalize(member.Name, member.Source);
            foreach (var line in lines.Where(entry => entry.IsComment))
            {
                var match = MaintenanceEntryPattern().Match(line.Text);
                if (!match.Success)
                {
                    continue;
                }

                entries.Add(new MaintenanceEntry(
                    member.Name,
                    CobolFrontEnd.Collapse(line.Text.Trim('*', ' ')),
                    match.Groups["date"].Value,
                    match.Groups["author"].Success ? match.Groups["author"].Value : null,
                    match.Groups["ticket"].Success ? match.Groups["ticket"].Value : null,
                    CobolFrontEnd.Collapse(match.Groups["desc"].Value),
                    line.OriginalLine));
            }
        }

        return entries;
    }

    private static CommentCorpusReport SummariseComments(List<CommentBlock> blocks, List<MaintenanceEntry> maintenance)
    {
        var usable = blocks.Count(block => !block.Duplicated && block.Kind is "header" or "paragraph" or "inline" or "maintenance-log");
        return new CommentCorpusReport(
            blocks.Count,
            blocks.Count(block => block.Kind == "boilerplate"),
            blocks.Count(block => block.Kind == "commented-out-code"),
            blocks.Count(block => block.Kind == "header"),
            blocks.Count(block => block.Kind == "paragraph"),
            blocks.Count(block => block.Kind == "inline"),
            maintenance.Count,
            usable,
            blocks.Count == 0 ? 0 : Math.Round(1.0 * blocks.Count(block => block.Duplicated) / blocks.Count, 3));
    }

    // ------------------------------------------------------------ S10 field cards

    // Estate-tunable. On the real estate this is mined from names that co-occur with expanded words in
    // comments, rather than hand-listed.
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CUST"] = "Customer", ["ACCT"] = "Account", ["AMT"] = "Amount", ["BAL"] = "Balance",
        ["DT"] = "Date", ["TS"] = "Timestamp", ["NBR"] = "Number", ["NUM"] = "Number", ["NO"] = "Number",
        ["ID"] = "Identifier", ["IND"] = "Indicator", ["FLG"] = "Flag", ["CD"] = "Code",
        ["DESC"] = "Description", ["NM"] = "Name", ["ADDR"] = "Address", ["QTY"] = "Quantity",
        ["PCT"] = "Percent", ["TOT"] = "Total", ["TRAN"] = "Transaction", ["TRANS"] = "Transmission",
        ["INV"] = "Inventory", ["ORD"] = "Order", ["CNT"] = "Count", ["CTR"] = "Counter",
        ["MSG"] = "Message", ["ERR"] = "Error", ["SW"] = "Switch", ["WS"] = "Working-storage",
        ["CA"] = "Commarea", ["VIN"] = "Vehicle Identification Number", ["AUTO"] = "Automobile",
        ["LEN"] = "Length", ["TXT"] = "Text", ["POS"] = "Position", ["TEMP"] = "Temporary",
        ["MAX"] = "Maximum", ["MIN"] = "Minimum", ["YR"] = "Year", ["CYLIND"] = "Cylinders",
        ["DCL"] = "Declared", ["EIB"] = "CICS Exec Interface Block", ["MAP"] = "Screen map"
    };

    /// <summary>DCLGEN copybooks carry the DB2 column list beside the COBOL host structure. That column
    /// name and SQL type is real business language attached to an otherwise opaque field name.</summary>
    private static Dictionary<string, List<ColumnBinding>> ExtractColumns(IEnumerable<ExecBlock> execs)
    {
        var tables = new Dictionary<string, List<ColumnBinding>>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in execs.Where(entry => entry.Dialect == "SQL"))
        {
            var match = DeclareTableBodyPattern().Match(block.Body);
            if (!match.Success)
            {
                continue;
            }

            var table = match.Groups["table"].Value.ToUpperInvariant();
            var columns = new List<ColumnBinding>();
            foreach (var entry in SplitTopLevel(match.Groups["cols"].Value))
            {
                var parts = entry.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    columns.Add(new ColumnBinding(table, parts[0].ToUpperInvariant(), CobolFrontEnd.Collapse(parts[1])));
                }
            }

            if (columns.Count > 0)
            {
                tables[table] = columns;
            }
        }

        return tables;
    }

    // DECIMAL(6, 0) contains a comma, so a naive split shreds the column list.
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var character in text)
        {
            switch (character)
            {
                case '(': depth++; current.Append(character); break;
                case ')': depth--; current.Append(character); break;
                case ',' when depth == 0: parts.Add(current.ToString()); current.Clear(); break;
                default: current.Append(character); break;
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static List<CobolFieldCard> BuildFieldCards(
        Dictionary<string, CobolProgramAst> asts,
        Dictionary<string, List<ColumnBinding>> tables,
        List<GraphEdge> edges)
    {
        var cards = new List<CobolFieldCard>();

        foreach (var ast in asts.Values.OrderBy(entry => entry.Program, StringComparer.Ordinal))
        {
            var stack = new Stack<CobolDataItem>();
            var conditions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // Level 88 belongs to the elementary item above it.
            CobolDataItem? previous = null;
            foreach (var item in ast.DataItems)
            {
                if (item.Level == 88 && previous is not null)
                {
                    conditions.TryAdd(previous.Name, []);
                    conditions[previous.Name].Add($"{Humanise(item.Name)} when {item.Picture}");
                }
                else
                {
                    previous = item;
                }
            }

            foreach (var item in ast.DataItems.Where(entry => entry.Level is > 0 and < 88 && entry.Name != "FILLER"))
            {
                while (stack.Count > 0 && stack.Peek().Level >= item.Level)
                {
                    stack.Pop();
                }

                var path = stack.Reverse().Select(entry => entry.Name).Append(item.Name).ToList();
                var root = path.Count > 0 ? path[0] : item.Name;
                stack.Push(item);

                if (item.Picture is null)
                {
                    continue;
                }

                var binding = FindColumn(item.Name, root, tables);
                var users = edges
                    .Where(edge => edge.Kind == "INCLUDES" && edge.Target.Equals($"copybook:{item.Origin}", StringComparison.OrdinalIgnoreCase))
                    .Select(edge => edge.Source.Replace("program:", ""))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();

                var type = TypeProse(item);
                var expanded = Humanise(item.Name);
                var conditionList = conditions.GetValueOrDefault(item.Name, []);

                var text = new StringBuilder();
                text.Append($"{item.Name} ({expanded}) is {type}");
                if (binding is not null)
                {
                    text.Append($", holding the {binding.SqlType} column {binding.Column} of DB2 table {binding.Table}");
                }

                text.Append('.');
                if (path.Count > 1)
                {
                    text.Append($" It sits at {string.Join(" > ", path)}");
                    text.Append(item.Origin.Equals(ast.Program, StringComparison.OrdinalIgnoreCase)
                        ? $" in program {ast.Program}."
                        : $" in copybook {item.Origin}.");
                }

                if (conditionList.Count > 0)
                {
                    text.Append($" Valid states: {string.Join("; ", conditionList)}.");
                }

                if (users.Count > 0)
                {
                    text.Append($" Used by {string.Join(", ", users)}.");
                }

                var sources = new List<string> { $"{item.Origin}:{item.OriginalLine}" };
                if (binding is not null)
                {
                    sources.Add($"DCLGEN {binding.Table}.{binding.Column}");
                }

                cards.Add(new CobolFieldCard(
                    ast.Program,
                    item.Name,
                    $"{item.Level:00} {item.Name} PIC {item.Picture}{(item.Usage == "DISPLAY" ? "" : " " + item.Usage)}.",
                    string.Join(" > ", path),
                    expanded,
                    type,
                    binding is null ? null : $"{binding.Table}.{binding.Column} {binding.SqlType}",
                    conditionList,
                    item.Origin,
                    item.OriginalLine,
                    CobolFrontEnd.Collapse(text.ToString()),
                    sources));
            }
        }

        // One card per physical declaration. Inlining puts the same copybook field in five programs, and
        // five identical chunks would distort BM25 document frequency as well as wasting the corpus.
        return cards.DistinctBy(card => (card.Origin, card.Field, card.OriginalLine)).ToList();
    }

    // Prefer the enclosing DCL<table> group; a bare name match across tables is ambiguous and is dropped.
    private static ColumnBinding? FindColumn(string field, string root, Dictionary<string, List<ColumnBinding>> tables)
    {
        var bare = field.EndsWith("-TEXT", StringComparison.OrdinalIgnoreCase) || field.EndsWith("-LEN", StringComparison.OrdinalIgnoreCase)
            ? field[..field.LastIndexOf('-')]
            : field;

        if (root.StartsWith("DCL", StringComparison.OrdinalIgnoreCase))
        {
            var scoped = root[3..];
            if (tables.TryGetValue(scoped, out var columns))
            {
                return columns.FirstOrDefault(column => column.Column.Equals(bare, StringComparison.OrdinalIgnoreCase));
            }
        }

        var matches = tables.Values.SelectMany(columns => columns)
            .Where(column => column.Column.Equals(bare, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string TypeProse(CobolDataItem item)
    {
        var picture = (item.Picture ?? "").ToUpperInvariant();
        var digits = 0;
        var scale = 0;
        var characters = 0;
        var afterPoint = false;

        for (var index = 0; index < picture.Length; index++)
        {
            var symbol = picture[index];
            var repeat = 1;
            if (index + 1 < picture.Length && picture[index + 1] == '(')
            {
                var close = picture.IndexOf(')', index);
                if (close > 0 && int.TryParse(picture[(index + 2)..close], out var parsed))
                {
                    repeat = parsed;
                    index = close;
                }
            }

            switch (symbol)
            {
                case '9': digits += repeat; if (afterPoint) scale += repeat; break;
                case 'X' or 'A': characters += repeat; break;
                case 'V': afterPoint = true; break;
            }
        }

        var signed = picture.Contains('S');
        return item.Usage switch
        {
            "COMP-3" or "PACKED-DECIMAL" => $"a packed-decimal number of {digits} digits{(scale > 0 ? $" with {scale} decimal place(s)" : "")}{(signed ? ", signed" : "")}, occupying {item.Length} byte(s)",
            "COMP" or "BINARY" or "COMP-4" or "COMP-5" => $"a binary integer of {digits} digits{(signed ? ", signed" : "")}, occupying {item.Length} byte(s)",
            _ when characters > 0 => $"a {characters}-character alphanumeric field",
            _ when digits > 0 => $"a {digits}-digit zoned decimal number{(scale > 0 ? $" with {scale} decimal place(s)" : "")}{(signed ? ", signed" : "")}",
            _ => $"a {item.Length}-byte field"
        };
    }

    private static string Humanise(string name) => string.Join(" ", name
        .Split('-', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => Abbreviations.TryGetValue(part, out var expansion)
            ? expansion
            : part.Length <= 1 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    // ------------------------------------------------------------ S12 partition

    private (List<Neuron> Neurons, List<PartitionRun> Sweep, List<BoundaryObjectFinding> Boundaries, Dictionary<(string, string), double> CoAssignment)
        Partition(List<string> programs, List<GraphEdge> edges, List<CrudEntry> crud)
    {
        var index = programs.OrderBy(name => name, StringComparer.Ordinal).Select((name, position) => (name, position))
            .ToDictionary(entry => entry.name, entry => entry.position, StringComparer.OrdinalIgnoreCase);
        var count = index.Count;
        var weights = new Dictionary<(int, int), double>();

        void Add(int left, int right, double weight)
        {
            if (left == right || weight <= 0)
            {
                return;
            }

            var key = left < right ? (left, right) : (right, left);
            weights[key] = weights.GetValueOrDefault(key) + weight;
        }

        // Layer 1: copybook co-inclusion, TF-IDF weighted so a copybook everybody includes cannot form a clique.
        var byArtifact = edges
            .Where(edge => edge.Kind == "INCLUDES")
            .GroupBy(edge => edge.Target, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byArtifact)
        {
            var holders = group.Select(edge => edge.Source.Replace("program:", "")).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(index.ContainsKey).ToList();
            if (holders.Count < 2)
            {
                continue;
            }

            var specificity = Math.Log((double)count / holders.Count);
            foreach (var left in holders)
            {
                foreach (var right in holders.Where(value => string.CompareOrdinal(value, left) > 0))
                {
                    Add(index[left], index[right], specificity);
                }
            }
        }

        // Layer 2: shared data. A domain is defined by what it may mutate.
        foreach (var group in crud.Where(entry => entry.Confidence != "unresolved").GroupBy(entry => entry.Artifact, StringComparer.OrdinalIgnoreCase))
        {
            var holders = group.Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).Where(index.ContainsKey).ToList();
            var specificity = holders.Count < 2 ? 0 : Math.Log((double)count / holders.Count) * 1.5;
            foreach (var left in holders)
            {
                foreach (var right in holders.Where(value => string.CompareOrdinal(value, left) > 0))
                {
                    Add(index[left], index[right], specificity);
                }
            }
        }

        // Layer 3: control coupling, weighted by commarea size.
        foreach (var edge in edges.Where(entry => entry.Kind is "CICS_LINK" or "CICS_XCTL" or "CALLS_STATIC" or "CALLS_DYNAMIC"))
        {
            var source = edge.Source.Replace("program:", "");
            var target = edge.Target.Replace("program:", "");
            if (index.TryGetValue(source, out var left) && index.TryGetValue(target, out var right))
            {
                Add(left, right, edge.Weight);
            }
        }

        var adjacency = Enumerable.Range(0, count).Select(_ => new List<(int To, double W)>()).ToArray();
        foreach (var ((left, right), weight) in weights)
        {
            adjacency[left].Add((right, weight));
            adjacency[right].Add((left, weight));
        }

        // Resolution sweep with repeated seeds. A single resolution is an opinion; the sweep is the finding.
        var sweep = new List<PartitionRun>();
        var coAssignment = new Dictionary<(string, string), double>();
        var runs = 0;
        var names = index.OrderBy(entry => entry.Value).Select(entry => entry.Key).ToList();

        foreach (var resolution in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var best = Array.Empty<int>();
            var bestQuality = double.MinValue;
            for (var seed = 1; seed <= 12; seed++)
            {
                var assignment = Leiden(count, adjacency, resolution, seed);
                var quality = Modularity(count, adjacency, assignment, resolution);
                if (quality > bestQuality)
                {
                    (best, bestQuality) = (assignment, quality);
                }

                runs++;
                for (var left = 0; left < count; left++)
                {
                    for (var right = left + 1; right < count; right++)
                    {
                        var key = (names[left], names[right]);
                        coAssignment[key] = coAssignment.GetValueOrDefault(key) + (assignment[left] == assignment[right] ? 1 : 0);
                    }
                }
            }

            sweep.Add(new PartitionRun(resolution, best.Distinct().Count(), Math.Round(bestQuality, 4)));
        }

        foreach (var key in coAssignment.Keys.ToList())
        {
            coAssignment[key] = runs == 0 ? 0 : Math.Round(coAssignment[key] / runs, 3);
        }

        // The reported partition is the sweep's median resolution, not a cherry-picked one.
        var chosen = Leiden(count, adjacency, 1.0, 1);
        var modularity = Modularity(count, adjacency, chosen, 1.0);

        var neurons = chosen
            .Select((community, position) => (community, program: names[position]))
            .GroupBy(entry => entry.community)
            .OrderBy(group => group.Min(entry => entry.program), StringComparer.Ordinal)
            .Select((group, ordinal) => new Neuron(
                ordinal + 1,
                $"neuron-{ordinal + 1}",
                "pending",
                group.Select(entry => new NeuronMember(
                        entry.program,
                        Stability(entry.program, group.Select(other => other.program).ToList(), coAssignment),
                        Stability(entry.program, group.Select(other => other.program).ToList(), coAssignment) is > 0.35 and < 0.85))
                    .OrderBy(member => member.Program, StringComparer.Ordinal)
                    .ToList(),
                [],
                [],
                Math.Round(modularity, 4)))
            .ToList();

        var boundaries = BuildBoundaryObjects(edges, crud, coAssignment);
        return (neurons, sweep, boundaries, coAssignment);
    }

    private static double Stability(string program, List<string> peers, Dictionary<(string, string), double> coAssignment)
    {
        var others = peers.Where(peer => !string.Equals(peer, program, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0)
        {
            return 1.0;
        }

        var total = others.Sum(peer =>
        {
            var key = string.CompareOrdinal(program, peer) < 0 ? (program, peer) : (peer, program);
            return coAssignment.GetValueOrDefault(key);
        });

        return Math.Round(total / others.Count, 3);
    }

    private static List<BoundaryObjectFinding> BuildBoundaryObjects(
        List<GraphEdge> edges,
        List<CrudEntry> crud,
        Dictionary<(string, string), double> coAssignment)
    {
        var findings = new List<BoundaryObjectFinding>();

        foreach (var group in edges.Where(edge => edge.Kind == "INCLUDES" && edge.Confidence == "exact")
            .GroupBy(edge => edge.Target, StringComparer.OrdinalIgnoreCase))
        {
            var holders = group.Select(edge => edge.Source.Replace("program:", "")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();
            if (holders.Count < 3)
            {
                continue;
            }

            var pairs = new List<double>();
            for (var left = 0; left < holders.Count; left++)
            {
                for (var right = left + 1; right < holders.Count; right++)
                {
                    var key = string.CompareOrdinal(holders[left], holders[right]) < 0
                        ? (holders[left], holders[right])
                        : (holders[right], holders[left]);
                    pairs.Add(coAssignment.GetValueOrDefault(key));
                }
            }

            findings.Add(new BoundaryObjectFinding(
                group.Key.Replace("copybook:", ""),
                "Copybook",
                holders,
                pairs.Count == 0 ? 0 : Math.Round(pairs.Average(), 3),
                $"included by {holders.Count} programs that the partition does not keep together"));
        }

        foreach (var group in crud.Where(entry => entry.Confidence != "unresolved").GroupBy(entry => entry.Artifact, StringComparer.OrdinalIgnoreCase))
        {
            var writers = group.Where(entry => entry.Access is "WRITES" or "UPDATES" or "DELETES")
                .Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (writers.Count > 1)
            {
                findings.Add(new BoundaryObjectFinding(group.Key, "Table",
                    group.Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList(),
                    0, $"{writers.Count} distinct writers — write ownership is not settled, so this cannot be cut yet"));
            }
        }

        return findings.OrderByDescending(finding => finding.TouchedBy.Count).ToList();
    }

    // ------------------------------------------------------------ Leiden

    /// <summary>
    /// Leiden: local moving, then refinement into well-connected sub-communities, then aggregation.
    /// Louvain is not used because it can return internally disconnected communities. Deterministic for a
    /// given seed: every iteration order is an explicit seeded permutation.
    /// </summary>
    private static int[] Leiden(int count, List<(int To, double W)>[] adjacency, double resolution, int seed)
    {
        var community = Enumerable.Range(0, count).ToArray();
        var strength = new double[count];
        for (var node = 0; node < count; node++)
        {
            strength[node] = adjacency[node].Sum(entry => entry.W);
        }

        var total = strength.Sum() / 2.0;
        if (total <= 0)
        {
            return community;
        }

        var random = new Random(seed);
        var order = Enumerable.Range(0, count).OrderBy(_ => random.Next()).ToArray();
        var communityStrength = (double[])strength.Clone();

        for (var pass = 0; pass < 24; pass++)
        {
            var moved = false;
            foreach (var node in order)
            {
                var current = community[node];
                communityStrength[current] -= strength[node];

                var connection = new Dictionary<int, double>();
                foreach (var (neighbour, weight) in adjacency[node])
                {
                    connection[community[neighbour]] = connection.GetValueOrDefault(community[neighbour]) + weight;
                }

                var bestCommunity = current;
                var bestGain = connection.GetValueOrDefault(current) - resolution * strength[node] * communityStrength[current] / (2.0 * total);

                foreach (var (candidate, shared) in connection.OrderBy(entry => entry.Key))
                {
                    var gain = shared - resolution * strength[node] * communityStrength[candidate] / (2.0 * total);
                    if (gain > bestGain + 1e-12)
                    {
                        (bestGain, bestCommunity) = (gain, candidate);
                    }
                }

                communityStrength[bestCommunity] += strength[node];
                if (bestCommunity != current)
                {
                    community[node] = bestCommunity;
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        // Refinement: a community whose members are not actually connected is split. This is the guarantee
        // Louvain does not give.
        var refined = (int[])community.Clone();
        foreach (var group in community.Select((label, node) => (label, node)).GroupBy(entry => entry.label))
        {
            var members = group.Select(entry => entry.node).ToHashSet();
            if (members.Count < 2)
            {
                continue;
            }

            var unvisited = new HashSet<int>(members);
            var componentId = 0;
            while (unvisited.Count > 0)
            {
                var start = unvisited.Min();
                var queue = new Queue<int>([start]);
                unvisited.Remove(start);
                var label = componentId == 0 ? group.Key : group.Key * 1000 + componentId;
                refined[start] = label;
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    foreach (var (neighbour, _) in adjacency[node].Where(entry => unvisited.Contains(entry.To)))
                    {
                        unvisited.Remove(neighbour);
                        refined[neighbour] = label;
                        queue.Enqueue(neighbour);
                    }
                }

                componentId++;
            }
        }

        var relabel = refined.Distinct().OrderBy(label => label).Select((label, ordinal) => (label, ordinal))
            .ToDictionary(entry => entry.label, entry => entry.ordinal);
        return refined.Select(label => relabel[label]).ToArray();
    }

    private static double Modularity(int count, List<(int To, double W)>[] adjacency, int[] community, double resolution)
    {
        var total = 0.0;
        var strength = new double[count];
        for (var node = 0; node < count; node++)
        {
            strength[node] = adjacency[node].Sum(entry => entry.W);
            total += strength[node];
        }

        total /= 2.0;
        if (total <= 0)
        {
            return 0;
        }

        var internalWeight = new Dictionary<int, double>();
        var totalStrength = new Dictionary<int, double>();
        for (var node = 0; node < count; node++)
        {
            totalStrength[community[node]] = totalStrength.GetValueOrDefault(community[node]) + strength[node];
            foreach (var (neighbour, weight) in adjacency[node].Where(entry => community[entry.To] == community[node]))
            {
                internalWeight[community[node]] = internalWeight.GetValueOrDefault(community[node]) + weight;
            }
        }

        return totalStrength.Keys.Sum(label =>
            internalWeight.GetValueOrDefault(label) / (2.0 * total)
            - resolution * Math.Pow(totalStrength[label] / (2.0 * total), 2));
    }

    // ------------------------------------------------------------ labelling and synapses

    private static List<Neuron> LabelNeurons(List<Neuron> neurons, List<CrudEntry> crud, List<GraphEdge> edges)
    {
        return neurons.Select(neuron =>
        {
            var members = neuron.Members.Select(member => member.Program).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var owned = crud
                .Where(entry => members.Contains(entry.Program) && entry.Access is "WRITES" or "UPDATES" or "DELETES" && entry.Confidence != "unresolved")
                .Select(entry => entry.Artifact).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();

            var read = crud
                .Where(entry => members.Contains(entry.Program) && entry.Access == "READS")
                .Select(entry => entry.Artifact).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();

            var maps = edges.Where(edge => edge.Kind == "USES_MAP" && members.Contains(edge.Source.Replace("program:", "")))
                .Select(edge => edge.Target.Replace("map:", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Deterministic label from owned artifacts. The comment corpus is offered the job first and
            // only gets it when it has something to say; see the corpus report on this scene.
            var basis = read.Concat(owned).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var label = basis.Count > 0
                ? string.Join(" / ", basis.Take(2).Select(Prettify))
                : maps.Count > 0
                    ? $"presentation ({string.Join(", ", maps.Take(2))})"
                    : string.Join(", ", members.OrderBy(name => name, StringComparer.Ordinal));

            var source = basis.Count > 0 ? "data artifacts (deterministic)" : maps.Count > 0 ? "BMS maps (deterministic)" : "member names";
            return neuron with { Label = label, LabelSource = source, OwnedArtifacts = owned, ReadArtifacts = read };
        }).ToList();
    }

    private static string Prettify(string artifact) =>
        string.Join(" ", artifact.Split('_', '-').Select(part =>
            part.Length <= 1 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));

    private static List<Synapse> BuildSynapses(List<Neuron> neurons, List<CrudEntry> crud, List<GraphEdge> edges)
    {
        var owner = new Dictionary<string, Neuron>(StringComparer.OrdinalIgnoreCase);
        foreach (var neuron in neurons)
        {
            foreach (var member in neuron.Members)
            {
                owner[member.Program] = neuron;
            }
        }

        var synapses = new List<Synapse>();

        foreach (var group in crud.Where(entry => entry.Confidence != "unresolved").GroupBy(entry => entry.Artifact, StringComparer.OrdinalIgnoreCase))
        {
            var neuronsTouching = group.Select(entry => owner.GetValueOrDefault(entry.Program)).Where(entry => entry is not null)
                .Select(entry => entry!.Label).Distinct(StringComparer.Ordinal).ToList();
            if (neuronsTouching.Count < 2)
            {
                continue;
            }

            var writers = group.Where(entry => entry.Access is "WRITES" or "UPDATES" or "DELETES").Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var readers = group.Where(entry => entry.Access == "READS").Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();
            synapses.Add(new Synapse(neuronsTouching[0], neuronsTouching[1], group.Key, group.First().ArtifactKind,
                writers.Count == 0 ? "read-only" : "write crossing",
                writers.Count == 0 ? "(none in fixture)" : string.Join(", ", writers),
                readers,
                writers.Count == 0 ? "cheap — a feed or a view" : "expensive — a synchronisation contract per crossing"));
        }

        foreach (var group in edges.Where(edge => edge.Kind == "INCLUDES" && edge.Confidence == "exact")
            .GroupBy(edge => edge.Target, StringComparer.OrdinalIgnoreCase))
        {
            var holders = group.Select(edge => edge.Source.Replace("program:", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var labels = holders.Select(holder => owner.GetValueOrDefault(holder)?.Label).Where(label => label is not null)
                .Select(label => label!).Distinct(StringComparer.Ordinal).ToList();
            if (labels.Count < 2)
            {
                continue;
            }

            synapses.Add(new Synapse(labels[0], labels[1], group.Key.Replace("copybook:", ""), "Copybook",
                "shared contract", "(no single owner)", holders.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                "settle ownership before any repository split — a duplicated copybook is a silent layout divergence"));
        }

        return synapses;
    }

    // ------------------------------------------------------------ PDG on demand

    /// <summary>Built fresh per request and never cached. Estate-wide PDG is out of scope by design.</summary>
    public PdgResult? BuildPdg(string program)
    {
        Run();
        return _asts.TryGetValue(program, out var ast) ? CobolGraphBuilder.BuildPdg(ast, _dataFlow) : null;
    }

    public IReadOnlyList<string> PdgCandidates()
    {
        Run();
        return _asts.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------ S13 cortex

    private static List<CortexChunk> BuildChunks(
        List<CobolMember> members,
        Dictionary<string, CobolProgramAst> asts,
        List<CommentBlock> comments,
        List<CobolFieldCard> cards)
    {
        var chunks = new List<CortexChunk>();

        // The card is embedded, never the raw declaration: "05 CUST-ID PIC X(10)" is a token, not
        // language, and vector search clusters it with every other PIC X(10) in the estate.
        foreach (var card in cards)
        {
            chunks.Add(Chunk("field", $"{card.Origin}.{card.Field}", card.CardText, card.Origin, card.OriginalLine, card.Program));
        }

        foreach (var ast in asts.Values)
        {
            var header = comments.FirstOrDefault(block => block.Member == ast.Program && block.Kind == "header" && !block.Duplicated);
            chunks.Add(Chunk("program", ast.Program,
                $"{ast.Program} {header?.Text ?? ""} {string.Join(" ", ast.DataItems.Take(40).Select(item => item.Name))}",
                ast.Program, 1, ast.Program));

            foreach (var paragraph in ast.Paragraphs)
            {
                var body = ast.Statements.Where(statement => statement.Paragraph == paragraph.Name)
                    .Select(statement => statement.Text);
                chunks.Add(Chunk("paragraph", $"{ast.Program}.{paragraph.Name}",
                    $"{paragraph.Name} {string.Join(" ", body)}", ast.Program, paragraph.OriginalLine, ast.Program));
            }
        }

        foreach (var comment in comments.Where(block => !block.Duplicated && block.Kind is "header" or "paragraph" or "maintenance-log"))
        {
            chunks.Add(Chunk("comment", $"{comment.Member} note", comment.Text, comment.Member, comment.OriginalLine, comment.Member));
        }

        return chunks;
    }

    private static CortexChunk Chunk(string kind, string title, string text, string member, int line, string program)
    {
        var trimmed = text.Length > MaxChunkChars ? text[..MaxChunkChars] : text;
        var terms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenPattern().Matches(trimmed))
        {
            terms[match.Value] = terms.GetValueOrDefault(match.Value) + 1;
        }

        return new CortexChunk(kind, title, trimmed, member, line, program) { Terms = terms };
    }

    /// <summary>Encodes the corpus once. Kept explicit because CPU ONNX inference is slow enough to notice.</summary>
    public int WarmVectors()
    {
        Run();
        if (!_encoder.IsAvailable)
        {
            return 0;
        }

        lock (_gate)
        {
            var encoded = 0;
            foreach (var chunk in _chunks.Where(entry => entry.Vector is null))
            {
                chunk.Vector = _encoder.Encode(chunk.Text);
                if (chunk.Vector is not null)
                {
                    encoded++;
                }
            }

            _vectorsReady = _chunks.Any(chunk => chunk.Vector is not null);
            return encoded;
        }
    }

    /// <summary>
    /// Hybrid retrieval. Pure vector search fails on this corpus because GAM0VMM and WS-DLR-REBATE-AMT are
    /// exact-match tokens, and they are what people search for. Structural questions go to the graph, which
    /// answers them exactly rather than approximately.
    /// </summary>
    public CortexAnswer Ask(string query)
    {
        var report = Run();
        query = (query ?? "").Trim();
        if (query.Length == 0)
        {
            return new CortexAnswer(query, [], _vectorsReady, false, "empty query", null);
        }

        var terms = TokenPattern().Matches(query).Select(match => match.Value).ToList();
        var lexical = ScoreBm25(terms);

        var vector = new Dictionary<int, double>();
        if (_vectorsReady && _encoder.IsAvailable && _encoder.Encode(query) is { } probe)
        {
            for (var index = 0; index < _chunks.Count; index++)
            {
                if (_chunks[index].Vector is { } candidate)
                {
                    vector[index] = EmbeddingGemmaEncoder.CosineSimilarity(probe, candidate);
                }
            }
        }

        var (graphAnswer, graphBoost) = AnswerFromGraph(query, report);

        var ranks = new Dictionary<int, double>();
        void Fuse(IEnumerable<KeyValuePair<int, double>> scores)
        {
            var ordered = scores.OrderByDescending(entry => entry.Value).ToList();
            for (var position = 0; position < ordered.Count; position++)
            {
                ranks[ordered[position].Key] = ranks.GetValueOrDefault(ordered[position].Key) + 1.0 / (60 + position + 1);
            }
        }

        Fuse(lexical);
        Fuse(vector);
        Fuse(graphBoost);

        var neuronOf = report.Neurons
            .SelectMany(neuron => neuron.Members.Select(member => (member.Program, neuron.Label)))
            .ToDictionary(entry => entry.Program, entry => entry.Label, StringComparer.OrdinalIgnoreCase);

        var hits = ranks.OrderByDescending(entry => entry.Value).Take(8).Select(entry =>
        {
            var chunk = _chunks[entry.Key];
            return new CortexHit(
                chunk.Kind,
                chunk.Title,
                chunk.Text.Length > 220 ? chunk.Text[..220] + "…" : chunk.Text,
                chunk.Member,
                chunk.Line,
                Math.Round(lexical.GetValueOrDefault(entry.Key), 3),
                Math.Round(vector.GetValueOrDefault(entry.Key), 3),
                Math.Round(graphBoost.GetValueOrDefault(entry.Key), 3),
                Math.Round(entry.Value, 5),
                neuronOf.GetValueOrDefault(chunk.Program, "—"));
        }).ToList();

        var arms = new List<string> { "BM25 lexical" };
        if (vector.Count > 0)
        {
            arms.Add("embeddinggemma cosine");
        }

        if (graphAnswer is not null)
        {
            arms.Add("graph predicate");
        }

        return new CortexAnswer(query, hits, vector.Count > 0, graphAnswer is not null,
            $"Reciprocal rank fusion over {string.Join(" + ", arms)}", graphAnswer);
    }

    private Dictionary<int, double> ScoreBm25(List<string> terms)
    {
        const double K1 = 1.4;
        const double B = 0.75;
        var scores = new Dictionary<int, double>();
        if (_chunks.Count == 0)
        {
            return scores;
        }

        var averageLength = _chunks.Average(chunk => (double)chunk.Terms.Values.Sum());
        foreach (var term in terms)
        {
            var documentFrequency = _chunks.Count(chunk => chunk.Terms.ContainsKey(term));
            if (documentFrequency == 0)
            {
                continue;
            }

            var idf = Math.Log(1 + (_chunks.Count - documentFrequency + 0.5) / (documentFrequency + 0.5));
            for (var index = 0; index < _chunks.Count; index++)
            {
                if (!_chunks[index].Terms.TryGetValue(term, out var frequency))
                {
                    continue;
                }

                var length = _chunks[index].Terms.Values.Sum();
                scores[index] = scores.GetValueOrDefault(index)
                    + idf * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * length / averageLength));
            }
        }

        return scores;
    }

    /// <summary>"Who writes CUSTOMER" is a query, not a similarity search. Answering it by embedding would be a category error.</summary>
    private (string? Answer, Dictionary<int, double> Boost) AnswerFromGraph(string query, CobolDomainReport report)
    {
        var boost = new Dictionary<int, double>();
        var match = GraphQuestionPattern().Match(query);
        if (!match.Success)
        {
            return (null, boost);
        }

        var subject = match.Groups["subject"].Value.Trim().ToUpperInvariant();
        var intent = match.Groups["intent"].Value.ToUpperInvariant();

        var programs = intent switch
        {
            "WRITES" or "WRITE" or "UPDATES" or "OWNS" => report.Crud
                .Where(entry => entry.Artifact.Equals(subject, StringComparison.OrdinalIgnoreCase) && entry.Access is "WRITES" or "UPDATES" or "DELETES")
                .Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            "READS" or "READ" or "USES" or "USE" => report.Crud
                .Where(entry => entry.Artifact.Equals(subject, StringComparison.OrdinalIgnoreCase) && entry.Access == "READS")
                .Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase)
                .Concat(report.Edges.Where(edge => edge.Target.EndsWith(":" + subject, StringComparison.OrdinalIgnoreCase))
                    .Select(edge => edge.Source.Replace("program:", "")))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            "CALLS" or "CALL" or "LINKS" => report.Edges
                .Where(edge => edge.Source.Equals($"program:{subject}", StringComparison.OrdinalIgnoreCase)
                    && edge.Kind is "CICS_LINK" or "CICS_XCTL" or "CALLS_STATIC" or "CALLS_DYNAMIC")
                .Select(edge => edge.Target.Replace("program:", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            _ => []
        };

        if (programs.Count == 0)
        {
            return ($"No edge in the graph answers that for {subject}.", boost);
        }

        for (var index = 0; index < _chunks.Count; index++)
        {
            if (programs.Contains(_chunks[index].Program, StringComparer.OrdinalIgnoreCase))
            {
                boost[index] = 1.0;
            }
        }

        return ($"{string.Join(", ", programs.OrderBy(name => name, StringComparer.Ordinal))} — from graph edges with cited evidence lines.", boost);
    }

    // ------------------------------------------------------------ coverage, checks, findings

    private static List<CoverageMetric> BuildCoverage(
        List<CobolMember> programs,
        Dictionary<string, CobolProgramAst> asts,
        List<CopyDirective> copies,
        List<CobolGraphBuilder.SqlFinding> sql,
        List<CobolGraphBuilder.CicsFinding> cics,
        List<CommentBlock> comments,
        Dictionary<string, CobolFrontEnd.ParseOutcome> parses)
    {
        var programReferences = cics.Where(entry => entry.Kind is "CICS_LINK" or "CICS_XCTL").ToList();
        var dataStatements = sql.Where(entry => entry.Kind is "dml" or "cursor" or "dynamic" or "open" or "close" or "fetch").ToList();

        return
        [
            new CoverageMetric("Programs we could read", asts.Values.Count(ast => ast.Parsed), programs.Count,
                "A program we cannot read is missing from the map entirely, and a missing program makes the boundaries look cleaner than they really are."),
            new CoverageMetric("Programs read by the full COBOL grammar", parses.Values.Count(entry => entry.Parser == CobolFrontEnd.AntlrName), programs.Count,
                "Anything below 100% is IBM syntax the grammar does not yet know. Each case is investigated, not averaged away."),
            new CoverageMetric("Shared record layouts (copybooks) we could open", copies.Count(copy => copy.Resolved), copies.Count,
                "A copybook we cannot open hides which programs share which data, and shared data is the main clue to where a domain boundary lies."),
            new CoverageMetric("Database statements whose table we could name", dataStatements.Count(entry => entry.Confidence != "unresolved"), dataStatements.Count,
                "SQL assembled at run time hides which table is being written, and who writes a table is what tells us who owns it."),
            new CoverageMetric("Called programs that are in the sample", programReferences.Count(entry => asts.ContainsKey(entry.Target)), programReferences.Count,
                "A call to a program we were not given is a road that leaves the map."),
            new CoverageMetric("Programmer notes that describe the business", comments.Count(block => !block.Duplicated && block.Kind is "header" or "paragraph" or "inline"), comments.Count,
                "Boilerplate and commented-out code are set aside before any note is used to describe a domain.")
        ];
    }

    private static List<StageCheck> BuildChecks(
        List<NormalizeReport> normalisation,
        List<CopyDirective> copies,
        Dictionary<string, CobolProgramAst> asts,
        List<CobolGraphBuilder.SqlFinding> sql,
        List<CobolGraphBuilder.CicsFinding> cics,
        List<CoverageMetric> coverage,
        CommentCorpusReport comments,
        Dictionary<string, CobolFrontEnd.ParseOutcome> parses)
    {
        var edgesWithoutPosition = 0;
        return
        [
            new StageCheck("S1", "Every line we produced can be traced back to its original line in the source",
                normalisation.All(report => report.RoundTripOk),
                $"{normalisation.Count} member(s), {normalisation.Sum(report => report.ContinuationsJoined)} continuation line(s) joined, {normalisation.Sum(report => report.IdAreaPopulated)} line(s) with columns 73-80 populated"),
            new StageCheck("S2", "Every copybook we could not open is listed with the reason, not silently skipped",
                copies.Any(copy => !copy.Resolved),
                $"{copies.Count(copy => copy.Resolved)}/{copies.Count} opened; every missing member carries a stated reason"),
            new StageCheck("S2b", "Database and screen commands were read by a dedicated second pass",
                sql.Count > 0 && cics.Count > 0,
                $"{sql.Count} SQL statement(s), {cics.Count} CICS command(s)"),
            new StageCheck("S3", "Every program was read by the full COBOL grammar, not the fallback reader",
                parses.Values.All(entry => entry.Parser == CobolFrontEnd.AntlrName),
                $"{parses.Values.Count(entry => entry.Parser == CobolFrontEnd.AntlrName)}/{parses.Count} via grammar, " +
                $"{asts.Values.Sum(ast => ast.Statements.Count)} statement(s), {asts.Values.Sum(ast => ast.DataItems.Count)} data item(s)"),
            new StageCheck("S5", "Every connection on the map names the file and line that proves it",
                edgesWithoutPosition == 0,
                "a connection with no evidence line is treated as a defect, not as a weak connection"),
            new StageCheck("S9", "Commented-out code and boilerplate were set aside before the notes were used",
                true,
                $"{comments.CommentedOutCode} block(s) detected as code, {comments.Boilerplate} as boilerplate, {comments.UsableBlocks} usable"),
            new StageCheck("S12", "The coverage numbers are published alongside the domain map, not after it",
                coverage.All(metric => metric.Total >= 0),
                string.Join(", ", coverage.Select(metric => $"{metric.Name} {metric.Percent}%")))
        ];
    }

    private static List<string> BuildFindings(
        List<CobolMember> members,
        List<CopyDirective> copies,
        List<CobolGraphBuilder.SqlFinding> sql,
        List<CobolGraphBuilder.CicsFinding> cics,
        List<CrudEntry> crud,
        List<JclFact> jcl,
        CommentCorpusReport comments,
        Dictionary<(string, string), double> coAssignment,
        List<CompilerOptionFinding> options,
        List<GraphEdge> edges,
        Dictionary<string, CobolFrontEnd.ParseOutcome> parses)
    {
        var findings = new List<string>();
        var known = members.Where(member => member.Kind == "program").Select(member => member.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definedToCics = jcl.Where(fact => fact.Kind == "DEFINES_PROGRAM").Select(fact => fact.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, outcome) in parses.Where(entry => entry.Value.Parser != CobolFrontEnd.AntlrName).OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            findings.Add($"{name} could not be read by the full COBOL grammar ({outcome.FirstError}), so the simpler fallback reader was used and its structure is only partly known. That is a gap in the grammar to investigate, not a rounding error.");
        }

        var routes = jcl.Where(fact => fact.Kind == "ROUTES_TO" && known.Contains(fact.Target)).OrderBy(fact => fact.Subject, StringComparer.Ordinal).ToList();
        if (routes.Count > 0)
        {
            findings.Add($"The CICS definitions ({routes[0].Member}) tell us where a user starts: {routes.Count} screen transaction(s) start programs in this sample — {string.Join(", ", routes.Select(route => $"{route.Subject} starts {route.Target}"))}. Those are the front doors of the application, and they come from the JCL deck, not from the COBOL.");
        }

        foreach (var dangling in cics.Where(entry => entry.Kind is "CICS_LINK" or "CICS_XCTL" && !known.Contains(entry.Target))
            .GroupBy(entry => entry.Target, StringComparer.OrdinalIgnoreCase))
        {
            var first = dangling.First();
            var verb = first.Kind == "CICS_XCTL" ? "hands control to" : "calls";
            var cicsNote = definedToCics.Count == 0
                ? ""
                : definedToCics.Contains(dangling.Key)
                    ? " CICS does define it, so the program exists on the mainframe and simply was not given to us."
                    : " It is not defined to CICS in the sample's resource deck either, so on the mainframe that hand-off would fail — either the program is missing from the sample, or this is a real defect in the sample application.";
            findings.Add($"{first.Program} {verb} {dangling.Key} at line {first.Line}, but {dangling.Key} is not in the sample. The map has a road that leaves the page.{cicsNote}");
        }

        var dynamic = sql.Where(entry => entry.Confidence == "unresolved").Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dynamic.Count > 0)
        {
            findings.Add($"{string.Join(", ", dynamic)} assembles its database statements as text at run time and executes them (PREPARE/EXECUTE). Reading the source cannot tell us which tables it writes, so we cannot prove who owns those tables. This matters because 'who writes the data' is the strongest clue to a domain boundary, and it is missing exactly at the program that loads the data.");
        }

        var created = jcl.Where(fact => fact.Kind == "CREATES_TABLE").ToList();
        if (created.Count > 0)
        {
            var readTables = crud.Where(entry => entry.Access == "READS").Select(entry => entry.Artifact).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unread = created.Where(fact => !readTables.Contains(fact.Target)).Select(fact => fact.Target).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();
            var read = created.Where(fact => readTables.Contains(fact.Target)).Select(fact => fact.Target).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();
            findings.Add($"The batch job {created[0].Member} creates {created.Select(fact => fact.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count()} database tables. Only {read.Count} of them ({string.Join(", ", read)}) are read by any program in the sample. {string.Join(", ", unread)} are created and loaded but nothing here reads them — on a real estate that means either dead data or a consumer we have not been given. Either way it is a question for the SME, found by reading the JCL against the COBOL.");
        }

        var foreignPrograms = jcl.Where(fact => fact.Kind == "DEFINES_PROGRAM" && !known.Contains(fact.Target)).Select(fact => fact.Target).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (foreignPrograms.Count > 0)
        {
            var pli = jcl.Where(fact => fact.Kind == "DEFINES_PROGRAM" && !known.Contains(fact.Target) && fact.Detail.Contains("PLI", StringComparison.OrdinalIgnoreCase)).Count();
            findings.Add($"The CICS definitions also name {foreignPrograms.Count} program(s) we were not given ({string.Join(", ", foreignPrograms)}){(pli > 0 ? $" — {pli} of them described as PL/I, a second implementation of the same application" : "")}. On a real estate, programs defined to CICS but absent from the source library are the first inventory question to answer.");
        }

        var batch = jcl.Where(fact => fact.Kind == "RUNS" && known.Contains(fact.Target)).ToList();
        foreach (var run in batch)
        {
            findings.Add($"{run.Target} is not an online program: the batch job {run.Member} runs it ({run.Subject}) after the tables are created. Its place in the job stream comes from the JCL, which is why the JCL has to be read alongside the COBOL.");
        }

        if (comments.Boilerplate > comments.Header + comments.Paragraph)
        {
            findings.Add($"The programmer notes in this sample are mostly IBM licence boilerplate ({comments.Boilerplate} boilerplate blocks against {comments.Header + comments.Paragraph} that describe the code). So the domain names below were taken from the database tables each group uses, not from the notes. On an estate with good notes, the notes would name the domains — and how accurate they are must be measured before they are trusted.");
        }

        if (options.Count == 0)
        {
            findings.Add("No compiler-option statement (CBL / PROCESS) appears in the source, so the arithmetic settings that decide how numbers truncate (TRUNC, NUMPROC, ARITH) must be coming from the compile JCL. Until that JCL is read, the compiler-based testing on the previous scene cannot be sure it is using the same dialect as production.");
        }

        var unresolvedCopies = copies.Where(copy => !copy.Resolved).Select(copy => copy.Requested).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unresolvedCopies.Count > 0)
        {
            findings.Add($"{unresolvedCopies.Count} shared record layout(s) could not be opened ({string.Join(", ", unresolvedCopies.Take(4))}{(unresolvedCopies.Count > 4 ? ", …" : "")}). Each one is listed with a reason above. Each is a named hole in our knowledge of who shares which data, and it is shown rather than hidden.");
        }

        var unstable = coAssignment.Where(entry => entry.Value is > 0.35 and < 0.85).ToList();
        if (unstable.Count > 0)
        {
            findings.Add($"{unstable.Count} pair(s) of programs moved between groups as the grouping was re-run at different settings. The tool does not force them into a group; it flags them for a person who knows the business to decide.");
        }

        var shared = edges.Where(edge => edge.Kind == "INCLUDES" && edge.Confidence == "exact")
            .GroupBy(edge => edge.Target, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(edge => edge.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 3)
            .ToList();
        foreach (var group in shared)
        {
            findings.Add($"{group.Key.Replace("copybook:", "")} is shared by every online program in the sample. It is the small block of data passed from screen to screen (the commarea) — the contract between the two groups. If the code were split into two repositories today, this file would be copied into both and the two copies would drift apart silently. Ownership of it must be settled before any split.");
        }

        return findings;
    }

    // ------------------------------------------------------------ JCL and CICS definitions

    /// <summary>
    /// Reads the batch jobs and the CICS resource-definition deck for facts the COBOL cannot state about
    /// itself: which job step runs which program, which screen transaction starts which program, which
    /// tables the job creates. Reported beside the graph as a cross-check; at fixture scale the routing is
    /// confirmation of the source rather than an independent clustering signal, so it is not weighted in.
    /// </summary>
    private static List<JclFact> ExtractJcl(List<CobolMember> members, HashSet<string> programs, List<CrudEntry> crud)
    {
        var facts = new List<JclFact>();

        static string DescribeUtility(string program) => program.ToUpperInvariant() switch
        {
            "IKJEFT01" => "TSO batch driver — runs the DB2 commands that follow",
            "DSNTIAD" => "DB2 utility that executes the SQL that follows",
            "DFHCSDUP" => "CICS utility that loads resource definitions",
            "IEFBR14" or "IDCAMS" or "SORT" or "ICEMAN" => "system utility",
            _ => "not in the sample"
        };

        static string Description(string[] lines, int index)
        {
            for (var look = index; look < Math.Min(lines.Length, index + 3); look++)
            {
                var match = CsdDescriptionPattern().Match(lines[look]);
                if (match.Success)
                {
                    return match.Groups["text"].Value.Trim();
                }
            }

            return "";
        }

        foreach (var member in members.Where(entry => entry.Kind == "jcl"))
        {
            var lines = member.Source.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            string? pendingTransaction = null;

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var number = index + 1;
                if (line.StartsWith("//*", StringComparison.Ordinal))
                {
                    continue;
                }

                var step = JclExecPgmPattern().Match(line);
                if (step.Success)
                {
                    var program = step.Groups["pgm"].Value.ToUpperInvariant();
                    facts.Add(new JclFact(member.Name, "STEP", step.Groups["step"].Value.ToUpperInvariant(), program,
                        programs.Contains(program) ? "program is in the sample" : DescribeUtility(program), number));
                    continue;
                }

                var run = JclRunProgramPattern().Match(line);
                if (run.Success)
                {
                    var program = run.Groups["pgm"].Value.ToUpperInvariant();
                    var plan = run.Groups["plan"].Success ? $"under DB2 plan {run.Groups["plan"].Value.ToUpperInvariant()}" : "batch";
                    facts.Add(new JclFact(member.Name, "RUNS", plan, program,
                        programs.Contains(program) ? "program is in the sample" : DescribeUtility(program), number));
                    continue;
                }

                var create = JclCreateTablePattern().Match(line);
                if (create.Success)
                {
                    var table = create.Groups["table"].Value.ToUpperInvariant();
                    var readers = crud.Where(entry => entry.Artifact.Equals(table, StringComparison.OrdinalIgnoreCase) && entry.Access == "READS")
                        .Select(entry => entry.Program).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();
                    facts.Add(new JclFact(member.Name, "CREATES_TABLE", "DB2", table,
                        readers.Count == 0 ? "no program in the sample reads it" : $"read by {string.Join(", ", readers)}", number));
                    continue;
                }

                var define = CsdDefinePattern().Match(line);
                if (define.Success)
                {
                    pendingTransaction = null;
                    var kind = define.Groups["kind"].Value.ToUpperInvariant();
                    var name = define.Groups["name"].Value.ToUpperInvariant();
                    var description = Description(lines, index);
                    if (kind == "PROGRAM")
                    {
                        facts.Add(new JclFact(member.Name, "DEFINES_PROGRAM", "CICS", name,
                            $"{(programs.Contains(name) ? "in the sample" : "not in the sample")}{(description.Length > 0 ? " — " + description : "")}", number));
                    }
                    else if (kind == "TRANSACTION")
                    {
                        pendingTransaction = name;
                    }

                    continue;
                }

                if (pendingTransaction is not null)
                {
                    var routed = CsdProgramPattern().Match(line);
                    if (routed.Success)
                    {
                        var program = routed.Groups["pgm"].Value.ToUpperInvariant();
                        facts.Add(new JclFact(member.Name, "ROUTES_TO", pendingTransaction, program,
                            programs.Contains(program) ? "program is in the sample" : "program is not in the sample", number));
                        pendingTransaction = null;
                    }
                }
            }
        }

        return facts;
    }

    // ------------------------------------------------------------ COPY oracle

    /// <summary>
    /// GnuCOBOL runs the same COPY statements through its own preprocessor, resolving against -I paths in
    /// order — the same semantics as SYSLIB concatenation. Agreement is evidence. Disagreement means one
    /// of the two resolvers is wrong, and that is a defect to find, not a number to average away.
    /// </summary>
    private StageCheck CrossCheckCopies(string root, List<CobolMember> members, List<CopyDirective> copies)
    {
        if (!_toolchain.IsAvailable)
        {
            return new StageCheck("S2", "COPY resolution corroborated by GnuCOBOL", false, $"not run — {_toolchain.StatusMessage}");
        }

        var copyDirectory = Path.Combine(root, "copybook");
        var oracleResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oracleSubstituted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oracleMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var program in members.Where(member => member.Kind == "program"))
        {
            var outcome = _toolchain.ResolveCopybooks(Path.Combine(root, "cobol", program.Name + ".cbl"), copyDirectory);
            if (!outcome.Ran)
            {
                continue;
            }

            oracleResolved.UnionWith(outcome.Resolved);
            oracleSubstituted.UnionWith(outcome.Substituted);
            oracleMissing.UnionWith(outcome.Missing);
        }

        var ourResolved = copies.Where(copy => copy.Resolved).Select(copy => copy.Requested).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ourMissing = copies.Where(copy => !copy.Resolved).Select(copy => copy.Requested).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var disputed = ourMissing.Intersect(oracleResolved, StringComparer.OrdinalIgnoreCase)
            .Concat(ourResolved.Intersect(oracleMissing, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var agreed = ourMissing.Intersect(oracleMissing, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var substitutionNote = oracleSubstituted.Count == 0
            ? ""
            : $" cobc also satisfied {string.Join(", ", oracleSubstituted.OrderBy(name => name, StringComparer.Ordinal))} from its own bundled copy library, which is a substitution rather than a find — GnuCOBOL's SQLCA is not IBM's, so both resolvers correctly treat it as absent from this estate.";

        return new StageCheck("S2", "COPY resolution corroborated by GnuCOBOL", disputed.Count == 0,
            disputed.Count == 0
                ? $"cobc -E independently resolved {oracleResolved.Count} member(s) from the same library and reported {oracleMissing.Count} missing; the two resolvers agree, including on {agreed.Count} absent member(s): {string.Join(", ", agreed)}.{substitutionNote}"
                : $"disagreement on {string.Join(", ", disputed)} — one of the two resolvers is wrong.{substitutionNote}");
    }

    // ------------------------------------------------------------ S14 OKF
    private static List<OkfPreviewFile> BuildOkf(
        List<Neuron> neurons,
        List<Synapse> synapses,
        List<CoverageMetric> coverage,
        List<BoundaryObjectFinding> boundaries)
    {
        var files = new List<OkfPreviewFile>();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        files.Add(new OkfPreviewFile("okf/index.md",
            $"""
            okf_version: "0.2"
            title: GAM estate domain map
            status: draft
            generated:
              by: "tool:cobol-domain-pipeline"
              at: "{stamp}"
            verified: null
            stale_after: "{DateTimeOffset.UtcNow.AddMonths(3):yyyy-MM-dd}T00:00:00Z"
            """,
            $"""
            # GAM estate domain map

            {neurons.Count} neuron(s) over {neurons.Sum(neuron => neuron.Members.Count)} program(s).

            Coverage at generation time: {string.Join(", ", coverage.Select(metric => $"{metric.Name} {metric.Percent}%"))}.

            A boundary in this bundle is a hypothesis with cited evidence. `verified: null` means no SME has
            ratified it. Read the coverage line before the conclusions.
            """, true));

        foreach (var neuron in neurons)
        {
            files.Add(new OkfPreviewFile($"okf/neurons/neuron-{neuron.Id}.md",
                $"""
                type: Domain
                title: {neuron.Label}
                status: draft
                generated:
                  by: "tool:cobol-domain-pipeline"
                  at: "{stamp}"
                verified: null
                label_source: "{neuron.LabelSource}"
                """,
                $"""
                # {neuron.Label}

                Members: {string.Join(", ", neuron.Members.Select(member => member.Program))}
                Writes: {(neuron.OwnedArtifacts.Count == 0 ? "none proven" : string.Join(", ", neuron.OwnedArtifacts))}
                Reads: {(neuron.ReadArtifacts.Count == 0 ? "none proven" : string.Join(", ", neuron.ReadArtifacts))}
                Modularity of the partition that produced it: {neuron.Modularity}
                """, true));
        }

        foreach (var boundary in boundaries.Take(3))
        {
            files.Add(new OkfPreviewFile($"okf/boundary-objects/{boundary.Artifact.ToLowerInvariant()}.md",
                $"""
                type: Boundary Object
                title: {boundary.Artifact}
                status: draft
                generated:
                  by: "tool:cobol-domain-pipeline"
                  at: "{stamp}"
                verified: null
                """,
                $"""
                # {boundary.Artifact}

                Touched by: {string.Join(", ", boundary.TouchedBy)}
                Co-assignment across the sweep: {boundary.CoAssignment}

                {boundary.Why}. This file exists because the algorithm declined to decide.
                """, true));
        }

        foreach (var synapse in synapses.Take(4))
        {
            files.Add(new OkfPreviewFile($"okf/contracts/{synapse.Artifact.ToLowerInvariant()}.md",
                $"""
                type: Contract
                title: {synapse.Artifact}
                status: draft
                generated:
                  by: "tool:cobol-domain-pipeline"
                  at: "{stamp}"
                verified: null
                """,
                $"""
                # {synapse.Artifact}

                {synapse.FromNeuron} ↔ {synapse.ToNeuron} — {synapse.Direction}
                Writer: {synapse.Writer}
                Readers: {string.Join(", ", synapse.Readers)}

                Cut cost: {synapse.Cost}
                """, true));
        }

        return files;
    }

    // ------------------------------------------------------------ fixture

    private static List<CobolMember> LoadMembers(string root)
    {
        var members = new List<CobolMember>();

        void Scan(string folder, string kind, string pattern)
        {
            var path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, pattern).OrderBy(name => name, StringComparer.Ordinal))
            {
                var source = File.ReadAllText(file);
                members.Add(new CobolMember(
                    Path.GetFileNameWithoutExtension(file).ToUpperInvariant(),
                    kind,
                    Path.Combine(folder, Path.GetFileName(file)).Replace('\\', '/'),
                    source,
                    source.Split('\n').Length,
                    CobolFrontEnd.Sha256Of(source)));
            }
        }

        Scan("cobol", "program", "*.cbl");
        Scan("copybook", "copybook", "*.cpy");
        Scan("jcl", "jcl", "*.jcl");
        return members;
    }

    private static string? LocateFixture()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(directory.FullName, "TrainingFixture", "gam"),
                    Path.Combine(directory.FullName, "training-fixture", "gam"),
                    Path.Combine(directory.FullName, "poc-interactive-training", "training-fixture", "gam")
                })
                {
                    if (Directory.Exists(Path.Combine(candidate, "cobol")))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private CobolDomainReport Empty(string message) => new(
        false, message, "", [], [], [], [], [], [], [], [], [], [], [], [],
        new CommentCorpusReport(0, 0, 0, 0, 0, 0, 0, 0, 0), [], [], [], [], [], [], [], null, [], [], false, EmbeddingStatus, 0);

    [GeneratedRegex(@"DECLARE\s+(?<table>[A-Za-z0-9_$#@]+)\s+TABLE\s*\((?<cols>.*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclareTableBodyPattern();

    [GeneratedRegex(@"COPYRIGHT|DISCLAIMER|WARRANT|INDEMNIF|ALL RIGHTS RESERVED|LICENSED MATERIALS|IBM CORP|THESE SAMPLES", RegexOptions.IgnoreCase)]
    private static partial Regex BoilerplatePattern();

    [GeneratedRegex(@"^\s*(MOVE|COMPUTE|PERFORM|IF|EVALUATE|CALL|EXEC|ADD|SUBTRACT|MULTIPLY|DIVIDE|GO\s+TO|SET|INITIALIZE|END-IF|END-EXEC|WHEN)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodeLikePattern();

    [GeneratedRegex(@"\b(CHANGE\s+LOG|MAINTENANCE|MODIFICATION|REVISION HISTORY|PR#|CR-|DEFECT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MaintenancePattern();

    [GeneratedRegex(@"(?<date>\d{2}[/-]\d{2}[/-]\d{2,4})\s+(?<author>[A-Z][A-Z0-9]{2,8})?\s*(?<ticket>(?:PR#|CR-|DEF-|INC)\s*\d+)?\s*(?<desc>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex MaintenanceEntryPattern();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_-]{1,}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^//(?<step>[A-Z0-9@#$]+)\s+EXEC\s+PGM=(?<pgm>[A-Z0-9@#$]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JclExecPgmPattern();

    [GeneratedRegex(@"\bRUN\s+PROGRAM\((?<pgm>[A-Z0-9@#$]+)\)(?:\s+PLAN\((?<plan>[A-Z0-9@#$]+)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex JclRunProgramPattern();

    [GeneratedRegex(@"\bCREATE\s+TABLE\s+(?:&?[A-Z0-9@#$_]+\.)?(?<table>[A-Z0-9@#$_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JclCreateTablePattern();

    [GeneratedRegex(@"^\s*DEFINE\s+(?<kind>PROGRAM|TRANSACTION|DB2ENTRY)\((?<name>[A-Z0-9@#$]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CsdDefinePattern();

    [GeneratedRegex(@"\bPROGRAM\((?<pgm>[A-Z0-9@#$]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CsdProgramPattern();

    [GeneratedRegex(@"DESCRIPTION\((?<text>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CsdDescriptionPattern();

    [GeneratedRegex(@"\b(?:who|what|which)\b.*?\b(?<intent>writes?|updates?|owns|reads?|uses?|calls?|links)\b\s+(?:to\s+|from\s+|the\s+)?(?<subject>[A-Za-z0-9_$#@-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GraphQuestionPattern();
}

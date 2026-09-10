using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Stages S2b and S5-S8. The preprocessor grammar captures EXEC bodies as opaque charData, so the CRUD
// matrix and the CICS call graph can only come from this second pass. Every edge carries the member and
// line that produced it; an edge without a position is a defect, not a low-confidence edge.
public sealed partial class CobolGraphBuilder
{
    public sealed record SqlFinding(
        string Program,
        string Kind,
        string Artifact,
        string Access,
        string Confidence,
        int Line,
        string Detail);

    // ------------------------------------------------------------ S2b EXEC SQL

    /// <summary>
    /// Cursors are resolved in two passes: DECLARE binds a cursor name to its tables, then OPEN/FETCH/CLOSE
    /// inherit them. PREPARE/EXECUTE builds its statement in a host variable, so the artifact is genuinely
    /// not knowable statically and is reported as unresolved rather than guessed.
    /// </summary>
    public static List<SqlFinding> ExtractSql(IEnumerable<ExecBlock> blocks)
    {
        var findings = new List<SqlFinding>();
        var cursors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var prepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = blocks.Where(block => block.Dialect == "SQL").OrderBy(block => block.OriginalLine).ToList();

        foreach (var block in ordered)
        {
            var body = Strip(block.Body);

            if (DeclareTablePattern().Match(body) is { Success: true } table)
            {
                findings.Add(new SqlFinding(block.Program, "declare-table", Normalise(table.Groups["name"].Value),
                    "DECLARE", "exact", block.OriginalLine, "table layout declaration"));
                continue;
            }

            if (DeclareCursorPattern().Match(body) is { Success: true } cursor)
            {
                var name = cursor.Groups["name"].Value.ToUpperInvariant();
                var tables = TablesIn(cursor.Groups["query"].Value);
                cursors[name] = tables;
                var forUpdate = body.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
                foreach (var target in tables)
                {
                    findings.Add(new SqlFinding(block.Program, "cursor", target, "READS", "exact", block.OriginalLine, $"cursor {name}"));
                    if (forUpdate)
                    {
                        findings.Add(new SqlFinding(block.Program, "cursor", target, "UPDATES", "exact", block.OriginalLine, $"cursor {name} FOR UPDATE"));
                    }
                }

                continue;
            }

            if (CursorUsePattern().Match(body) is { Success: true } use)
            {
                var verb = use.Groups["verb"].Value.ToUpperInvariant();
                var name = use.Groups["name"].Value.ToUpperInvariant();
                if (cursors.TryGetValue(name, out var tables))
                {
                    foreach (var target in tables)
                    {
                        findings.Add(new SqlFinding(block.Program, verb.ToLowerInvariant(), target, "READS", "resolved", block.OriginalLine, $"{verb} {name}"));
                    }
                }
                else if (prepared.Contains(name))
                {
                    findings.Add(new SqlFinding(block.Program, "dynamic", "(unresolved)", "UNKNOWN", "unresolved", block.OriginalLine,
                        $"{verb} of prepared statement {name}"));
                }

                continue;
            }

            if (PreparePattern().Match(body) is { Success: true } prepare)
            {
                var name = prepare.Groups["name"].Value.ToUpperInvariant();
                prepared.Add(name);
                findings.Add(new SqlFinding(block.Program, "dynamic", "(unresolved)", "UNKNOWN", "unresolved", block.OriginalLine,
                    $"PREPARE {name} builds its statement text in a host variable"));
                continue;
            }

            if (ExecutePattern().Match(body) is { Success: true } execute)
            {
                findings.Add(new SqlFinding(block.Program, "dynamic", "(unresolved)", "UNKNOWN", "unresolved", block.OriginalLine,
                    $"EXECUTE {execute.Groups["name"].Value.ToUpperInvariant()} — target table not knowable statically"));
                continue;
            }

            foreach (var (pattern, access) in new (Regex, string)[]
            {
                (InsertPattern(), "WRITES"),
                (UpdatePattern(), "UPDATES"),
                (DeletePattern(), "DELETES")
            })
            {
                var match = pattern.Match(body);
                if (match.Success)
                {
                    findings.Add(new SqlFinding(block.Program, "dml", Normalise(match.Groups["name"].Value), access, "exact", block.OriginalLine, access.ToLowerInvariant()));
                }
            }

            if (body.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var target in TablesIn(body))
                {
                    findings.Add(new SqlFinding(block.Program, "dml", target, "READS", "exact", block.OriginalLine, "singleton select"));
                }
            }

            if (ConnectPattern().Match(body) is { Success: true } connect)
            {
                findings.Add(new SqlFinding(block.Program, "connect", connect.Groups["name"].Value.ToUpperInvariant(),
                    "CONNECT", "exact", block.OriginalLine, "database connection"));
            }
        }

        return findings;
    }

    private static List<string> TablesIn(string query)
    {
        var match = FromClausePattern().Match(query);
        if (!match.Success)
        {
            return [];
        }

        return match.Groups["tables"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .Select(Normalise)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalise(string name) => name.Trim().Trim('"').ToUpperInvariant();

    private static string Strip(string body) =>
        CobolFrontEnd.Collapse(ExecWrapperPattern().Replace(body, string.Empty)).TrimEnd('.', ' ');

    // ------------------------------------------------------------ S2b EXEC CICS

    public sealed record CicsFinding(
        string Program,
        string Command,
        string Kind,
        string Target,
        string Confidence,
        int Line);

    public static List<CicsFinding> ExtractCics(IEnumerable<ExecBlock> blocks)
    {
        var findings = new List<CicsFinding>();
        foreach (var block in blocks.Where(block => block.Dialect == "CICS"))
        {
            var body = Strip(block.Body);
            var command = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "";

            // LINK returns; XCTL does not. They are different control semantics and must never be merged.
            if (command is "LINK" or "XCTL" && ProgramOperandPattern().Match(body) is { Success: true } program)
            {
                var literal = program.Groups["quoted"].Success;
                findings.Add(new CicsFinding(block.Program, command,
                    command == "LINK" ? "CICS_LINK" : "CICS_XCTL",
                    (literal ? program.Groups["quoted"].Value : program.Groups["name"].Value).ToUpperInvariant(),
                    literal ? "exact" : "inferred", block.OriginalLine));
                continue;
            }

            if (command is "RETURN" && TransIdPattern().Match(body) is { Success: true } transid)
            {
                findings.Add(new CicsFinding(block.Program, "RETURN TRANSID", "NEXT_TRANSID",
                    transid.Groups["name"].Value.ToUpperInvariant(), "exact", block.OriginalLine));
                continue;
            }

            if (command is "START" && TransIdPattern().Match(body) is { Success: true } started)
            {
                findings.Add(new CicsFinding(block.Program, "START", "CICS_START",
                    started.Groups["name"].Value.ToUpperInvariant(), "exact", block.OriginalLine));
                continue;
            }

            if (command is "SEND" or "RECEIVE" && MapOperandPattern().Match(body) is { Success: true } map)
            {
                findings.Add(new CicsFinding(block.Program, $"{command} MAP", "USES_MAP",
                    map.Groups["name"].Value.ToUpperInvariant(), "exact", block.OriginalLine));
                continue;
            }

            if (command is "READ" or "WRITE" or "REWRITE" or "DELETE" && FileOperandPattern().Match(body) is { Success: true } file)
            {
                var access = command switch
                {
                    "READ" => "READS",
                    "WRITE" => "WRITES",
                    "REWRITE" => "UPDATES",
                    _ => "DELETES"
                };
                findings.Add(new CicsFinding(block.Program, $"{command} FILE", access,
                    file.Groups["name"].Value.ToUpperInvariant(), "exact", block.OriginalLine));
            }
        }

        return findings;
    }

    // ------------------------------------------------------------ S5 call graph

    public static List<CicsFinding> ExtractCalls(CobolProgramAst ast, IReadOnlyCollection<string> knownPrograms)
    {
        var findings = new List<CicsFinding>();

        // Constant propagation for dynamic CALL: the last literal moved into the item before the call site.
        var literals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var statement in ast.Statements.OrderBy(item => item.Ordinal))
        {
            if (statement.Verb == "MOVE" && MoveLiteralPattern().Match(statement.Text) is { Success: true } move)
            {
                literals[move.Groups["target"].Value.ToUpperInvariant()] = move.Groups["literal"].Value.ToUpperInvariant();
                continue;
            }

            if (statement.Verb != "CALL")
            {
                continue;
            }

            var call = CallPattern().Match(statement.Text);
            if (!call.Success)
            {
                continue;
            }

            if (call.Groups["quoted"].Success)
            {
                findings.Add(new CicsFinding(ast.Program, "CALL", "CALLS_STATIC",
                    call.Groups["quoted"].Value.ToUpperInvariant(), "exact", statement.OriginalLine));
                continue;
            }

            var item = call.Groups["name"].Value.ToUpperInvariant();
            if (literals.TryGetValue(item, out var resolved))
            {
                findings.Add(new CicsFinding(ast.Program, "CALL", "CALLS_DYNAMIC", resolved, "resolved", statement.OriginalLine));
            }
            else
            {
                findings.Add(new CicsFinding(ast.Program, "CALL", "CALLS_DYNAMIC", "(unresolved)", "unresolved", statement.OriginalLine));
            }
        }

        return findings;
    }

    // ------------------------------------------------------------ S6 DDG

    /// <summary>
    /// Def-use over the declared symbol table. Only names that were actually declared produce edges, so a
    /// keyword can never be mistaken for a data item. REDEFINES overlap is added at byte granularity
    /// because writing one alias mutates every alias sharing those bytes.
    /// </summary>
    public static List<DataFlowEdge> BuildDataFlow(CobolProgramAst ast)
    {
        var edges = new List<DataFlowEdge>();
        var declared = ast.DataItems
            .Where(item => item.Name != "FILLER")
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var statement in ast.Statements)
        {
            var names = IdentifierPattern().Matches(statement.Text)
                .Select(match => match.Value.ToUpperInvariant())
                .Where(declared.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
            {
                continue;
            }

            switch (statement.Verb)
            {
                case "MOVE":
                {
                    var move = MoveTargetsPattern().Match(statement.Text);
                    if (!move.Success)
                    {
                        break;
                    }

                    var sources = IdentifierPattern().Matches(move.Groups["from"].Value)
                        .Select(match => match.Value.ToUpperInvariant()).Where(declared.ContainsKey).ToList();
                    var targets = IdentifierPattern().Matches(move.Groups["to"].Value)
                        .Select(match => match.Value.ToUpperInvariant()).Where(declared.ContainsKey).ToList();
                    foreach (var target in targets)
                    {
                        foreach (var source in sources.Where(value => !string.Equals(value, target, StringComparison.OrdinalIgnoreCase)))
                        {
                            edges.Add(new DataFlowEdge(ast.Program, source, target, "MOVE", statement.OriginalLine));
                        }
                    }

                    break;
                }

                case "COMPUTE":
                {
                    var compute = ComputePattern().Match(statement.Text);
                    if (!compute.Success)
                    {
                        break;
                    }

                    var target = compute.Groups["target"].Value.ToUpperInvariant();
                    if (!declared.ContainsKey(target))
                    {
                        break;
                    }

                    foreach (var source in IdentifierPattern().Matches(compute.Groups["expr"].Value)
                        .Select(match => match.Value.ToUpperInvariant())
                        .Where(declared.ContainsKey)
                        .Where(value => !string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        edges.Add(new DataFlowEdge(ast.Program, source, target, "COMPUTE", statement.OriginalLine));
                    }

                    break;
                }

                case "EXEC":
                {
                    // FETCH ... INTO :host-variables is a flow from the table into working storage.
                    var into = IntoPattern().Match(statement.Text);
                    if (into.Success)
                    {
                        foreach (var target in IdentifierPattern().Matches(into.Groups["hosts"].Value)
                            .Select(match => match.Value.ToUpperInvariant()).Where(declared.ContainsKey))
                        {
                            edges.Add(new DataFlowEdge(ast.Program, "(sql result)", target, "FETCH INTO", statement.OriginalLine));
                        }
                    }

                    break;
                }
            }
        }

        // Byte-range overlap: two items sharing storage are the same bytes under different names.
        foreach (var item in ast.DataItems.Where(entry => entry.Redefines is not null))
        {
            edges.Add(new DataFlowEdge(ast.Program, item.Name, item.Redefines!, "REDEFINES overlap", item.OriginalLine));
            edges.Add(new DataFlowEdge(ast.Program, item.Redefines!, item.Name, "REDEFINES overlap", item.OriginalLine));
        }

        return edges.DistinctBy(edge => (edge.From, edge.To, edge.Via)).ToList();
    }

    // ------------------------------------------------------------ S7 PDG (built on demand)

    /// <summary>
    /// Control dependence over structured COBOL scopes, joined to the data flow. Built dynamically for one
    /// program at a time and never cached, because estate-wide PDG is neither tractable nor useful for
    /// domain segmentation. GO TO and ALTER are detected and reported rather than silently mis-modelled.
    /// </summary>
    public static PdgResult BuildPdg(CobolProgramAst ast, IReadOnlyList<DataFlowEdge> dataFlow)
    {
        var nodes = new List<PdgNode>();
        var control = new List<(int From, int To)>();
        var byOrdinal = ast.Statements.ToDictionary(statement => statement.Ordinal);

        foreach (var statement in ast.Statements.OrderBy(item => item.Ordinal))
        {
            var condition = statement.ConditionOrdinal >= 0 && byOrdinal.TryGetValue(statement.ConditionOrdinal, out var parent)
                ? Truncate(parent.Text, 90)
                : "(unconditional)";

            nodes.Add(new PdgNode(ast.Program, statement.Paragraph, statement.Ordinal, statement.Verb,
                Truncate(statement.Text, 110), statement.ConditionOrdinal, condition, statement.Depth, statement.OriginalLine));

            if (statement.ConditionOrdinal >= 0)
            {
                control.Add((statement.ConditionOrdinal, statement.Ordinal));
            }
        }

        var caveats = new List<string>
        {
            "Control dependence is derived from structured scopes (IF / EVALUATE / inline PERFORM).",
            "This is one program's statement graph. It says nothing about domain boundaries."
        };

        var gotos = ast.Statements.Count(statement => statement.Verb == "GO");
        if (gotos > 0)
        {
            caveats.Add($"{gotos} GO TO statement(s) present — unstructured transfers are not modelled as control edges.");
        }

        if (ast.Statements.Any(statement => statement.Text.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase)))
        {
            caveats.Add("ALTER detected. Control flow cannot be trusted from source alone in this program.");
        }

        var performThru = ast.Statements.Count(statement => statement.Verb == "PERFORM" && ThruPattern().IsMatch(statement.Text));
        if (performThru > 0)
        {
            caveats.Add($"{performThru} PERFORM THRU range(s) — fall-through is resolved by the compiler, not by this parse.");
        }

        return new PdgResult(
            ast.Program,
            nodes,
            control,
            dataFlow.Where(edge => edge.Program == ast.Program).ToList(),
            nodes.Count == 0 ? 0 : nodes.Max(node => node.Depth),
            gotos == 0,
            caveats);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@"^\s*EXEC\s+(SQL|CICS|SQLIMS|DLI)\b|\bEND-EXEC\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecWrapperPattern();

    [GeneratedRegex(@"^DECLARE\s+(?<name>[A-Z0-9_""$#@.]+)\s+TABLE\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeclareTablePattern();

    [GeneratedRegex(@"^DECLARE\s+(?<name>[A-Z0-9_$#@-]+)\s+CURSOR\s+(?:WITH\s+\w+\s+)?FOR\s+(?<query>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeclareCursorPattern();

    [GeneratedRegex(@"^(?<verb>OPEN|CLOSE|FETCH)\s+(?<name>[A-Z0-9_$#@-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CursorUsePattern();

    [GeneratedRegex(@"^PREPARE\s+(?<name>[A-Z0-9_$#@-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PreparePattern();

    [GeneratedRegex(@"^EXECUTE\s+(?!IMMEDIATE)(?<name>[A-Z0-9_$#@-]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutePattern();

    [GeneratedRegex(@"^INSERT\s+INTO\s+(?<name>[A-Z0-9_""$#@.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InsertPattern();

    [GeneratedRegex(@"^UPDATE\s+(?<name>[A-Z0-9_""$#@.]+)\s+SET\b", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatePattern();

    [GeneratedRegex(@"^DELETE\s+FROM\s+(?<name>[A-Z0-9_""$#@.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeletePattern();

    [GeneratedRegex(@"^CONNECT\s+TO\s+(?<name>[A-Z0-9_$#@-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectPattern();

    [GeneratedRegex(@"\bFROM\s+(?<tables>[A-Z0-9_""$#@., ]+?)(?=\s+(?:WHERE|ORDER|GROUP|HAVING|FETCH|FOR|UNION|INTO)\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FromClausePattern();

    [GeneratedRegex(@"\bPROGRAM\s*\(\s*(?:'(?<quoted>[^']+)'|""(?<quoted>[^""]+)""|(?<name>[A-Z0-9$#@_-]+))\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramOperandPattern();

    [GeneratedRegex(@"\bTRANSID\s*\(\s*'?(?<name>[A-Z0-9$#@_-]+)'?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TransIdPattern();

    [GeneratedRegex(@"\bMAP\s*\(\s*'?(?<name>[A-Z0-9$#@_-]+)'?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex MapOperandPattern();

    [GeneratedRegex(@"\bFILE\s*\(\s*'?(?<name>[A-Z0-9$#@_-]+)'?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex FileOperandPattern();

    [GeneratedRegex(@"^CALL\s+(?:'(?<quoted>[^']+)'|""(?<quoted>[^""]+)""|(?<name>[A-Z0-9$#@_-]+))", RegexOptions.IgnoreCase)]
    private static partial Regex CallPattern();

    [GeneratedRegex(@"^MOVE\s+(?:'(?<literal>[^']+)'|""(?<literal>[^""]+)"")\s+TO\s+(?<target>[A-Z0-9$#@_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MoveLiteralPattern();

    [GeneratedRegex(@"^MOVE\s+(?<from>.+?)\s+TO\s+(?<to>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MoveTargetsPattern();

    [GeneratedRegex(@"^COMPUTE\s+(?<target>[A-Z0-9$#@_-]+)(?:\s+ROUNDED)?\s*=\s*(?<expr>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComputePattern();

    [GeneratedRegex(@"\bINTO\s+(?<hosts>:.+?)(?=\s+(?:FROM|WHERE|END-EXEC)\b|$)", RegexOptions.IgnoreCase)]
    private static partial Regex IntoPattern();

    [GeneratedRegex(@"\bTHRU\b|\bTHROUGH\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThruPattern();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+\b|\b[A-Z][A-Z0-9]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IdentifierPattern();
}

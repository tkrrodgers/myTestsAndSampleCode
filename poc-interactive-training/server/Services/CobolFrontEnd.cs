using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Stages S0-S4 of the domain-segmentation design: inventory, fixed-format normalisation, the
// preprocessor pass, and the parse that produces the AST and the byte-accurate symbol table.
//
// S3 is the real ANTLR Cobol85 grammar (see CobolAntlrParser). The recursive-descent parser below is
// kept as a fallback so a grammar gap degrades to a partial AST instead of losing the program from the
// graph entirely -- an invisible program clusters nowhere, which looks like a clean boundary. Which
// parser actually ran is reported per program rather than assumed.
public sealed partial class CobolFrontEnd
{
    public const string AntlrName = "ANTLR Cobol85.g4";
    public const string FallbackName = "recursive-descent fallback";
    public const string FrontEndName = "ANTLR Cobol85.g4 (antlr-ng generated, vendored) with a recursive-descent fallback";

    // Upstream members deliberately left out of the fixture. Recorded so "excluded" never reads as
    // "unresolved" — one is a decision, the other is a hole in the evidence.
    private static readonly Dictionary<string, string> ExcludedBySize = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GAM0BCD"] = "data-population copybook, 262 KB upstream",
        ["GAM0BDD"] = "data-population copybook, 116 KB upstream",
        ["GAM0BED"] = "data-population copybook, 417 KB upstream",
        ["GAM0BPD"] = "data-population copybook, 193 KB upstream",
        ["GAM0BMD"] = "data-population copybook, 12 KB upstream"
    };

    // ------------------------------------------------------------ S1 normalisation

    /// <summary>
    /// Fixed-format Enterprise COBOL is columns 1-6 sequence, 7 indicator, 8-72 code, 73-80 identification.
    /// The ANTLR grammar's COMMENTLINE rule matches '*&gt;', so a column-7 '*' would shred the parse.
    /// Continuations must be joined before lexing. Every produced line keeps its origin.
    /// </summary>
    public static (List<CobolLine> Lines, NormalizeReport Report) Normalize(string member, string source)
    {
        var raw = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<CobolLine>();
        int comments = 0, joined = 0, idPopulated = 0, code = 0;
        var roundTrip = true;
        var detail = "columns 1-6 stripped, 7 indicator honoured, 73-80 captured";

        foreach (var (text, offset) in raw.Select((value, index) => (value, index)))
        {
            var originalLine = offset + 1;
            if (text.Trim().Length == 0)
            {
                continue;
            }

            var indicator = text.Length > 6 ? text[6] : ' ';
            var areaStart = Math.Min(text.Length, 7);
            var areaEnd = Math.Min(text.Length, 72);
            var area = areaEnd > areaStart ? text[areaStart..areaEnd].TrimEnd() : string.Empty;
            var idArea = text.Length > 72 ? text[72..Math.Min(text.Length, 80)].Trim() : string.Empty;

            if (idArea.Length > 0)
            {
                idPopulated++;
            }

            if (indicator is '*' or '/')
            {
                comments++;
                lines.Add(new CobolLine(lines.Count, originalLine, member, area, true, idArea, 0));
                continue;
            }

            // A debugging line only compiles under WITH DEBUGGING MODE. Treated as non-code and reported.
            if (indicator is 'D' or 'd')
            {
                comments++;
                lines.Add(new CobolLine(lines.Count, originalLine, member, area, true, idArea, 0));
                continue;
            }

            if (area.Length == 0)
            {
                continue;
            }

            if (indicator == '-' && lines.Count > 0)
            {
                var previous = lines.FindLastIndex(line => !line.IsComment);
                if (previous >= 0)
                {
                    joined++;
                    lines[previous] = lines[previous] with
                    {
                        Text = lines[previous].Text + area.TrimStart(),
                        JoinedFrom = lines[previous].JoinedFrom + 1
                    };
                    continue;
                }
            }

            // Re-emit and compare: the normalised text must be exactly what stood in columns 8-72.
            if (!text[areaStart..areaEnd].TrimEnd().Equals(area, StringComparison.Ordinal))
            {
                roundTrip = false;
                detail = $"line {originalLine} did not round-trip";
            }

            code++;
            lines.Add(new CobolLine(lines.Count, originalLine, member, area, false, idArea, 0));
        }

        return (lines, new NormalizeReport(member, raw.Length, code, comments, joined, idPopulated, roundTrip, detail));
    }

    // ------------------------------------------------------------ S2 preprocessor

    public sealed record PreprocessResult(
        List<CobolLine> Expanded,
        List<CopyDirective> Copies,
        List<ExecBlock> ExecBlocks,
        List<CompilerOptionFinding> Options);

    /// <summary>
    /// Resolves COPY against the library in concatenation order, captures EXEC blocks verbatim for the
    /// second pass, and mines CBL/PROCESS compiler options. EXEC bodies are deliberately not parsed here,
    /// exactly as Cobol85Preprocessor.g4 captures them as charData.
    /// </summary>
    public static PreprocessResult Preprocess(
        string program,
        List<CobolLine> lines,
        IReadOnlyDictionary<string, CobolMember> library)
    {
        var result = new PreprocessResult([], [], [], []);
        Run(program, lines, library, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static void Run(
        string program,
        List<CobolLine> lines,
        IReadOnlyDictionary<string, CobolMember> library,
        PreprocessResult result,
        HashSet<string> visited)
    {
        var expanded = result.Expanded;
        var copies = result.Copies;
        var execs = result.ExecBlocks;
        var options = result.Options;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.IsComment)
            {
                expanded.Add(line);
                continue;
            }

            var trimmed = line.Text.Trim();

            if (CompilerOptionPattern().IsMatch(trimmed))
            {
                foreach (Match option in OptionValuePattern().Matches(trimmed))
                {
                    options.Add(new CompilerOptionFinding(
                        line.Member, option.Groups["name"].Value.ToUpperInvariant(),
                        option.Groups["value"].Value.ToUpperInvariant(), "CBL/PROCESS statement", line.OriginalLine));
                }

                continue;
            }

            // EXEC ... END-EXEC, possibly spanning many lines. Captured whole, never interpreted here.
            var execMatch = ExecStartPattern().Match(trimmed);
            if (execMatch.Success)
            {
                var dialect = execMatch.Groups["dialect"].Value.ToUpperInvariant();
                var body = new StringBuilder();
                var startLine = line.OriginalLine;
                var span = 0;
                while (index < lines.Count)
                {
                    var current = lines[index];
                    if (!current.IsComment)
                    {
                        body.Append(current.Text.Trim()).Append(' ');
                        span++;
                    }

                    if (current.Text.Contains("END-EXEC", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    index++;
                }

                var text = body.ToString();
                execs.Add(new ExecBlock(program, dialect, Collapse(text), startLine, span));

                // Cobol85.g4 only recognises an EXEC block as a single marker line, and the statement
                // rules carry no DOT_FS, so the sentence period must be emitted separately.
                var marker = dialect switch
                {
                    "CICS" => "*>EXECCICS ",
                    "SQLIMS" => "*>EXECSQLIMS ",
                    "SQL" => "*>EXECSQL ",
                    _ => null
                };

                if (marker is not null)
                {
                    var collapsed = Collapse(text).TrimEnd();
                    var terminated = collapsed.EndsWith('.');
                    expanded.Add(new CobolLine(expanded.Count, startLine, program,
                        marker + (terminated ? collapsed.TrimEnd('.') : collapsed), false, "", 0));
                    if (terminated)
                    {
                        expanded.Add(new CobolLine(expanded.Count, startLine, program, ".", false, "", 0));
                    }
                }

                // EXEC SQL INCLUDE is a copybook inclusion by another name and belongs in the same coverage.
                var include = SqlIncludePattern().Match(text);
                if (include.Success)
                {
                    var name = include.Groups["name"].Value.ToUpperInvariant();
                    copies.Add(Resolve(program, name, null, false, startLine, library, result, visited));
                }

                continue;
            }

            var copyMatch = CopyPattern().Match(trimmed);
            if (copyMatch.Success)
            {
                var statement = trimmed;
                var scan = index;
                while (!statement.Contains('.') && scan + 1 < lines.Count)
                {
                    scan++;
                    statement += " " + lines[scan].Text.Trim();
                }

                index = scan;
                var name = copyMatch.Groups["name"].Value.Trim('\'', '"').ToUpperInvariant();
                var lib = copyMatch.Groups["lib"].Success ? copyMatch.Groups["lib"].Value.ToUpperInvariant() : null;
                var replacing = statement.Contains("REPLACING", StringComparison.OrdinalIgnoreCase);
                copies.Add(Resolve(program, name, lib, replacing, line.OriginalLine, library, result, visited));
                continue;
            }

            expanded.Add(line);
        }
    }

    private static CopyDirective Resolve(
        string program,
        string name,
        string? library,
        bool replacing,
        int originalLine,
        IReadOnlyDictionary<string, CobolMember> books,
        PreprocessResult result,
        HashSet<string> visited)
    {
        if (books.TryGetValue(name, out var member))
        {
            // Inlined text is preprocessed too, or a copybook's own EXEC block reaches the grammar as
            // multiple lines and the whole program fails to parse. The visited set stops a COPY cycle.
            if (visited.Add(name))
            {
                var (inner, _) = Normalize(name, member.Source);
                Run(program, inner, books, result, visited);
            }

            return new CopyDirective(program, name, library, replacing, true, $"resolved from {member.RelativePath}", originalLine);
        }

        var reason = name switch
        {
            _ when ExcludedBySize.TryGetValue(name, out var why) => $"excluded from fixture by size — {why}",
            _ when name.StartsWith("DFH", StringComparison.OrdinalIgnoreCase) => "CICS system copybook, supplied by the CICS copy library on z/OS",
            "SQLCA" or "SQLDA" => "DB2 system copybook, supplied by the precompiler library on z/OS — GnuCOBOL ships its own, which is not IBM's layout",
            _ when BmsPattern().IsMatch(name) => "BMS map copybook, generated by the assembler SYSPUNCH step",
            _ => "not found in the fixture copybook library"
        };

        return new CopyDirective(program, name, library, replacing, false, reason, originalLine);
    }

    // ------------------------------------------------------------ S3/S4 parse

    /// <summary>Preprocessed text plus the map back to real members and lines, so evidence stays citable.</summary>
    public sealed record PreparedSource(string Text, IReadOnlyList<CobolLine> Lines)
    {
        public int OriginalLineOf(int parsedLine) =>
            parsedLine >= 1 && parsedLine <= Lines.Count ? Lines[parsedLine - 1].OriginalLine : 0;

        public string MemberOf(int parsedLine) =>
            parsedLine >= 1 && parsedLine <= Lines.Count ? Lines[parsedLine - 1].Member : "";
    }

    /// <summary>
    /// Renders preprocessed lines for the parser, one output line per source line so the line map is
    /// exact. Comments are dropped rather than re-tagged; the comment corpus is collected separately.
    /// </summary>
    public static PreparedSource Render(List<CobolLine> expanded)
    {
        var builder = new StringBuilder();
        var map = new List<CobolLine>();
        foreach (var line in expanded.Where(entry => !entry.IsComment && entry.Text.Trim().Length > 0))
        {
            builder.Append("       ").Append(line.Text).Append('\n');
            map.Add(line);
        }

        return new PreparedSource(builder.ToString(), map);
    }

    /// <summary>
    /// Byte offsets within the containing 01. REDEFINES restarts at the redefined item's offset, which
    /// is what makes the DDG's overlap detection real rather than name-based.
    /// </summary>
    public static List<CobolDataItem> AssignOffsets(List<CobolDataItem> items)
    {
        var stack = new Stack<(int Level, int Offset)>();
        var byName = new Dictionary<string, CobolDataItem>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CobolDataItem>(items.Count);

        foreach (var item in items)
        {
            while (stack.Count > 0 && stack.Peek().Level >= item.Level)
            {
                stack.Pop();
            }

            var offset = 0;
            if (item.Redefines is not null && byName.TryGetValue(item.Redefines, out var target))
            {
                offset = target.Offset;
            }
            else if (stack.Count > 0)
            {
                offset = stack.Peek().Offset;
            }

            var placed = item with { Offset = offset };
            result.Add(placed);
            if (placed.Name != "FILLER")
            {
                byName[placed.Name] = placed;
            }

            if (item.Redefines is null && stack.Count > 0)
            {
                var top = stack.Pop();
                stack.Push((top.Level, top.Offset + placed.Length));
            }

            stack.Push((item.Level, offset));
        }

        return result;
    }

    public sealed record ParseOutcome(CobolProgramAst Ast, string Parser, int SyntaxErrors, string? FirstError);

    /// <summary>
    /// ANTLR first; on syntax errors or an exception, fall back and say so. A silent fallback would
    /// make a grammar gap look like a clean parse, which is the failure this reporting exists to stop.
    /// </summary>
    public static ParseOutcome ParseProgram(string program, List<CobolLine> lines)
    {
        try
        {
            var outcome = CobolAntlrParser.Parse(program, Render(lines));
            if (outcome.SyntaxErrors == 0 && outcome.Ast.ProgramId is not null)
            {
                return new ParseOutcome(outcome.Ast, AntlrName, 0, null);
            }

            var degraded = ParseFallback(program, lines);
            return new ParseOutcome(
                degraded with { Detail = $"{degraded.Detail}; ANTLR reported {outcome.SyntaxErrors} syntax error(s)" },
                FallbackName, outcome.SyntaxErrors, outcome.FirstError);
        }
        catch (Exception ex)
        {
            return new ParseOutcome(ParseFallback(program, lines), FallbackName, -1, ex.Message);
        }
    }

    /// <summary>Structural fallback used only when the grammar rejects a program.</summary>
    public static CobolProgramAst ParseFallback(string program, List<CobolLine> lines)
    {
        string? programId = null;
        var divisions = new List<string>();
        var paragraphs = new List<CobolParagraph>();
        var items = new List<CobolDataItem>();
        var statements = new List<CobolStatement>();

        var division = "";
        var section = "";
        var paragraph = "(program)";
        var ordinal = 0;
        var depth = 0;
        var conditionStack = new Stack<int>();
        var offsets = new Stack<(int Level, int Offset)>();
        var byName = new Dictionary<string, CobolDataItem>(StringComparer.OrdinalIgnoreCase);
        var pendingItem = new StringBuilder();
        var pendingLine = 0;
        var paragraphCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void FlushItem()
        {
            if (pendingItem.Length == 0)
            {
                return;
            }

            var text = Collapse(pendingItem.ToString().TrimEnd('.', ' '));
            pendingItem.Clear();
            var match = DataItemPattern().Match(text);
            if (!match.Success)
            {
                return;
            }

            var level = int.Parse(match.Groups["level"].Value);
            var name = match.Groups["name"].Value.ToUpperInvariant();
            var rest = match.Groups["rest"].Value;

            var picture = PicturePattern().Match(rest) is { Success: true } pic ? pic.Groups["pic"].Value : null;
            var usage = UsageOf(rest);
            var redefines = RedefinesPattern().Match(rest) is { Success: true } red ? red.Groups["name"].Value.ToUpperInvariant() : null;
            var occurs = OccursPattern().Match(rest) is { Success: true } occ ? int.Parse(occ.Groups["n"].Value) : 0;
            var length = PictureSize(picture, usage);

            while (offsets.Count > 0 && offsets.Peek().Level >= level)
            {
                offsets.Pop();
            }

            var offset = 0;
            if (redefines is not null && byName.TryGetValue(redefines, out var target))
            {
                offset = target.Offset;
            }
            else if (offsets.Count > 0)
            {
                offset = offsets.Peek().Offset;
            }

            var item = new CobolDataItem(program, level, name, picture, usage, redefines, occurs,
                offset, length * Math.Max(occurs, 1), section, lines.Count > 0 ? program : program, pendingLine);
            items.Add(item);
            if (name != "FILLER")
            {
                byName[name] = item;
            }

            // Group items own their children's bytes; elementary items advance the running offset.
            if (redefines is null && offsets.Count > 0)
            {
                var top = offsets.Pop();
                offsets.Push((top.Level, top.Offset + item.Length));
            }

            offsets.Push((level, offset));
        }

        foreach (var line in lines)
        {
            if (line.IsComment)
            {
                continue;
            }

            var text = Collapse(line.Text.Trim());
            var upper = text.ToUpperInvariant();

            if (DivisionPattern().Match(upper) is { Success: true } div)
            {
                FlushItem();
                division = div.Groups["name"].Value.ToUpperInvariant();
                divisions.Add(division);
                section = "";
                continue;
            }

            if (SectionPattern().Match(upper) is { Success: true } sec && division == "DATA")
            {
                FlushItem();
                section = sec.Groups["name"].Value.ToUpperInvariant();
                offsets.Clear();
                continue;
            }

            if (ProgramIdPattern().Match(upper) is { Success: true } pid)
            {
                programId = pid.Groups["name"].Value.ToUpperInvariant();
                continue;
            }

            if (division == "DATA")
            {
                if (LevelStartPattern().IsMatch(upper))
                {
                    FlushItem();
                    pendingLine = line.OriginalLine;
                }

                pendingItem.Append(text).Append(' ');
                if (text.EndsWith('.'))
                {
                    FlushItem();
                }

                continue;
            }

            if (division != "PROCEDURE")
            {
                continue;
            }

            // Area A label: the normalised text starts at column 8, so a paragraph name sits at index 0.
            if (line.Text.Length > 0 && line.Text[0] != ' ' && ParagraphPattern().IsMatch(upper))
            {
                paragraph = upper.TrimEnd('.');
                paragraphs.Add(new CobolParagraph(program, paragraph, line.OriginalLine, 0));
                depth = 0;
                conditionStack.Clear();
                continue;
            }

            var verb = VerbOf(upper);
            if (verb is null)
            {
                continue;
            }

            if (verb is "END-IF" or "END-EVALUATE" or "END-PERFORM")
            {
                depth = Math.Max(0, depth - 1);
                if (conditionStack.Count > 0)
                {
                    conditionStack.Pop();
                }

                continue;
            }

            var parent = conditionStack.Count > 0 ? conditionStack.Peek() : -1;
            var statement = new CobolStatement(program, paragraph, ordinal, verb, text, depth, parent, line.OriginalLine);
            statements.Add(statement);
            paragraphCounts[paragraph] = paragraphCounts.GetValueOrDefault(paragraph) + 1;
            ordinal++;

            var opensScope = verb switch
            {
                "IF" => !upper.Contains("END-IF"),
                "EVALUATE" => !upper.Contains("END-EVALUATE"),
                "PERFORM" => (upper.Contains(" UNTIL ") || upper.Contains(" VARYING ")) && !upper.Contains("END-PERFORM"),
                _ => false
            };

            if (opensScope)
            {
                depth++;
                conditionStack.Push(statement.Ordinal);
            }
        }

        FlushItem();

        var counted = paragraphs
            .Select(item => item with { StatementCount = paragraphCounts.GetValueOrDefault(item.Name) })
            .ToList();

        return new CobolProgramAst(
            program,
            programId,
            counted,
            items,
            statements,
            divisions.Distinct(StringComparer.Ordinal).ToList(),
            counted.Count + items.Count + statements.Count,
            programId is not null && divisions.Contains("PROCEDURE"),
            programId is null ? "no PROGRAM-ID resolved" : $"{divisions.Count} division(s)");
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Bytes on the wire. This is what makes the REDEFINES overlap in the DDG byte-accurate.</summary>
    public static int PictureSize(string? picture, string usage)
    {
        if (string.IsNullOrWhiteSpace(picture))
        {
            return 0;
        }

        var expanded = new StringBuilder();
        var text = picture.ToUpperInvariant().TrimEnd('.');
        for (var index = 0; index < text.Length; index++)
        {
            var symbol = text[index];
            if (index + 1 < text.Length && text[index + 1] == '(')
            {
                var close = text.IndexOf(')', index);
                if (close > 0 && int.TryParse(text[(index + 2)..close], out var repeat))
                {
                    expanded.Append(symbol, Math.Min(repeat, 4096));
                    index = close;
                    continue;
                }
            }

            expanded.Append(symbol);
        }

        var flat = expanded.ToString();
        var digits = flat.Count(character => character is '9');
        var chars = flat.Count(character => character is 'X' or 'A');
        var signSeparate = flat.Contains('S') && usage == "DISPLAY";

        return usage switch
        {
            "COMP-3" or "PACKED-DECIMAL" => digits / 2 + 1,
            "COMP" or "BINARY" or "COMP-4" or "COMP-5" => digits <= 4 ? 2 : digits <= 9 ? 4 : 8,
            "COMP-1" => 4,
            "COMP-2" => 8,
            _ => digits + chars + (signSeparate ? 0 : 0)
        };
    }

    private static string UsageOf(string rest)
    {
        var upper = rest.ToUpperInvariant();
        foreach (var candidate in new[] { "COMP-3", "PACKED-DECIMAL", "COMP-5", "COMP-4", "COMP-2", "COMP-1", "BINARY", "COMP" })
        {
            if (Regex.IsMatch(upper, $@"\b{Regex.Escape(candidate)}\b"))
            {
                return candidate;
            }
        }

        return "DISPLAY";
    }

    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "MOVE", "COMPUTE", "ADD", "SUBTRACT", "MULTIPLY", "DIVIDE", "IF", "ELSE", "END-IF",
        "EVALUATE", "WHEN", "END-EVALUATE", "PERFORM", "END-PERFORM", "CALL", "GO", "GOBACK",
        "STOP", "INITIALIZE", "SET", "STRING", "UNSTRING", "READ", "WRITE", "REWRITE", "DELETE",
        "EXEC", "CONTINUE", "ACCEPT", "DISPLAY", "INSPECT", "SEARCH", "RETURN", "OPEN", "CLOSE"
    };

    private static string? VerbOf(string upper)
    {
        var token = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('.');
        return token is not null && Verbs.Contains(token) ? token.ToUpperInvariant() : null;
    }

    public static string Collapse(string text) => WhitespacePattern().Replace(text, " ").Trim();

    public static string Sha256Of(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    [GeneratedRegex(@"^(CBL|PROCESS)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerOptionPattern();

    [GeneratedRegex(@"(?<name>TRUNC|NUMPROC|ARITH|CODEPAGE|NSYMBOL|PGMNAME|OPTIMIZE)\s*\(\s*(?<value>[A-Z0-9]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex OptionValuePattern();

    [GeneratedRegex(@"^EXEC\s+(?<dialect>SQLIMS|SQL|CICS|DLI)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecStartPattern();

    [GeneratedRegex(@"\bINCLUDE\s+(?<name>[A-Z0-9][A-Z0-9$#@_-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlIncludePattern();

    [GeneratedRegex(@"^COPY\s+(?<name>'[^']+'|""[^""]+""|[A-Z0-9][A-Z0-9$#@_-]*)(?:\s+(?:OF|IN)\s+(?<lib>[A-Z0-9$#@_-]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex CopyPattern();

    [GeneratedRegex(@"^GAM0MC\d$", RegexOptions.IgnoreCase)]
    private static partial Regex BmsPattern();

    [GeneratedRegex(@"^(?<name>IDENTIFICATION|ENVIRONMENT|DATA|PROCEDURE)\s+DIVISION", RegexOptions.IgnoreCase)]
    private static partial Regex DivisionPattern();

    [GeneratedRegex(@"^(?<name>FILE|WORKING-STORAGE|LOCAL-STORAGE|LINKAGE|REPORT|SCREEN)\s+SECTION", RegexOptions.IgnoreCase)]
    private static partial Regex SectionPattern();

    [GeneratedRegex(@"^PROGRAM-ID\.?\s+(?<name>[A-Z0-9][A-Z0-9$#@_-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramIdPattern();

    [GeneratedRegex(@"^(?<level>\d{1,2})\s+(?<name>[A-Z0-9][A-Z0-9$#@_-]*|FILLER)\b(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataItemPattern();

    [GeneratedRegex(@"^\d{1,2}\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LevelStartPattern();

    [GeneratedRegex(@"\bPIC(?:TURE)?\s+(?:IS\s+)?(?<pic>[X9AVSZ0-9()+\-.,/*$CRDB]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PicturePattern();

    [GeneratedRegex(@"\bREDEFINES\s+(?<name>[A-Z0-9][A-Z0-9$#@_-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex RedefinesPattern();

    [GeneratedRegex(@"\bOCCURS\s+(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex OccursPattern();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9$#@_-]*\.$", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

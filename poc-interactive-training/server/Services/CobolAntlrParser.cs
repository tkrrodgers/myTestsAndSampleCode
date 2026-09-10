using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using PocInteractiveTraining.Server.Cobol.Generated;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Stage S3 via the real ANTLR Cobol85 grammar. The generated parser under Cobol/Generated is
// antlr-ng output committed deliberately, so the build needs neither a JDK nor Node.
//
// Two things about the grammar contract are not obvious and cost three iterations to find:
//   1. Cobol85.g4 never sees "EXEC SQL". It sees a '*>EXECSQL' marker comment carrying the whole
//      block on ONE line, which the preprocessor stage must emit (EXECSQLLINE matches to newline).
//   2. execSqlStatement/execCicsStatement are EXEC*LINE+ with no DOT_FS, so a sentence-terminating
//      period has to survive as its own token rather than being swallowed by the marker line.
// Get either wrong and every program reports ~100 syntax errors that look like a grammar gap.
public static class CobolAntlrParser
{
    public sealed record ParseOutcome(CobolProgramAst Ast, int SyntaxErrors, string? FirstError);

    public static ParseOutcome Parse(string program, CobolFrontEnd.PreparedSource source)
    {
        var errors = new CollectingErrorListener();
        var lexer = new Cobol85Lexer(CharStreams.fromString(source.Text));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);

        var parser = new Cobol85Parser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        var tree = parser.startRule();
        var listener = new AstListener(program, source);
        Antlr4.Runtime.Tree.ParseTreeWalker.Default.Walk(listener, tree);

        return new ParseOutcome(listener.Build(errors.Count), errors.Count, errors.First);
    }

    private sealed class AstListener(string program, CobolFrontEnd.PreparedSource source) : Cobol85BaseListener
    {
        private readonly List<CobolParagraph> _paragraphs = [];
        private readonly List<CobolDataItem> _items = [];
        private readonly List<CobolStatement> _statements = [];
        private readonly List<string> _divisions = [];
        private readonly Dictionary<ParserRuleContext, int> _ordinalOf = [];
        private readonly Dictionary<string, int> _statementCount = new(StringComparer.OrdinalIgnoreCase);

        private string? _programId;
        private string _section = "";
        private string _paragraph = "(program)";
        private int _ordinal;

        private int LineOf(IToken token) => source.OriginalLineOf(token.Line);

        public override void EnterIdentificationDivision(Cobol85Parser.IdentificationDivisionContext context) => _divisions.Add("IDENTIFICATION");

        public override void EnterEnvironmentDivision(Cobol85Parser.EnvironmentDivisionContext context) => _divisions.Add("ENVIRONMENT");

        public override void EnterDataDivision(Cobol85Parser.DataDivisionContext context) => _divisions.Add("DATA");

        public override void EnterProcedureDivision(Cobol85Parser.ProcedureDivisionContext context) => _divisions.Add("PROCEDURE");

        public override void EnterProgramIdParagraph(Cobol85Parser.ProgramIdParagraphContext context) =>
            _programId = context.programName()?.GetText().ToUpperInvariant();

        public override void EnterFileSection(Cobol85Parser.FileSectionContext context) => _section = "FILE";

        public override void EnterWorkingStorageSection(Cobol85Parser.WorkingStorageSectionContext context) => _section = "WORKING-STORAGE";

        public override void EnterLinkageSection(Cobol85Parser.LinkageSectionContext context) => _section = "LINKAGE";

        public override void EnterLocalStorageSection(Cobol85Parser.LocalStorageSectionContext context) => _section = "LOCAL-STORAGE";

        public override void EnterDataDescriptionEntryFormat1(Cobol85Parser.DataDescriptionEntryFormat1Context context)
        {
            var level = context.LEVEL_NUMBER_77() is not null
                ? 77
                : int.TryParse(context.INTEGERLITERAL()?.GetText(), out var parsed) ? parsed : 0;

            var picture = context.dataPictureClause().FirstOrDefault()?.pictureString()?.GetText();
            var usage = UsageOf(context.dataUsageClause().FirstOrDefault()?.GetText());
            var redefines = context.dataRedefinesClause().FirstOrDefault()?.dataName()?.GetText().ToUpperInvariant();
            var occurs = int.TryParse(context.dataOccursClause().FirstOrDefault()?.integerLiteral()?.GetText(), out var times) ? times : 0;
            var length = CobolFrontEnd.PictureSize(picture, usage);

            _items.Add(new CobolDataItem(
                program,
                level,
                context.dataName()?.GetText().ToUpperInvariant() ?? "FILLER",
                picture,
                usage,
                redefines,
                occurs,
                0,
                length * Math.Max(occurs, 1),
                _section,
                source.MemberOf(context.Start.Line),
                LineOf(context.Start)));
        }

        // Level-88 condition names state a field's value domain in business words rather than codes,
        // which makes them the cheapest real semantics in a copybook. Carried as Level 88 with the
        // VALUE clause in Picture.
        public override void EnterDataDescriptionEntryFormat3(Cobol85Parser.DataDescriptionEntryFormat3Context context)
        {
            _items.Add(new CobolDataItem(
                program,
                88,
                context.conditionName()?.GetText().ToUpperInvariant() ?? "",
                CobolFrontEnd.Collapse(context.dataValueClause()?.GetText() ?? ""),
                "DISPLAY",
                null,
                0,
                0,
                0,
                _section,
                program,
                LineOf(context.Start)));
        }

        public override void EnterParagraph(Cobol85Parser.ParagraphContext context)        {
            _paragraph = context.paragraphName().GetText().ToUpperInvariant();
            _paragraphs.Add(new CobolParagraph(program, _paragraph, LineOf(context.Start), 0));
        }

        public override void EnterStatement(Cobol85Parser.StatementContext context)
        {
            // Pre-order walk, so an enclosing statement always has its ordinal before its body does.
            var depth = 0;
            var parentOrdinal = -1;
            for (var node = context.Parent; node is not null; node = node.Parent)
            {
                if (node is not Cobol85Parser.StatementContext ancestor)
                {
                    continue;
                }

                depth++;
                if (parentOrdinal < 0 && _ordinalOf.TryGetValue(ancestor, out var found))
                {
                    parentOrdinal = found;
                }
            }

            _ordinalOf[context] = _ordinal;
            _statements.Add(new CobolStatement(
                program,
                _paragraph,
                _ordinal,
                context.Start.Text.ToUpperInvariant(),
                FirstLineOf(context),
                depth,
                parentOrdinal,
                LineOf(context.Start)));

            _statementCount[_paragraph] = _statementCount.GetValueOrDefault(_paragraph) + 1;
            _ordinal++;
        }

        // An ifStatement context spans its whole body, so only the head line is useful as a label.
        private static string FirstLineOf(ParserRuleContext context)
        {
            if (context.Start is null || context.Stop is null)
            {
                return context.GetText();
            }

            var stream = context.Start.InputStream;
            var text = stream.GetText(Interval.Of(context.Start.StartIndex, context.Stop.StopIndex));
            var newline = text.IndexOf('\n');
            return CobolFrontEnd.Collapse(newline < 0 ? text : text[..newline]);
        }

        private static string UsageOf(string? clause)
        {
            if (string.IsNullOrWhiteSpace(clause))
            {
                return "DISPLAY";
            }

            var upper = clause.ToUpperInvariant();
            foreach (var candidate in new[] { "PACKED-DECIMAL", "COMP-3", "COMP-5", "COMP-4", "COMP-2", "COMP-1", "BINARY", "COMP" })
            {
                if (upper.Contains(candidate, StringComparison.Ordinal))
                {
                    return candidate == "PACKED-DECIMAL" ? "COMP-3" : candidate;
                }
            }

            return "DISPLAY";
        }

        public CobolProgramAst Build(int syntaxErrors)
        {
            var paragraphs = _paragraphs
                .Select(item => item with { StatementCount = _statementCount.GetValueOrDefault(item.Name) })
                .ToList();
            var items = CobolFrontEnd.AssignOffsets(_items);

            return new CobolProgramAst(
                program,
                _programId,
                paragraphs,
                items,
                _statements,
                _divisions.Distinct(StringComparer.Ordinal).ToList(),
                paragraphs.Count + items.Count + _statements.Count,
                syntaxErrors == 0 && _programId is not null,
                syntaxErrors == 0
                    ? $"ANTLR Cobol85, {_divisions.Distinct().Count()} division(s)"
                    : $"ANTLR Cobol85, {syntaxErrors} syntax error(s)");
        }
    }

    private sealed class CollectingErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
    {
        public int Count { get; private set; }

        public string? First { get; private set; }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e) => Record(line, msg);

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e) => Record(line, msg);

        private void Record(int line, string msg)
        {
            Count++;
            First ??= $"line {line}: {(msg.Length > 90 ? msg[..90] : msg)}";
        }
    }
}

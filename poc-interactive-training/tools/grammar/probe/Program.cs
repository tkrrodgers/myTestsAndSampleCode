using Antlr4.Runtime;
using PocInteractiveTraining.Server.Cobol.Generated;

// Throwaway probe: does the antlr-ng-generated Cobol85 parser compile against
// Antlr4.Runtime.Standard, and does it parse real IBM Enterprise COBOL?
var root = args.Length > 0 ? args[0] : @"..\..\training-fixture\gam\cobol";

foreach (var file in Directory.EnumerateFiles(root, "*.cbl").OrderBy(f => f))
{
    var normalised = Normalise(File.ReadAllText(file));
    var errors = new CountingErrorListener();

    var lexer = new Cobol85Lexer(CharStreams.fromString(normalised));
    lexer.RemoveErrorListeners();
    lexer.AddErrorListener(errors);

    var parser = new Cobol85Parser(new CommonTokenStream(lexer));
    parser.RemoveErrorListeners();
    parser.AddErrorListener(errors);

    var clock = System.Diagnostics.Stopwatch.StartNew();
    var tree = parser.startRule();
    clock.Stop();

    Console.WriteLine($"{Path.GetFileName(file),-14} errors={errors.Count,-4} " +
        $"rules={Count(tree),-6} {clock.ElapsedMilliseconds,5} ms" +
        (errors.First is { } first ? $"  first: {first}" : ""));
}

static int Count(Antlr4.Runtime.Tree.IParseTree node)
{
    var total = 1;
    for (var i = 0; i < node.ChildCount; i++)
    {
        total += Count(node.GetChild(i));
    }

    return total;
}

// Columns 1-6 sequence, 7 indicator, 8-72 code. COPY and EXEC blocks are removed because the
// preprocessor grammar owns them; Cobol85.g4 is only ever fed already-preprocessed source.
static string Normalise(string source)
{
    var output = new System.Text.StringBuilder();
    var exec = new System.Text.StringBuilder();
    var inExec = false;
    foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
    {
        if (line.Trim().Length == 0) continue;
        var indicator = line.Length > 6 ? line[6] : ' ';
        if (indicator is '*' or '/' or 'D' or 'd') continue;
        var start = Math.Min(line.Length, 7);
        var end = Math.Min(line.Length, 72);
        var area = end > start ? line[start..end].TrimEnd() : "";
        if (area.Length == 0) continue;

        var trimmed = area.TrimStart();
        if (inExec)
        {
            exec.Append(' ').Append(trimmed);
            if (trimmed.Contains("END-EXEC", StringComparison.OrdinalIgnoreCase))
            {
                inExec = false;
                output.Append("       ").AppendLine(Tag(exec.ToString()));
                exec.Clear();
            }

            continue;
        }

        // Cobol85.g4 only sees EXEC blocks as *>EXECSQL / *>EXECCICS marker lines, one per block.
        if (trimmed.StartsWith("EXEC ", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("END-EXEC", StringComparison.OrdinalIgnoreCase))
            {
                output.Append("       ").AppendLine(Tag(trimmed));
            }
            else
            {
                inExec = true;
                exec.Clear().Append(trimmed);
            }

            continue;
        }

        if (trimmed.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase)) continue;
        output.Append("       ").AppendLine(area);
    }

    return output.ToString();
}

static string Tag(string block)
{
    // execCicsStatement/execSqlStatement are EXEC*LINE+ with no DOT_FS, so the sentence-terminating
    // period has to survive as its own token rather than being swallowed by the marker line.
    var trailingDot = block.TrimEnd().EndsWith('.');
    var body = trailingDot ? block.TrimEnd().TrimEnd('.') : block;
    var tag = block.StartsWith("EXEC CICS", StringComparison.OrdinalIgnoreCase) ? "*>EXECCICS " : "*>EXECSQL ";
    return tag + body + (trailingDot ? Environment.NewLine + "       ." : "");
}

sealed class CountingErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
{
    public int Count { get; private set; }
    public string? First { get; private set; }

    public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol,
        int line, int charPositionInLine, string msg, RecognitionException e) => Record(line, charPositionInLine, msg);

    public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol,
        int line, int charPositionInLine, string msg, RecognitionException e) => Record(line, charPositionInLine, msg);

    private void Record(int line, int column, string msg)
    {
        Count++;
        First ??= $"{line}:{column} {(msg.Length > 70 ? msg[..70] : msg)}";
    }
}

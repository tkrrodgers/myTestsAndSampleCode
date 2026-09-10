using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Drives a real GnuCOBOL install: compiles COBOL, extracts the semantic facts the compiler resolves
// (field layout, packed-decimal attributes, PERFORM THRU ranges), and executes the built program to
// produce ground truth. Fails soft — if cobc is not installed, IsAvailable is false and the scene
// degrades to a read-only explanation rather than breaking.
//
// Fixtures are compiled as free-format because C# raw string literals cannot preserve Area A columns.
public sealed partial class CobolToolchain
{
    private const int TimeoutMs = 30_000;

    private readonly string? _cobc;
    private readonly string? _configDir;
    private readonly string? _copyDir;

    public CobolToolchain()
    {
        _cobc = LocateCompiler();
        if (_cobc is null)
        {
            StatusMessage = "GnuCOBOL not found. Install a prebuilt cobc and set GNUCOBOL_HOME, or add cobc to PATH.";
            return;
        }

        // The package layout puts config under share/gnucobol, not where cobc looks by default.
        var root = Directory.GetParent(Path.GetDirectoryName(_cobc)!)?.FullName;
        foreach (var candidate in new[] { Path.Combine(root ?? "", "share", "gnucobol"), Path.Combine(root ?? "", "") })
        {
            if (Directory.Exists(Path.Combine(candidate, "config")))
            {
                _configDir = Path.Combine(candidate, "config");
                _copyDir = Path.Combine(candidate, "copy");
                break;
            }
        }

        IsAvailable = _configDir is not null;
        StatusMessage = IsAvailable
            ? $"GnuCOBOL ready ({ProbeVersion()})"
            : $"cobc found at {_cobc} but its config directory could not be located.";
    }

    public bool IsAvailable { get; }

    public string StatusMessage { get; }

    // Compiles to intermediate C and mines the artifacts for what the compiler resolved.
    public CobolFacts ExtractFacts(string source)
    {
        if (!IsAvailable)
        {
            return new CobolFacts(false, StatusMessage, [], [], [], "");
        }

        using var work = new TempWorkspace();
        var cbl = work.Write("FIXTURE.cbl", source);
        var result = Run(_cobc!, $"-C -m -free -std=ibm \"{Path.GetFileName(cbl)}\"", work.Path, null);
        if (result.ExitCode != 0)
        {
            return new CobolFacts(false, Clean(result.StdErr), [], [], [], "");
        }

        var localHeader = work.ReadIfExists("FIXTURE.c.l.h");
        var header = work.ReadIfExists("FIXTURE.c.h");
        var body = work.ReadIfExists("FIXTURE.c");

        var attributes = ParseAttributes(header);
        var fields = ParseFields(localHeader, attributes);
        var paragraphs = ParagraphPattern().Matches(body)
            .Select(match => $"line {match.Groups[1].Value}: {match.Groups[2].Value.Trim()}")
            .Distinct()
            .ToList();
        var control = ControlPattern().Matches(body)
            .Select(match => match.Value.Trim())
            .Distinct()
            .ToList();

        control.AddRange(ExtractArithmetic(body, localHeader, fields));

        return new CobolFacts(true, "", fields, paragraphs, control, body);
    }

    // The store flag on cob_decimal_get_field is how the compiler records whether a statement had a
    // ROUNDED clause. 0 truncates toward zero; 1 rounds. Nothing in the COBOL source states this
    // directly, and it is the difference between 15.82 and 15.83.
    private static List<string> ExtractArithmetic(string body, string localHeader, List<CobolField> fields)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in FieldIdPattern().Matches(localHeader))
        {
            byId.TryAdd(match.Groups["id"].Value, match.Groups["name"].Value.Trim());
        }

        var facts = new List<string>();
        foreach (Match match in StorePattern().Matches(body))
        {
            var target = byId.GetValueOrDefault(match.Groups["field"].Value, match.Groups["field"].Value);
            var rounded = match.Groups["flag"].Value != "0";
            var scale = fields.FirstOrDefault(field => field.Name == target)?.Attribute ?? "";
            var where = NearestStatement(body, match.Index);
            facts.Add(rounded
                ? $"{where}result stored into {target} is ROUNDED{(scale.Length > 0 ? " (" + scale + ")" : "")}"
                : $"{where}result stored into {target} is TRUNCATED toward zero - the statement has no ROUNDED clause{(scale.Length > 0 ? " (" + scale + ")" : "")}");
        }

        return facts.Distinct().ToList();
    }

    private static string NearestStatement(string body, int index)
    {
        var matches = StatementPattern().Matches(body[..index]);
        if (matches.Count == 0)
        {
            return "";
        }

        var last = matches[^1];
        return $"line {last.Groups[1].Value} {last.Groups[2].Value.Trim()}: ";
    }

    // Builds the program and runs it once per input, capturing exactly what the legacy logic produces.
    public IReadOnlyList<CobolOracleRun> RunOracle(string source, IReadOnlyList<string> inputs)
    {
        if (!IsAvailable)
        {
            return inputs.Select(input => new CobolOracleRun(input, "", StatusMessage)).ToList();
        }

        using var work = new TempWorkspace();
        work.Write("FIXTURE.cbl", source);
        var build = Run(_cobc!, "-x -free -std=ibm FIXTURE.cbl", work.Path, null);
        if (build.ExitCode != 0)
        {
            var message = Clean(build.StdErr);
            return inputs.Select(input => new CobolOracleRun(input, "", message)).ToList();
        }

        var exe = Path.Combine(work.Path, "FIXTURE.exe");
        var runs = new List<CobolOracleRun>(inputs.Count);
        foreach (var input in inputs)
        {
            var execution = Run(exe, "", work.Path, input);
            runs.Add(execution.ExitCode == 0
                ? new CobolOracleRun(input, execution.StdOut.Trim(), null)
                : new CobolOracleRun(input, "", Clean(execution.StdErr)));
        }

        return runs;
    }

    // Independent COPY resolution. cobc -E runs the preprocessor only, expanding COPY/REPLACING against
    // the -I path list in order — the same semantics as SYSLIB concatenation on z/OS — and emits #line
    // directives naming the exact member each expanded line came from. Used to corroborate our own
    // resolver rather than to replace it: two mechanisms agreeing is evidence, one asserting is not.
    public CopyResolution ResolveCopybooks(string sourceFile, string copyDirectory)
    {
        if (!IsAvailable)
        {
            return new CopyResolution(false, [], [], [], StatusMessage);
        }

        var workingDirectory = Path.GetDirectoryName(sourceFile) ?? Path.GetTempPath();
        var result = Run(_cobc!, $"-E -std=ibm -I \"{copyDirectory}\" \"{sourceFile}\"", workingDirectory, null);
        var self = Path.GetFileNameWithoutExtension(sourceFile);
        var bundled = _copyDir is null ? null : Path.GetFullPath(_copyDir);

        var included = LineDirectivePattern().Matches(result.StdOut)
            .Select(match => match.Groups["file"].Value)
            .Where(path => !Path.GetFileNameWithoutExtension(path).Equals(self, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // cobc satisfying SQLCA from its own copy library is a substitution, not evidence that the
        // estate's member was found. GnuCOBOL's SQLCA is not IBM's.
        bool IsBundled(string path) =>
            bundled is not null && Path.GetFullPath(path).StartsWith(bundled, StringComparison.OrdinalIgnoreCase);

        List<string> Names(IEnumerable<string> paths) => paths
            .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var resolved = Names(included.Where(path => !IsBundled(path)));
        var substituted = Names(included.Where(IsBundled));

        var missing = MissingCopyPattern().Matches(result.StdErr)
            .Select(match => match.Groups["name"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // A non-zero exit is expected here: GnuCOBOL has no DB2 precompiler, so EXEC SQL is a syntax
        // error even though COPY expansion already succeeded. Only the copy verdicts are read.
        return new CopyResolution(true, resolved, substituted, missing,
            $"cobc -E resolved {resolved.Count}, substituted {substituted.Count}, missing {missing.Count}");
    }

    private string ProbeVersion()
    {
        var result = Run(_cobc!, "--version", Path.GetTempPath(), null);
        return result.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? "unknown version";
    }

    private static List<CobolField> ParseFields(string localHeader, Dictionary<string, string> attributes)
    {
        var fields = new List<CobolField>();
        foreach (Match match in FieldPattern().Matches(localHeader))
        {
            var attribute = attributes.GetValueOrDefault(match.Groups["attr"].Value, "");
            fields.Add(new CobolField(
                match.Groups["name"].Value.Trim(),
                match.Groups["base"].Value,
                int.TryParse(match.Groups["offset"].Value, out var offset) ? offset : 0,
                int.TryParse(match.Groups["size"].Value, out var size) ? size : 0,
                attribute));
        }

        return fields;
    }

    private static Dictionary<string, string> ParseAttributes(string header)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AttributePattern().Matches(header))
        {
            var type = match.Groups["type"].Value;
            var digits = match.Groups["digits"].Value;
            var scale = match.Groups["scale"].Value;
            var flags = match.Groups["flags"].Value;
            map[match.Groups["name"].Value] = $"{DescribeType(type)}, {digits} digits, scale {scale}{(flags.EndsWith('1') ? ", signed" : "")}";
        }

        return map;
    }

    // libcob type codes, restricted to the ones the training fixtures produce.
    private static string DescribeType(string hex) => hex switch
    {
        "0x10" => "display numeric",
        "0x11" => "binary",
        "0x12" => "packed decimal (COMP-3)",
        "0x13" => "float",
        "0x21" => "alphanumeric",
        "0x20" => "group",
        _ => $"type {hex}"
    };

    private static string Clean(string text) =>
        string.Join("; ", text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.Contains("ignoring unknown directive", StringComparison.Ordinal))
            .Take(6));

    private static string? LocateCompiler()
    {
        var candidates = new List<string>();
        var home = Environment.GetEnvironmentVariable("GNUCOBOL_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, "bin", "cobc.exe"));
            candidates.Add(Path.Combine(home, "cobc.exe"));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(profile, "tools", "gnucobol-3.2rc1", "gnucobol-3.2rc1-windows-mingw-x64", "bin", "cobc.exe"));

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                candidates.Add(Path.Combine(directory.Trim(), "cobc.exe"));
                candidates.Add(Path.Combine(directory.Trim(), "cobc"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private (int ExitCode, string StdOut, string StdErr) Run(string fileName, string arguments, string workingDirectory, string? stdin)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (_configDir is not null)
        {
            info.Environment["COB_CONFIG_DIR"] = _configDir;
            info.Environment["COB_COPY_DIR"] = _copyDir ?? "";
            var bin = Path.GetDirectoryName(_cobc)!;
            info.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return (-1, "", $"Could not start {fileName}.");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) stdout.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) stderr.AppendLine(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            process.StandardInput.WriteLine(stdin);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit(TimeoutMs))
        {
            try { process.Kill(true); } catch (Exception) { /* already gone */ }
            return (-1, stdout.ToString(), $"Timed out after {TimeoutMs / 1000}s.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    [GeneratedRegex(@"^#line\s+\d+\s+""(?<file>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex LineDirectivePattern();

    [GeneratedRegex(@"(?<name>[A-Za-z0-9$#@_-]+):\s*No such file or directory")]
    private static partial Regex MissingCopyPattern();

    [GeneratedRegex(@"static cob_field (?<id>f_\d+)\s*=\s*\{(?<size>\d+),\s*(?<base>b_\d+)(?:\s*\+\s*(?<offset>\d+))?,\s*&(?<attr>a_\d+)\};\s*/\*\s*(?<name>[^*]+?)\s*\*/")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"static const cob_field_attr (?<name>a_\d+)\s*=\s*\{(?<type>0x[0-9a-fA-F]+),\s*(?<digits>-?\d+),\s*(?<scale>-?\d+),\s*(?<flags>0x[0-9a-fA-F]+)")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"/\* Line: (\d+)\s*:\s*(Paragraph [^:]+):")]
    private static partial Regex ParagraphPattern();

    [GeneratedRegex(@"(?:frame_ptr->perform_through = \d+;|/\* PERFORM [^*]+\*/|/\* Implicit PERFORM return \*/)")]
    private static partial Regex ControlPattern();

    [GeneratedRegex(@"static cob_field (?<id>f_\d+)[^;]*;\s*/\*\s*(?<name>[^*]+?)\s*\*/")]
    private static partial Regex FieldIdPattern();

    [GeneratedRegex(@"cob_decimal_get_field \(d_\d+, &(?<field>f_\d+), (?<flag>\d+)\)")]
    private static partial Regex StorePattern();

    [GeneratedRegex(@"/\* Line: (\d+)\s*:\s*(COMPUTE|ADD|SUBTRACT|MULTIPLY|DIVIDE|MOVE)\s*:")]
    private static partial Regex StatementPattern();

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clara-cobol-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, content.Replace("\r\n", "\n"));
            return file;
        }

        public string ReadIfExists(string name)
        {
            var file = System.IO.Path.Combine(Path, name);
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (Exception) { /* best effort */ }
        }
    }
}

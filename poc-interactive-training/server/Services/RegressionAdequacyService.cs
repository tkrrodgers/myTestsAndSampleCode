using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Objective 2c: does the existing suite actually cover the risk this enhancement introduces?
// This is real mutation testing, not an estimate — the fixture is compiled with Roslyn, the tests are
// executed, then one deliberate defect is injected at a time and the tests are re-executed. A mutant
// that survives is a defect the suite would have shipped. No model is involved in the verdict.
public sealed class RegressionAdequacyService
{
    private const int MutantTimeoutMs = 2000;

    public MutationReport Analyse(string source, string tests)
    {
        var sourceTree = CSharpSyntaxTree.ParseText(source);
        var baseline = Compile(sourceTree, tests, out var baselineErrors);
        if (baseline is null)
        {
            return Failure($"The fixture did not compile: {string.Join("; ", baselineErrors)}");
        }

        var baselineRun = RunTests(baseline);
        if (baselineRun.Failed.Count > 0)
        {
            return Failure($"The unmutated suite must pass before mutation testing means anything. Failing: {string.Join(", ", baselineRun.Failed)}");
        }

        if (baselineRun.Passed.Count == 0)
        {
            return Failure("No test methods were discovered.");
        }

        var sites = MutationSite.Collect(sourceTree);
        var results = new List<MutantResult>();

        for (var index = 0; index < sites.Count; index++)
        {
            var site = sites[index];
            var mutatedTree = new MutationRewriter(index).Apply(sourceTree);
            var mutant = Compile(mutatedTree, tests, out _);
            if (mutant is null)
            {
                // A mutant that will not compile is not a meaningful defect; exclude it rather than
                // inflating the score with a free kill.
                continue;
            }

            var run = RunTests(mutant);
            var killed = run.Failed.Count > 0 || run.TimedOut;
            results.Add(new MutantResult(
                site.Description,
                site.Line,
                site.Original,
                site.Mutated,
                killed,
                killed ? (run.TimedOut ? "timeout" : string.Join(", ", run.Failed)) : "no test failed"));
        }

        if (results.Count == 0)
        {
            return Failure("No viable mutants were generated for this fixture.");
        }

        var survivors = results.Where(result => !result.Killed).ToList();
        var score = (int)Math.Round(100.0 * (results.Count - survivors.Count) / results.Count);

        var verdict = survivors.Count == 0
            ? "Every injected defect was caught. The suite covers the behaviour exercised here."
            : $"{survivors.Count} of {results.Count} injected defects survived. The suite runs green on code that is measurably wrong.";

        return new MutationReport(
            true,
            score,
            results.Count,
            results.Count - survivors.Count,
            baselineRun.Passed,
            results,
            verdict,
            null);
    }

    private static MutationReport Failure(string error) =>
        new(false, 0, 0, 0, [], [], string.Empty, error);

    /// <summary>
    /// The audit Roslyn alone cannot produce. Static analysis finds the predicates; only execution can
    /// say which ones the data exercises and whether the candidate still agrees with production.
    /// </summary>
    public RegressionAuditReport Audit(
        string baselineSource,
        string candidateSource,
        string tests,
        string vectorCsv,
        IReadOnlyList<(string Rule, string Description)> intendedChanges)
    {
        var vectors = ParseVectors(vectorCsv, out var vectorError);
        var differential = vectorError is not null
            ? DifferentialFailure(vectorError)
            : CompareVersions(baselineSource, candidateSource, vectors, intendedChanges);

        var coverage = vectorError is not null
            ? CoverageFailure(vectorError)
            : MeasureConditionCoverage(candidateSource, vectors);

        return new RegressionAuditReport(differential, coverage, Analyse(candidateSource, tests));
    }

    private static DifferentialReport DifferentialFailure(string error) =>
        new(false, 0, 0, 0, 0, 0, [], [], string.Empty, error);

    private static ConditionCoverageReport CoverageFailure(string error) =>
        new(false, 0, 0, 0, [], [], 0, string.Empty, error);

    private static List<string[]> ParseVectors(string csv, out string? error)
    {
        error = null;
        var lines = csv.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        if (lines.Count < 2)
        {
            error = "The test-vector file needs a header row and at least one data row.";
            return [];
        }

        return lines.Skip(1).Select(line => line.Split(',').Select(cell => cell.Trim()).ToArray()).ToList();
    }

    private DifferentialReport CompareVersions(
        string baselineSource,
        string candidateSource,
        List<string[]> vectors,
        IReadOnlyList<(string Rule, string Description)> intendedChanges)
    {
        var baseline = Compile(CSharpSyntaxTree.ParseText(baselineSource), string.Empty, out var baselineErrors);
        if (baseline is null)
        {
            return DifferentialFailure($"The production baseline did not compile: {string.Join("; ", baselineErrors)}");
        }

        var candidate = Compile(CSharpSyntaxTree.ParseText(candidateSource), string.Empty, out var candidateErrors);
        if (candidate is null)
        {
            return DifferentialFailure($"The candidate did not compile: {string.Join("; ", candidateErrors)}");
        }

        var baselineEntry = FindEntryPoint(baseline);
        var candidateEntry = FindEntryPoint(candidate);
        if (baselineEntry is null || candidateEntry is null)
        {
            return DifferentialFailure("No public static entry-point method was found in one of the versions.");
        }

        var ruleProbe = BuildRuleProbe(candidateEntry, intendedChanges);
        var comparisons = new List<VectorComparison>();

        for (var row = 0; row < vectors.Count; row++)
        {
            var cells = vectors[row];
            var baselineResult = Invoke(baselineEntry, cells);
            var candidateResult = Invoke(candidateEntry, cells);
            var differs = !string.Equals(baselineResult, candidateResult, StringComparison.Ordinal);

            var matched = ruleProbe is null
                ? []
                : intendedChanges
                    .Where((_, index) => EvaluateRule(ruleProbe, index, cells))
                    .Select(change => change.Description)
                    .ToList();

            var (classification, note) = (differs, matched.Count > 0) switch
            {
                (true, true) => ("intended", string.Join("; ", matched)),
                (true, false) => ("REGRESSION", "Behaviour changed and no signed-off rule covers this input."),
                (false, true) => ("adjudicate", $"A signed-off rule applies but behaviour did not change: {string.Join("; ", matched)}. Either the change is missing, or a higher-precedence rule legitimately suppresses it. A human decides which."),
                (false, false) => ("identical", "Production and candidate agree.")
            };

            comparisons.Add(new VectorComparison(
                row + 1,
                string.Join(", ", cells),
                baselineResult,
                candidateResult,
                differs,
                classification,
                note));
        }

        var identical = comparisons.Count(item => item.Classification == "identical");
        var intended = comparisons.Count(item => item.Classification == "intended");
        var unintended = comparisons.Count(item => item.Classification == "REGRESSION");
        var missing = comparisons.Count(item => item.Classification == "adjudicate");

        var verdict = unintended > 0
            ? $"{unintended} input(s) changed behaviour with no signed-off rule covering them. Each one is a regression until the business says otherwise."
            : missing > 0
                ? $"No unexplained differences. {missing} input(s) need a human decision: a signed-off rule applies but behaviour did not change."
                : $"Every difference is explained by a signed-off change. {intended} input(s) changed as requested; {identical} are unchanged.";

        return new DifferentialReport(
            true,
            vectors.Count,
            identical,
            intended,
            unintended,
            missing,
            comparisons,
            intendedChanges.Select(change => change.Description).ToList(),
            verdict,
            null);
    }

    /// <summary>
    /// Roslyn finds every branch predicate; a compiled probe then evaluates each one against the real
    /// data. A predicate that is never false is a branch the permutation data never exercises.
    /// </summary>
    private ConditionCoverageReport MeasureConditionCoverage(string candidateSource, List<string[]> vectors)
    {
        var tree = CSharpSyntaxTree.ParseText(candidateSource);
        var candidate = Compile(tree, string.Empty, out _);
        var entry = candidate is null ? null : FindEntryPoint(candidate);
        if (entry is null)
        {
            return CoverageFailure("The candidate did not compile, so its conditions cannot be exercised.");
        }

        var conditions = tree.GetRoot()
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Select(statement => (
                Text: statement.Condition.ToString(),
                Line: statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
            .ToList();

        if (conditions.Count == 0)
        {
            return CoverageFailure("No branch conditions were found in the candidate.");
        }

        var parameters = entry.GetParameters();
        var signature = string.Join(", ", parameters.Select(p => $"{Alias(p.ParameterType)} {p.Name}"));
        var body = string.Join("\n", conditions.Select((condition, index) =>
            $"    public static bool C{index}({signature}) => ({condition.Text});"));

        var probe = Compile(CSharpSyntaxTree.ParseText($"public static class Probe\n{{\n{body}\n}}"), string.Empty, out _);

        // A predicate over locals rather than parameters will not compile in isolation. Report it as
        // skipped rather than pretending it was measured.
        var skipped = 0;
        var details = new List<PredicateCoverage>();
        var probeType = probe?.GetType("Probe");

        for (var index = 0; index < conditions.Count; index++)
        {
            var method = probeType?.GetMethod("C" + index, BindingFlags.Public | BindingFlags.Static);
            if (method is null)
            {
                skipped++;
                details.Add(new PredicateCoverage(conditions[index].Text, conditions[index].Line, false, false, false));
                continue;
            }

            var trueSeen = false;
            var falseSeen = false;
            foreach (var cells in vectors)
            {
                if (!TryBind(method, cells, out var args))
                {
                    continue;
                }

                try
                {
                    if (method.Invoke(null, args) is true) { trueSeen = true; } else { falseSeen = true; }
                }
                catch
                {
                    // An input the predicate cannot evaluate is not coverage evidence either way.
                }
            }

            details.Add(new PredicateCoverage(conditions[index].Text, conditions[index].Line, trueSeen, falseSeen, true));
        }

        var evaluable = details.Where(detail => detail.Evaluable).ToList();
        var full = evaluable.Count(detail => detail.TrueSeen && detail.FalseSeen);
        var percent = evaluable.Count == 0 ? 0 : (int)Math.Round(100.0 * full / evaluable.Count);

        var gaps = evaluable
            .Where(detail => !detail.TrueSeen || !detail.FalseSeen)
            .Select(detail => detail.TrueSeen
                ? $"Line {detail.Line}: `{detail.Expression}` was never false — no data takes the other branch."
                : $"Line {detail.Line}: `{detail.Expression}` was never true — that branch never ran.")
            .ToList();

        var verdict = gaps.Count == 0
            ? "Every branch condition was driven both true and false by the supplied data."
            : $"{gaps.Count} of {evaluable.Count} conditions were only ever driven one way. The data does not reach the whole logic space.";

        return new ConditionCoverageReport(true, evaluable.Count, full, percent, details, gaps, skipped, verdict, null);
    }

    private static Assembly? BuildRuleProbe(MethodInfo entry, IReadOnlyList<(string Rule, string Description)> rules)
    {
        if (rules.Count == 0)
        {
            return null;
        }

        var signature = string.Join(", ", entry.GetParameters().Select(p => $"{Alias(p.ParameterType)} {p.Name}"));
        var body = string.Join("\n", rules.Select((rule, index) =>
            $"    public static bool R{index}({signature}) => ({rule.Rule});"));
        return Compile(CSharpSyntaxTree.ParseText($"public static class Rules\n{{\n{body}\n}}"), string.Empty, out _);
    }

    private static bool EvaluateRule(Assembly probe, int index, string[] cells)
    {
        var method = probe.GetType("Rules")?.GetMethod("R" + index, BindingFlags.Public | BindingFlags.Static);
        if (method is null || !TryBind(method, cells, out var args))
        {
            return false;
        }

        try
        {
            return method.Invoke(null, args) is true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindEntryPoint(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(method => method.GetParameters().Length > 0 && method.ReturnType != typeof(void));

    private static string Invoke(MethodInfo method, string[] cells)
    {
        if (!TryBind(method, cells, out var args))
        {
            return "input did not bind";
        }

        try
        {
            return Convert.ToString(method.Invoke(null, args), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex)
        {
            return "threw " + (ex.InnerException ?? ex).GetType().Name;
        }
    }

    private static bool TryBind(MethodInfo method, string[] cells, out object?[] args)
    {
        var parameters = method.GetParameters();
        args = new object?[parameters.Length];
        if (cells.Length < parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            try
            {
                args[index] = Convert.ChangeType(cells[index], parameters[index].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static string Alias(Type type) => type == typeof(decimal) ? "decimal"
        : type == typeof(bool) ? "bool"
        : type == typeof(int) ? "int"
        : type == typeof(long) ? "long"
        : type == typeof(double) ? "double"
        : type == typeof(string) ? "string"
        : type.FullName ?? type.Name;

    private static Assembly? Compile(SyntaxTree sourceTree, string tests, out List<string> errors)
    {
        var compilation = CSharpCompilation.Create(
            "MutationFixture_" + Guid.NewGuid().ToString("N"),
            [sourceTree, CSharpSyntaxTree.ParseText(tests), CSharpSyntaxTree.ParseText(RegressionSamples.AssertHelper)],
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        errors = emit.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.GetMessage())
            .Take(5)
            .ToList();

        if (!emit.Success)
        {
            return null;
        }

        stream.Seek(0, SeekOrigin.Begin);
        return new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true)
            .LoadFromStream(stream);
    }

    private static List<MetadataReference> ReferenceSet()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Console.dll" or "netstandard.dll" or "System.Linq.dll")
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        if (trusted.Count == 0)
        {
            trusted.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        return trusted;
    }

    private static (List<string> Passed, List<string> Failed, bool TimedOut) RunTests(Assembly assembly)
    {
        var passed = new List<string>();
        var failed = new List<string>();
        var timedOut = false;

        var methods = assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Tests", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.Name.StartsWith("Test", StringComparison.Ordinal) && method.GetParameters().Length == 0)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var method in methods)
        {
            // A mutant can loop forever; a timeout counts as detected, per standard practice.
            var task = Task.Run(() =>
            {
                try
                {
                    method.Invoke(null, null);
                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (!task.Wait(MutantTimeoutMs))
            {
                timedOut = true;
                failed.Add(method.Name + " (timeout)");
                continue;
            }

            if (task.Result)
            {
                passed.Add(method.Name);
            }
            else
            {
                failed.Add(method.Name);
            }
        }

        return (passed, failed, timedOut);
    }

    private sealed record MutationSite(int Index, string Description, int Line, string Original, string Mutated)
    {
        public static List<MutationSite> Collect(SyntaxTree tree)
        {
            var collector = new MutationRewriter(-1);
            collector.Apply(tree);
            return collector.Sites;
        }
    }

    // Visits in a fixed order and rewrites only the site matching the target index, so each mutant
    // carries exactly one defect and the run is reproducible.
    private sealed class MutationRewriter(int target) : CSharpSyntaxRewriter
    {
        private int _seen;
        public List<MutationSite> Sites { get; } = [];

        public SyntaxTree Apply(SyntaxTree tree)
        {
            var root = Visit(tree.GetRoot());
            return CSharpSyntaxTree.Create((CSharpSyntaxNode)root);
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var replacement = node.Kind() switch
            {
                SyntaxKind.GreaterThanExpression => (SyntaxKind.GreaterThanEqualsToken, "boundary widened (> becomes >=)"),
                SyntaxKind.GreaterThanOrEqualExpression => (SyntaxKind.GreaterThanToken, "boundary narrowed (>= becomes >)"),
                SyntaxKind.LessThanExpression => (SyntaxKind.LessThanEqualsToken, "boundary widened (< becomes <=)"),
                SyntaxKind.LessThanOrEqualExpression => (SyntaxKind.LessThanToken, "boundary narrowed (<= becomes <)"),
                SyntaxKind.AddExpression => (SyntaxKind.MinusToken, "addition becomes subtraction"),
                SyntaxKind.SubtractExpression => (SyntaxKind.PlusToken, "subtraction becomes addition"),
                _ => (SyntaxKind.None, string.Empty)
            };

            if (replacement.Item1 == SyntaxKind.None)
            {
                return visited;
            }

            var index = Record(node, replacement.Item2, node.OperatorToken.Text, SyntaxFactory.Token(replacement.Item1).Text);
            return index == target
                ? visited.WithOperatorToken(SyntaxFactory.Token(replacement.Item1).WithTriviaFrom(node.OperatorToken))
                : visited;
        }

        public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            if (!node.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                return base.VisitLiteralExpression(node);
            }

            var text = node.Token.Text;
            var suffix = text.Length > 0 && !char.IsDigit(text[^1]) ? text[^1].ToString() : string.Empty;
            var numeric = suffix.Length > 0 ? text[..^1] : text;
            if (!decimal.TryParse(numeric, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return base.VisitLiteralExpression(node);
            }

            var mutatedText = (value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
            var index = Record(node, "threshold or amount shifted by one", text, mutatedText);
            return index == target
                ? node.WithToken(SyntaxFactory.ParseToken(mutatedText).WithTriviaFrom(node.Token))
                : base.VisitLiteralExpression(node);
        }

        private int Record(SyntaxNode node, string description, string original, string mutated)
        {
            var index = _seen++;
            if (target < 0)
            {
                Sites.Add(new MutationSite(
                    index,
                    description,
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    original,
                    mutated));
            }

            return index;
        }
    }
}

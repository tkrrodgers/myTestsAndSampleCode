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

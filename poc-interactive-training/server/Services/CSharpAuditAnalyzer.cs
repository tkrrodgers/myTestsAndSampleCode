using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Deterministic, reproducible C# audit built on the Roslyn syntax tree. No code is executed;
// analysis is static only. The numeric rating here is authoritative — the model narrates it, it
// never invents it.
public sealed class CSharpAuditAnalyzer
{
    public AuditReport Analyze(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.GetMessage())
            .Take(5)
            .ToList();

        var typeNodes = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        if (typeNodes.Count == 0)
        {
            return new AuditReport(0, "F", [], [new AuditFinding("high", "No type declarations were found to audit.")],
                [], [], [], "No analyzable C# types found.", true,
                errors.Count > 0 ? string.Join("; ", errors) : "No type declarations were parsed.");
        }

        var declaredNames = typeNodes.Select(GetName).ToHashSet(StringComparer.Ordinal);
        var typeMetrics = new List<AuditTypeMetric>();
        var methodMetrics = new List<AuditMethodMetric>();
        var graphEdges = new List<string>();

        foreach (var typeNode in typeNodes)
        {
            var typeName = GetName(typeNode);
            var members = typeNode.Members;
            var methodLike = members.OfType<BaseMethodDeclarationSyntax>().ToList();
            var fieldCount = members.OfType<FieldDeclarationSyntax>().Sum(field => field.Declaration.Variables.Count);
            var publicMembers = members.Count(member => HasModifier(member, SyntaxKind.PublicKeyword));
            var typeLines = LineCount(typeNode);

            var dependsOn = typeNode.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Select(identifier => identifier.Identifier.Text)
                .Where(name => name != typeName && declaredNames.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            foreach (var dependency in dependsOn)
            {
                graphEdges.Add($"{typeName} -> {dependency}");
            }

            typeMetrics.Add(new AuditTypeMetric(typeName, typeNode.Keyword.ValueText, methodLike.Count, fieldCount, publicMembers, typeLines, dependsOn));

            foreach (var method in methodLike)
            {
                methodMetrics.Add(new AuditMethodMetric(
                    typeName,
                    MethodName(method),
                    CyclomaticComplexity(method),
                    LineCount(method),
                    MaxNesting(method),
                    method.ParameterList.Parameters.Count));
            }
        }

        var allNodes = root.DescendantNodes().ToList();
        var magicNumbers = CountMagicNumbers(root);
        var todos = CountTodos(root);
        var docComments = typeNodes.SelectMany(type => type.Members)
            .Count(member => HasModifier(member, SyntaxKind.PublicKeyword) && HasXmlDoc(member));
        var publicMemberTotal = typeMetrics.Sum(type => type.PublicMembers);
        var hasTests = allNodes.OfType<MethodDeclarationSyntax>().Any(HasTestAttribute) ||
            declaredNames.Any(name => name.EndsWith("Tests", StringComparison.Ordinal) || name.EndsWith("Test", StringComparison.Ordinal));
        var nullableEnabled = root.DescendantTrivia().Any(trivia => trivia.ToString().Contains("#nullable", StringComparison.Ordinal));
        var writeLineCalls = allNodes.OfType<MemberAccessExpressionSyntax>()
            .Count(access => access.Name.Identifier.Text is "WriteLine" or "Write");

        var maxComplexity = methodMetrics.Count > 0 ? methodMetrics.Max(method => method.Complexity) : 0;
        var avgComplexity = methodMetrics.Count > 0 ? methodMetrics.Average(method => method.Complexity) : 0;
        var maxMethodLines = methodMetrics.Count > 0 ? methodMetrics.Max(method => method.Lines) : 0;
        var maxNesting = methodMetrics.Count > 0 ? methodMetrics.Max(method => method.MaxNesting) : 0;
        var maxTypeLines = typeMetrics.Max(type => type.Lines);
        var maxTypeMethods = typeMetrics.Max(type => type.Methods);
        var docCoverage = publicMemberTotal > 0 ? (int)Math.Round(100.0 * docComments / publicMemberTotal) : 0;

        // Deterministic dimension scores (0-100).
        var modularity = Clamp(100
            - Math.Max(0, maxMethodLines - 25) * 2
            - Math.Max(0, maxTypeLines - 150) / 2
            - Math.Max(0, maxTypeMethods - 12) * 4);
        var complexity = Clamp(100
            - Math.Max(0, maxComplexity - 8) * 6
            - (int)Math.Max(0, (avgComplexity - 4) * 5));
        var clarity = Clamp(100
            - magicNumbers * 6
            - Math.Max(0, maxNesting - 2) * 12
            - todos * 5);
        var documentation = publicMemberTotal == 0 ? 60 : docCoverage;
        var testability = Clamp((hasTests ? 70 : 25) + (HasConstructorInjection(typeNodes) ? 20 : 0) + (declaredNames.Count > 1 ? 10 : 0));
        var explicitness = Clamp(60
            + (nullableEnabled ? 20 : 0)
            + (AllTypesHaveAccessModifier(typeNodes) ? 15 : 0)
            - writeLineCalls * 5
            - CountWeakTyping(root) * 8);

        var dimensions = new List<AuditScore>
        {
            new("Modularity", modularity, $"largest method {maxMethodLines} lines, largest type {maxTypeLines} lines / {maxTypeMethods} methods"),
            new("Complexity", complexity, $"max cyclomatic {maxComplexity}, avg {avgComplexity:0.0}"),
            new("Clarity", clarity, $"{magicNumbers} magic numbers, max nesting {maxNesting}, {todos} TODO/HACK"),
            new("Documentation", documentation, publicMemberTotal == 0 ? "no public members" : $"{docCoverage}% of public members documented"),
            new("Testability", testability, hasTests ? "tests detected" : "no tests detected"),
            new("Explicitness", explicitness, $"nullable {(nullableEnabled ? "on" : "off")}, {writeLineCalls} console I/O calls")
        };

        // Weighted overall (weights sum to 100).
        var overall = (int)Math.Round(
            modularity * 0.20 +
            complexity * 0.22 +
            clarity * 0.18 +
            documentation * 0.12 +
            testability * 0.16 +
            explicitness * 0.12);
        overall = Clamp(overall);

        var findings = BuildFindings(maxComplexity, maxMethodLines, maxTypeMethods, maxNesting, magicNumbers, hasTests, docCoverage, publicMemberTotal, writeLineCalls, nullableEnabled, errors);

        var summary = $"{typeMetrics.Count} type(s), {methodMetrics.Count} method(s); readiness {overall}/100 ({Grade(overall)}).";

        return new AuditReport(
            overall,
            Grade(overall),
            dimensions,
            findings,
            typeMetrics.OrderByDescending(type => type.Lines).ToList(),
            methodMetrics.OrderByDescending(method => method.Complexity).Take(12).ToList(),
            graphEdges,
            summary,
            false,
            null);
    }

    private static List<AuditFinding> BuildFindings(int maxComplexity, int maxMethodLines, int maxTypeMethods,
        int maxNesting, int magicNumbers, bool hasTests, int docCoverage, int publicMemberTotal, int writeLineCalls,
        bool nullableEnabled, List<string> errors)
    {
        var findings = new List<AuditFinding>();
        if (errors.Count > 0)
        {
            findings.Add(new AuditFinding("medium", $"Roslyn reported parse issues (analysis is partial): {string.Join("; ", errors)}"));
        }

        if (maxComplexity > 10)
        {
            findings.Add(new AuditFinding("high", $"A method has cyclomatic complexity {maxComplexity}; split decision logic into named rules."));
        }

        if (maxMethodLines > 40)
        {
            findings.Add(new AuditFinding("high", $"Longest method is {maxMethodLines} lines; extract cohesive steps into smaller methods."));
        }

        if (maxTypeMethods > 15)
        {
            findings.Add(new AuditFinding("medium", $"A type has {maxTypeMethods} methods; it may be a god class — consider splitting responsibilities."));
        }

        if (maxNesting > 3)
        {
            findings.Add(new AuditFinding("medium", $"Nesting reaches depth {maxNesting}; use guard clauses to flatten control flow."));
        }

        if (magicNumbers > 0)
        {
            findings.Add(new AuditFinding("medium", $"{magicNumbers} magic number(s) found; promote them to named constants to expose business rules."));
        }

        if (!hasTests)
        {
            findings.Add(new AuditFinding("high", "No tests detected; add characterization tests before refactoring or agentic changes."));
        }

        if (publicMemberTotal > 0 && docCoverage < 50)
        {
            findings.Add(new AuditFinding("low", $"Only {docCoverage}% of public members are documented; document the public contract."));
        }

        if (writeLineCalls > 0)
        {
            findings.Add(new AuditFinding("low", $"{writeLineCalls} direct console I/O call(s); separate side effects from business logic."));
        }

        if (!nullableEnabled)
        {
            findings.Add(new AuditFinding("low", "Nullable reference types are not enabled; enabling them makes contracts explicit for agents."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new AuditFinding("low", "No high-severity structural issues detected by static analysis."));
        }

        return findings;
    }

    private static string GetName(TypeDeclarationSyntax type) => type.Identifier.ValueText;

    private static string MethodName(BaseMethodDeclarationSyntax method) => method switch
    {
        MethodDeclarationSyntax named => named.Identifier.ValueText,
        ConstructorDeclarationSyntax ctor => $"{ctor.Identifier.ValueText}()",
        _ => method.Kind().ToString()
    };

    private static int CyclomaticComplexity(SyntaxNode node)
    {
        var decisions = node.DescendantNodes().Count(child => child is
            IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or
            DoStatementSyntax or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax or CatchClauseSyntax or
            ConditionalExpressionSyntax or SwitchExpressionArmSyntax);
        var logical = node.DescendantTokens().Count(token =>
            token.IsKind(SyntaxKind.AmpersandAmpersandToken) ||
            token.IsKind(SyntaxKind.BarBarToken) ||
            token.IsKind(SyntaxKind.QuestionQuestionToken));
        return decisions + logical + 1;
    }

    private static int MaxNesting(SyntaxNode node)
    {
        var max = 0;
        void Walk(SyntaxNode current, int depth)
        {
            max = Math.Max(max, depth);
            foreach (var child in current.ChildNodes())
            {
                var next = child is BlockSyntax or IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
                    or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or TryStatementSyntax
                    ? depth + 1
                    : depth;
                Walk(child, next);
            }
        }

        Walk(node, 0);
        return max;
    }

    private static int LineCount(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    private static bool HasModifier(MemberDeclarationSyntax member, SyntaxKind modifier) =>
        member.Modifiers.Any(token => token.IsKind(modifier));

    private static bool HasXmlDoc(SyntaxNode node) =>
        node.GetLeadingTrivia().Any(trivia =>
            trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static bool HasTestAttribute(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString() is "Test" or "Fact" or "Theory" or "TestMethod");

    private static bool HasConstructorInjection(IEnumerable<TypeDeclarationSyntax> types) =>
        types.SelectMany(type => type.Members.OfType<ConstructorDeclarationSyntax>())
            .Any(ctor => ctor.ParameterList.Parameters.Count > 0);

    private static bool AllTypesHaveAccessModifier(IEnumerable<TypeDeclarationSyntax> types) =>
        types.All(type => type.Modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.InternalKeyword) ||
            token.IsKind(SyntaxKind.PrivateKeyword) || token.IsKind(SyntaxKind.ProtectedKeyword)));

    private static int CountWeakTyping(SyntaxNode root) =>
        root.DescendantNodes().OfType<ArrayTypeSyntax>()
            .Count(array => array.ElementType is PredefinedTypeSyntax predefined &&
                predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword));

    private static int CountMagicNumbers(SyntaxNode root) =>
        root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Count(literal => literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
                literal.Token.ValueText is not ("0" or "1" or "2") &&
                !IsInsideConstField(literal));

    private static bool IsInsideConstField(SyntaxNode node) =>
        node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } field &&
        field.Modifiers.Any(token => token.IsKind(SyntaxKind.ConstKeyword));

    private static int CountTodos(SyntaxNode root) =>
        root.DescendantTrivia()
            .Count(trivia => (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) &&
                (trivia.ToString().Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
                 trivia.ToString().Contains("HACK", StringComparison.OrdinalIgnoreCase)));

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);

    private static string Grade(int score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F"
    };
}

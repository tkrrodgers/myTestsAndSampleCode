using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// CLARA — Clear Language for Auditable Rules & Arithmetic.
//
// A small, deterministic, side-effect-free business-rule language designed to be legible to an LLM and a
// human at once. This is a real compiler, not a tree-walking interpreter: it lexes with source positions,
// parses into a structural model, performs full static type checking over the money/percent/number/integer/
// boolean type system, lints the decision table, and emits a .NET expression tree that is JIT-compiled to
// machine code. The resulting delegate operates on a caller-owned decimal frame with zero allocation and
// zero dictionary lookups per call, which is what makes CLARA competitive with hand-written C#.
public static class ClaraCompiler
{
    public static ClaraCompilation Compile(string source)
    {
        var clock = Stopwatch.StartNew();
        var diagnostics = new List<ClaraDiagnostic>();
        var program = ParseStructure(source ?? "", diagnostics);
        var policy = Bind(program, diagnostics);
        clock.Stop();

        var failed = diagnostics.Any(diagnostic => diagnostic.Severity == ClaraSeverity.Error);
        return new ClaraCompilation(
            !failed && policy is not null,
            failed ? null : policy,
            diagnostics,
            clock.Elapsed.TotalMilliseconds);
    }

    // Compiles and then executes every declared example — the self-verifying contract the UI renders.
    public static ClaraProgramResult Run(string source)
    {
        var compilation = Compile(source);
        var errors = compilation.Diagnostics.Where(d => d.Severity == ClaraSeverity.Error).Select(d => d.Format()).ToList();
        var warnings = compilation.Diagnostics.Where(d => d.Severity == ClaraSeverity.Warning).Select(d => d.Format()).ToList();
        var policy = compilation.Policy;

        var examples = new List<ClaraExampleResult>();
        if (policy is not null)
        {
            foreach (var example in policy.Examples)
            {
                examples.Add(policy.RunExample(example));
            }
        }

        return new ClaraProgramResult(
            compilation.Success,
            policy?.Name,
            policy?.Description,
            errors,
            warnings,
            Describe(policy?.Inputs),
            Describe(policy?.Derived),
            Describe(policy?.Outputs),
            policy?.Requires.Select(check => $"{check.Expression} — {check.Message}").ToList() ?? [],
            policy?.Invariants.Select(check => $"{check.Expression} — {check.Message}").ToList() ?? [],
            policy?.Rules.Select(rule => new ClaraRuleSummary(rule.Name, rule.IsOtherwise, rule.Line, rule.Because, rule.Owner)).ToList() ?? [],
            examples,
            compilation.Diagnostics,
            policy?.Rules.Count ?? 0,
            compilation.CompileMilliseconds);
    }

    private static List<string> Describe(IReadOnlyList<ClaraSymbol>? symbols) =>
        symbols?.Select(symbol => symbol.Description is { Length: > 0 } text
            ? $"{symbol.Name}: {ClaraTypes.Name(symbol.Type)} — {text}"
            : $"{symbol.Name}: {ClaraTypes.Name(symbol.Type)}").ToList() ?? [];

    // ---------------------------------------------------------------- structure

    private sealed record RawDecl(string Name, string TypeText, string? Description, int Line, int Column);

    private sealed record RawConst(string Name, string TypeText, string Expression, string? Description, int Line, int Column, int ExprColumn);

    private sealed record RawAssign(string Target, string Expression, int Line, int TargetColumn, int ExprColumn);

    private sealed record RawCheck(string Expression, string? Message, int Line, int Column);

    private sealed class RawRule
    {
        public string Name = "";
        public int Line;
        public int Column;
        public string? When;
        public int WhenLine;
        public int WhenColumn;
        public bool IsOtherwise;
        public bool HasCondition;
        public string? Because;
        public string? Owner;
        public List<RawAssign> Assignments { get; } = [];
    }

    private sealed record RawBind(string Name, string Expression, int Line, int NameColumn, int ExprColumn);

    private sealed class RawExample
    {
        public string Description = "";
        public int Line;
        public List<RawBind> Inputs { get; } = [];
        public List<RawBind> Expects { get; } = [];
    }

    private sealed class RawDerive
    {
        public required RawDecl Declaration { get; init; }
        public List<RawRule> Rules { get; } = [];
    }

    private sealed class RawProgram
    {
        public string? PolicyName;
        public string? PolicyDescription;
        public int PolicyLine;
        public List<RawDecl> Inputs { get; } = [];
        public List<RawDecl> Outputs { get; } = [];
        public List<RawConst> Constants { get; } = [];
        public List<RawDerive> Derives { get; } = [];
        public List<RawRule> Rules { get; } = [];
        public List<RawCheck> Requires { get; } = [];
        public List<RawCheck> Invariants { get; } = [];
        public List<RawExample> Examples { get; } = [];
    }

    private static RawProgram ParseStructure(string source, List<ClaraDiagnostic> diagnostics)
    {
        var program = new RawProgram();
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var section = Section.None;
        RawRule? rule = null;
        RawExample? example = null;
        RawDerive? derive = null;

        for (var lineNumber = 1; lineNumber <= lines.Length; lineNumber++)
        {
            var original = StripComment(lines[lineNumber - 1]);
            var indent = 0;
            while (indent < original.Length && char.IsWhiteSpace(original[indent]))
            {
                indent++;
            }

            var text = original[indent..].TrimEnd();
            if (text.Length == 0)
            {
                continue;
            }

            var baseColumn = indent + 1;
            var scope = section switch
            {
                Section.Rule when rule is not null && derive is not null => $"rule '{rule.Name}' in derive '{derive.Declaration.Name}'",
                Section.Rule when rule is not null => $"rule '{rule.Name}'",
                Section.Rule when derive is not null => $"derive '{derive.Declaration.Name}'",
                Section.Examples when example is not null => $"example '{example.Description}'",
                _ => SectionName(section)
            };

            if (text.StartsWith("policy ", StringComparison.Ordinal))
            {
                var name = text[7..].Trim();
                if (program.PolicyName is not null)
                {
                    diagnostics.Add(Error("CLARA002", lineNumber, baseColumn, "policy", "A source file declares exactly one policy."));
                }
                else if (!IsIdentifier(name))
                {
                    diagnostics.Add(Error("CLARA005", lineNumber, baseColumn + 7, "policy", $"'{name}' is not a valid policy name (letters, digits and underscore, not starting with a digit)."));
                }
                else
                {
                    program.PolicyName = name;
                    program.PolicyLine = lineNumber;
                }

                section = Section.None;
                continue;
            }

            // A bare quoted line straight after the policy header is the policy's own description.
            if (section == Section.None && derive is null && program.PolicyName is not null &&
                program.PolicyDescription is null && text.Length > 1 && text[0] == '"' && text[^1] == '"')
            {
                program.PolicyDescription = text[1..^1].Trim();
                continue;
            }

            if (text is "rules:")
            {
                section = Section.Rule;
                rule = null;
                derive = null;
                example = null;
                continue;
            }

            if (TryReadSectionHeader(text, out var header))
            {
                section = header;
                rule = null;
                derive = null;
                example = null;
                continue;
            }

            if (text.StartsWith("derive ", StringComparison.Ordinal))
            {
                var (body, description) = SplitTrailingString(text[7..].Trim());
                if (!TrySplit(body, ':', out var deriveName, out var deriveType, out _))
                {
                    diagnostics.Add(Error("CLARA004", lineNumber, baseColumn, "derives", $"Invalid derive '{text}' (expected 'derive name: type \"what it means\"')."));
                    continue;
                }

                derive = new RawDerive { Declaration = new RawDecl(deriveName, deriveType, description, lineNumber, baseColumn) };
                program.Derives.Add(derive);
                section = Section.Rule;
                rule = null;
                example = null;
                continue;
            }

            if (text.StartsWith("rule ", StringComparison.Ordinal) && text.EndsWith(':'))
            {
                rule = new RawRule
                {
                    Name = text[5..^1].Trim(),
                    Line = lineNumber,
                    Column = baseColumn,
                    Owner = derive?.Declaration.Name
                };

                if (!IsIdentifier(rule.Name))
                {
                    diagnostics.Add(Error("CLARA005", lineNumber, baseColumn + 5, "rules", $"'{rule.Name}' is not a valid rule name."));
                }

                (derive?.Rules ?? program.Rules).Add(rule);
                section = Section.Rule;
                example = null;
                continue;
            }

            if (text.StartsWith("example ", StringComparison.Ordinal) && text.EndsWith(':'))
            {
                example = new RawExample { Description = text[8..^1].Trim().Trim('"').Trim(), Line = lineNumber };
                program.Examples.Add(example);
                section = Section.Examples;
                rule = null;
                derive = null;
                continue;
            }

            switch (section)
            {
                case Section.Inputs:
                case Section.Outputs:
                {
                    var (body, description) = SplitTrailingString(text);
                    if (!TrySplit(body, ':', out var name, out var typeText, out _))
                    {
                        diagnostics.Add(Error("CLARA004", lineNumber, baseColumn, scope, $"Invalid declaration '{text}' (expected 'name: type \"what it means\"')."));
                        break;
                    }

                    var declaration = new RawDecl(name, typeText, description, lineNumber, baseColumn);
                    (section == Section.Inputs ? program.Inputs : program.Outputs).Add(declaration);
                    break;
                }

                case Section.Constants:
                {
                    var (body, description) = SplitTrailingString(text);
                    var equals = body.IndexOf('=');
                    if (equals < 0 || !TrySplit(body[..equals], ':', out var name, out var typeText, out _))
                    {
                        diagnostics.Add(Error("CLARA004", lineNumber, baseColumn, scope, $"Invalid constant '{text}' (expected 'name: type = expression \"why it is this value\"')."));
                        break;
                    }

                    program.Constants.Add(new RawConst(name, typeText, body[(equals + 1)..].Trim(), description, lineNumber, baseColumn, baseColumn + equals + 1));
                    break;
                }

                case Section.Requires:
                case Section.Invariants:
                {
                    var (body, message) = SplitTrailingString(text);
                    if (body.Length == 0)
                    {
                        diagnostics.Add(Error("CLARA016", lineNumber, baseColumn, scope, $"Invalid check '{text}' (expected '<condition> \"why it must hold\"')."));
                        break;
                    }

                    var check = new RawCheck(body, message, lineNumber, baseColumn);
                    (section == Section.Requires ? program.Requires : program.Invariants).Add(check);
                    break;
                }

                case Section.Rule:
                {
                    if (rule is null)
                    {
                        diagnostics.Add(Error("CLARA009", lineNumber, baseColumn, "rules", "A rule body appeared before its 'rule <Name>:' header."));
                        break;
                    }

                    if (text.Equals("otherwise", StringComparison.Ordinal))
                    {
                        if (rule.HasCondition)
                        {
                            diagnostics.Add(Error("CLARA011", lineNumber, baseColumn, scope, "A rule has either a 'when' condition or 'otherwise', not both."));
                        }

                        rule.IsOtherwise = true;
                        rule.HasCondition = true;
                        break;
                    }

                    if (text.StartsWith("when ", StringComparison.Ordinal))
                    {
                        if (rule.HasCondition)
                        {
                            diagnostics.Add(Error("CLARA011", lineNumber, baseColumn, scope, "A rule declares exactly one 'when' condition."));
                        }

                        rule.When = text[5..].Trim();
                        rule.WhenLine = lineNumber;
                        rule.WhenColumn = baseColumn + 5;
                        rule.HasCondition = true;
                        break;
                    }

                    if (text.StartsWith("then ", StringComparison.Ordinal))
                    {
                        var body = text[5..];
                        var equals = body.IndexOf('=');
                        if (equals < 0)
                        {
                            diagnostics.Add(Error("CLARA009", lineNumber, baseColumn, scope, $"Invalid assignment '{text}' (expected 'then name = expression')."));
                            break;
                        }

                        rule.Assignments.Add(new RawAssign(
                            body[..equals].Trim(),
                            body[(equals + 1)..].Trim(),
                            lineNumber,
                            baseColumn + 5,
                            baseColumn + 5 + equals + 1));
                        break;
                    }

                    if (text.StartsWith("because ", StringComparison.Ordinal))
                    {
                        var (_, citation) = SplitTrailingString(text);
                        if (citation is null)
                        {
                            diagnostics.Add(Error("CLARA009", lineNumber, baseColumn, scope, "'because' must be followed by a quoted citation, for example: because \"ADR-024\"."));
                            break;
                        }

                        rule.Because = citation;
                        break;
                    }

                    diagnostics.Add(Error("CLARA009", lineNumber, baseColumn, scope, $"Unexpected rule line '{text}' (expected 'when', 'otherwise', 'then' or 'because')."));
                    break;
                }

                case Section.Examples:
                {
                    if (example is null)
                    {
                        diagnostics.Add(Error("CLARA009", lineNumber, baseColumn, "examples", "An example body appeared before its 'example \"...\":' header."));
                        break;
                    }

                    var isExpect = text.StartsWith("expect ", StringComparison.Ordinal);
                    var offset = isExpect ? 7 : 0;
                    var body = text[offset..];
                    var equals = body.IndexOf('=');
                    if (equals < 0)
                    {
                        diagnostics.Add(Error("CLARA013", lineNumber, baseColumn, scope, $"Invalid example line '{text}' (expected 'name = value' or 'expect name = value')."));
                        break;
                    }

                    var bind = new RawBind(
                        body[..equals].Trim(),
                        body[(equals + 1)..].Trim(),
                        lineNumber,
                        baseColumn + offset,
                        baseColumn + offset + equals + 1);
                    (isExpect ? example.Expects : example.Inputs).Add(bind);
                    break;
                }

                default:
                    diagnostics.Add(Error("CLARA009", lineNumber, baseColumn, "policy", $"'{text}' is outside any section. Expected inputs:, constants:, rule <Name>:, output: or examples:."));
                    break;
            }
        }

        return program;
    }

    private enum Section { None, Inputs, Constants, Outputs, Rule, Examples, Requires, Invariants }

    private static string SectionName(Section section) => section switch
    {
        Section.Inputs => "inputs",
        Section.Constants => "constants",
        Section.Outputs => "output",
        Section.Rule => "rules",
        Section.Examples => "examples",
        Section.Requires => "requires",
        Section.Invariants => "invariants",
        _ => "policy"
    };

    private static bool TryReadSectionHeader(string text, out Section section)
    {
        section = text switch
        {
            "inputs:" => Section.Inputs,
            "constants:" => Section.Constants,
            "output:" or "outputs:" => Section.Outputs,
            "examples:" => Section.Examples,
            "requires:" => Section.Requires,
            "invariants:" => Section.Invariants,
            _ => Section.None
        };

        return section != Section.None;
    }

    // Splits a trailing quoted annotation off a line. CLARA has no string literals in expressions,
    // so a quote at the end of a line is unambiguously documentation.
    private static (string Body, string? Description) SplitTrailingString(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length < 2 || trimmed[^1] != '"')
        {
            return (trimmed, null);
        }

        var open = trimmed.LastIndexOf('"', trimmed.Length - 2);
        return open < 0
            ? (trimmed, null)
            : (trimmed[..open].TrimEnd(), trimmed[(open + 1)..^1].Trim());
    }

    private static bool TrySplit(string text, char separator, out string left, out string right, out int rightOffset)
    {
        var index = text.IndexOf(separator);
        if (index < 0)
        {
            left = right = "";
            rightOffset = 0;
            return false;
        }

        left = text[..index].Trim();
        right = text[(index + 1)..].Trim();
        rightOffset = index + 1;
        return left.Length > 0 && right.Length > 0;
    }

    // A '#' or '//' starts a comment, but not inside a quoted example description.
    private static string StripComment(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '#' || (current == '/' && index + 1 < line.Length && line[index + 1] == '/'))
            {
                return line[..index];
            }
        }

        return line;
    }

    // ---------------------------------------------------------------- binding, type checking, codegen

    private sealed record Symbol(string Name, ClaraType Type, int Slot, int Line, string? Description);

    private sealed class Scope
    {
        public Dictionary<string, Symbol> Inputs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Symbol> Outputs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Symbol> Derived { get; } = new(StringComparer.Ordinal);
        public HashSet<string> VisibleDerived { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TypedValue> Constants { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UsedInputs { get; } = new(StringComparer.Ordinal);
        public bool AllowOutputReads { get; set; }
        public required ParameterExpression Frame { get; init; }
    }

    private readonly record struct TypedValue(ClaraType Type, decimal Number);

    private static ClaraPolicy? Bind(RawProgram program, List<ClaraDiagnostic> diagnostics)
    {
        if (program.PolicyName is null)
        {
            diagnostics.Add(Error("CLARA001", 1, 1, "policy", "Missing 'policy <Name>' declaration."));
        }

        var frame = Expression.Parameter(typeof(decimal[]), "frame");
        var scope = new Scope { Frame = frame };
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);

        void Declare(RawDecl declaration, SymbolKind kind)
        {
            var where = kind switch { SymbolKind.Input => "inputs", SymbolKind.Output => "output", _ => "derives" };
            if (!IsIdentifier(declaration.Name) || Reserved.Contains(declaration.Name))
            {
                diagnostics.Add(Error("CLARA005", declaration.Line, declaration.Column, where,
                    $"'{declaration.Name}' is not a usable name (reserved word or invalid identifier)."));
                return;
            }

            if (taken.TryGetValue(declaration.Name, out var firstLine))
            {
                diagnostics.Add(Error("CLARA002", declaration.Line, declaration.Column, where,
                    $"'{declaration.Name}' is already declared on line {firstLine}."));
                return;
            }

            if (!ClaraTypes.TryParse(declaration.TypeText, out var type))
            {
                diagnostics.Add(Error("CLARA003", declaration.Line, declaration.Column, where,
                    $"Unknown type '{declaration.TypeText}' (use money, percent, number, integer or boolean)."));
                return;
            }

            if (string.IsNullOrWhiteSpace(declaration.Description))
            {
                diagnostics.Add(Warning("CLARA106", declaration.Line, declaration.Column, where,
                    $"'{declaration.Name}' has no description. A reader with no prior knowledge of this system cannot tell what it means — add \"...\" after the type."));
            }

            taken[declaration.Name] = declaration.Line;
            var slot = scope.Inputs.Count + scope.Derived.Count + scope.Outputs.Count;
            var symbol = new Symbol(declaration.Name, type, slot, declaration.Line, declaration.Description);
            switch (kind)
            {
                case SymbolKind.Input: scope.Inputs[declaration.Name] = symbol; break;
                case SymbolKind.Derived: scope.Derived[declaration.Name] = symbol; break;
                default: scope.Outputs[declaration.Name] = symbol; break;
            }
        }

        foreach (var declaration in program.Inputs)
        {
            Declare(declaration, SymbolKind.Input);
        }

        foreach (var deriveBlock in program.Derives)
        {
            Declare(deriveBlock.Declaration, SymbolKind.Derived);
        }

        foreach (var declaration in program.Outputs)
        {
            Declare(declaration, SymbolKind.Output);
        }

        foreach (var constant in program.Constants)
        {
            if (!IsIdentifier(constant.Name) || Reserved.Contains(constant.Name))
            {
                diagnostics.Add(Error("CLARA005", constant.Line, constant.Column, "constants", $"'{constant.Name}' is not a usable constant name."));
                continue;
            }

            if (taken.TryGetValue(constant.Name, out var firstLine))
            {
                diagnostics.Add(Error("CLARA002", constant.Line, constant.Column, "constants", $"'{constant.Name}' is already declared on line {firstLine}."));
                continue;
            }

            if (!ClaraTypes.TryParse(constant.TypeText, out var declaredType))
            {
                diagnostics.Add(Error("CLARA003", constant.Line, constant.Column, "constants", $"Unknown type '{constant.TypeText}' for constant '{constant.Name}'."));
                continue;
            }

            var folded = CompileConstant(constant.Expression, scope, constant.Line, constant.ExprColumn, "constants", diagnostics);
            if (folded is not { } value)
            {
                continue;
            }

            if (!Assignable(declaredType, value.Type))
            {
                diagnostics.Add(Error("CLARA007", constant.Line, constant.ExprColumn, "constants",
                    $"Constant '{constant.Name}' is declared {ClaraTypes.Name(declaredType)} but its value is {ClaraTypes.Name(value.Type)}."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(constant.Description))
            {
                diagnostics.Add(Warning("CLARA106", constant.Line, constant.Column, "constants",
                    $"Constant '{constant.Name}' has no description. A local threshold without its source is knowledge only your team has — add \"...\" citing where the value comes from."));
            }

            taken[constant.Name] = constant.Line;
            scope.Constants[constant.Name] = new TypedValue(declaredType, value.Number);
        }

        if (scope.Outputs.Count == 0)
        {
            diagnostics.Add(Error("CLARA014", program.PolicyLine == 0 ? 1 : program.PolicyLine, 1, "output", "The policy declares no outputs."));
        }

        if (program.Rules.Count == 0)
        {
            diagnostics.Add(Error("CLARA014", program.PolicyLine == 0 ? 1 : program.PolicyLine, 1, "rules", "The policy has no rules."));
        }

        // One table compiler serves the policy's outputs and every derived stage.
        var ruleNames = new Dictionary<string, int>(StringComparer.Ordinal);

        List<(Expression? Condition, Expression Body)> CompileTable(List<RawRule> rawRules, Symbol? derivedTarget, List<ClaraRuleInfo>? infos)
        {
            var blocks = new List<(Expression? Condition, Expression Body)>();
            var required = derivedTarget is null
                ? scope.Outputs.Values.OrderBy(symbol => symbol.Slot).ToList()
                : [derivedTarget];
            var label = derivedTarget is null ? "" : $" in derive '{derivedTarget.Name}'";

            for (var index = 0; index < rawRules.Count; index++)
            {
                var rule = rawRules[index];
                var ruleScope = $"rule '{rule.Name}'{label}";

                if (ruleNames.TryGetValue(rule.Name, out var firstLine))
                {
                    diagnostics.Add(Error("CLARA002", rule.Line, rule.Column, ruleScope, $"A rule named '{rule.Name}' is already defined on line {firstLine}."));
                }
                else
                {
                    ruleNames[rule.Name] = rule.Line;
                }

                Expression? condition = null;
                if (!rule.HasCondition)
                {
                    diagnostics.Add(Error("CLARA011", rule.Line, rule.Column, ruleScope, "A rule needs a 'when <condition>' line or the single word 'otherwise'."));
                }
                else if (!rule.IsOtherwise)
                {
                    var compiled = CompileExpression(rule.When!, scope, rule.WhenLine, rule.WhenColumn, ruleScope, diagnostics);
                    if (compiled is { } typed)
                    {
                        if (typed.Type != ClaraType.Boolean)
                        {
                            diagnostics.Add(Error("CLARA007", rule.WhenLine, rule.WhenColumn, ruleScope,
                                $"A 'when' condition must be boolean but this is {ClaraTypes.Name(typed.Type)}."));
                        }
                        else
                        {
                            condition = typed.Expression;
                        }
                    }
                }

                var assigned = new Dictionary<string, int>(StringComparer.Ordinal);
                var body = new List<Expression>();
                foreach (var assignment in rule.Assignments)
                {
                    Symbol? target;
                    if (derivedTarget is not null)
                    {
                        if (assignment.Target != derivedTarget.Name)
                        {
                            diagnostics.Add(Error("CLARA018", assignment.Line, assignment.TargetColumn, ruleScope,
                                $"A rule inside 'derive {derivedTarget.Name}' may only assign '{derivedTarget.Name}', not '{assignment.Target}'."));
                            continue;
                        }

                        target = derivedTarget;
                    }
                    else if (!scope.Outputs.TryGetValue(assignment.Target, out target))
                    {
                        var hint = scope.Inputs.ContainsKey(assignment.Target) || scope.Constants.ContainsKey(assignment.Target)
                            ? " Inputs and constants are read-only."
                            : scope.Derived.ContainsKey(assignment.Target)
                                ? $" '{assignment.Target}' is a derived stage and is assigned inside its own 'derive' block."
                                : "";
                        diagnostics.Add(Error("CLARA010", assignment.Line, assignment.TargetColumn, ruleScope,
                            $"'{assignment.Target}' is not a declared output.{hint}"));
                        continue;
                    }

                    if (assigned.TryGetValue(assignment.Target, out var firstAssignment))
                    {
                        diagnostics.Add(Error("CLARA002", assignment.Line, assignment.TargetColumn, ruleScope,
                            $"'{assignment.Target}' is already assigned by this rule on line {firstAssignment}."));
                        continue;
                    }

                    var compiled = CompileExpression(assignment.Expression, scope, assignment.Line, assignment.ExprColumn, ruleScope, diagnostics);
                    if (compiled is not { } value)
                    {
                        continue;
                    }

                    if (!Assignable(target.Type, value.Type))
                    {
                        diagnostics.Add(Error("CLARA007", assignment.Line, assignment.ExprColumn, ruleScope,
                            $"'{target.Name}' is {ClaraTypes.Name(target.Type)} but the expression is {ClaraTypes.Name(value.Type)}. {ConversionHint(target.Type)}"));
                        continue;
                    }

                    if (target.Type == ClaraType.Money && value.Inexact)
                    {
                        diagnostics.Add(Warning("CLARA103", assignment.Line, assignment.ExprColumn, ruleScope,
                            $"Money value '{target.Name}' is produced by division or a rate multiplication without an explicit round(...). State the rounding so cents are not left to chance."));
                    }

                    assigned[assignment.Target] = assignment.Line;
                    body.Add(Expression.Assign(Slot(scope, target), Store(value)));
                }

                foreach (var symbol in required)
                {
                    if (!assigned.ContainsKey(symbol.Name))
                    {
                        diagnostics.Add(Error("CLARA012", rule.Line, rule.Column, ruleScope,
                            $"Rule '{rule.Name}' does not assign '{symbol.Name}'. Every rule must produce every value it owns so no path can leave one undefined."));
                    }
                }

                if (string.IsNullOrWhiteSpace(rule.Because))
                {
                    diagnostics.Add(Warning("CLARA107", rule.Line, rule.Column, ruleScope,
                        $"Rule '{rule.Name}' has no 'because \"...\"'. Without the authority behind a rule, a later reader cannot tell whether it is safe to change."));
                }

                infos?.Add(new ClaraRuleInfo(rule.Name, rule.IsOtherwise, rule.Line, rule.Because, derivedTarget?.Name));

                if (derivedTarget is null)
                {
                    body.Add(Expression.Constant(index));
                    blocks.Add((rule.IsOtherwise ? null : condition, Expression.Block(typeof(int), body)));
                }
                else
                {
                    blocks.Add((rule.IsOtherwise ? null : condition,
                        Expression.Block(typeof(void), body.Count == 0 ? [Expression.Empty()] : body)));
                }
            }

            return blocks;
        }

        void LintTable(List<RawRule> rawRules, string label, bool exhaustivenessIsAnError)
        {
            var otherwiseIndex = rawRules.FindIndex(rule => rule.IsOtherwise);
            if (otherwiseIndex >= 0)
            {
                for (var index = otherwiseIndex + 1; index < rawRules.Count; index++)
                {
                    var rule = rawRules[index];
                    diagnostics.Add(Warning("CLARA100", rule.Line, rule.Column, $"rule '{rule.Name}'",
                        $"Unreachable: it follows the 'otherwise' rule '{rawRules[otherwiseIndex].Name}' on line {rawRules[otherwiseIndex].Line}."));
                }
            }
            else if (rawRules.Count > 0)
            {
                var last = rawRules[^1];
                diagnostics.Add(exhaustivenessIsAnError
                    ? Error("CLARA017", last.Line, last.Column, label,
                        $"{label} must end with an 'otherwise' rule; a derived stage has no way to signal that nothing matched.")
                    : Warning("CLARA101", last.Line, last.Column, label,
                        "No 'otherwise' rule: the decision table is not exhaustive and some inputs may match no rule."));
            }
        }

        var deriveChains = new List<Expression>();
        foreach (var deriveBlock in program.Derives)
        {
            if (!scope.Derived.TryGetValue(deriveBlock.Declaration.Name, out var symbol))
            {
                continue;
            }

            if (deriveBlock.Rules.Count == 0)
            {
                diagnostics.Add(Error("CLARA014", deriveBlock.Declaration.Line, deriveBlock.Declaration.Column,
                    $"derive '{symbol.Name}'", $"Derived stage '{symbol.Name}' has no rules."));
                continue;
            }

            var blocks = CompileTable(deriveBlock.Rules, symbol, null);
            LintTable(deriveBlock.Rules, $"derive '{symbol.Name}'", exhaustivenessIsAnError: true);

            // Visible only after its own table is compiled, so a stage cannot reference itself or a later one.
            scope.VisibleDerived.Add(symbol.Name);

            Expression chain = Expression.Empty();
            for (var index = blocks.Count - 1; index >= 0; index--)
            {
                var (condition, body) = blocks[index];
                chain = condition is null ? body : Expression.Condition(condition, body, chain);
            }

            deriveChains.Add(chain);
        }

        var ruleInfos = new List<ClaraRuleInfo>();
        var ruleBlocks = CompileTable(program.Rules, null, ruleInfos);
        LintTable(program.Rules, "rules", exhaustivenessIsAnError: false);

        var requireExpressions = CompileChecks(program.Requires, scope, allowOutputReads: false, "requires", diagnostics);
        var invariantExpressions = CompileChecks(program.Invariants, scope, allowOutputReads: true, "invariants", diagnostics);

        if (program.Invariants.Count == 0 && scope.Outputs.Count > 0)
        {
            diagnostics.Add(Warning("CLARA108", program.PolicyLine == 0 ? 1 : program.PolicyLine, 1, "invariants",
                "The policy declares no invariants. Invariants are how a system-wide guarantee survives someone editing a single rule — state at least the bounds every result must respect."));
        }

        if (string.IsNullOrWhiteSpace(program.PolicyDescription))
        {
            diagnostics.Add(Warning("CLARA106", program.PolicyLine == 0 ? 1 : program.PolicyLine, 1, "policy",
                "The policy has no description. One quoted line under the policy name tells a reader with no context what this decides and for whom."));
        }

        foreach (var input in scope.Inputs.Values.OrderBy(symbol => symbol.Slot))
        {
            if (!scope.UsedInputs.Contains(input.Name))
            {
                diagnostics.Add(Warning("CLARA102", input.Line, 1, "inputs", $"Input '{input.Name}' is declared but never used."));
            }
        }

        var examples = BindExamples(program, scope, diagnostics);

        if (diagnostics.Any(diagnostic => diagnostic.Severity == ClaraSeverity.Error))
        {
            return null;
        }

        Expression dispatch = Expression.Constant(-1);
        for (var index = ruleBlocks.Count - 1; index >= 0; index--)
        {
            var (condition, body) = ruleBlocks[index];
            dispatch = condition is null ? body : Expression.Condition(condition, body, dispatch);
        }

        var full = deriveChains.Count == 0
            ? dispatch
            : Expression.Block(typeof(int), deriveChains.Append(dispatch));

        var execute = Expression.Lambda<Func<decimal[], int>>(full, frame).Compile();

        return new ClaraPolicy(
            program.PolicyName!,
            program.PolicyDescription,
            Export(scope.Inputs),
            Export(scope.Derived),
            Export(scope.Outputs),
            ruleInfos,
            ToChecks(program.Requires),
            ToChecks(program.Invariants),
            examples,
            scope.Inputs.Count + scope.Derived.Count + scope.Outputs.Count,
            execute,
            BuildCheckDelegate(requireExpressions, frame),
            BuildCheckDelegate(invariantExpressions, frame));
    }

    private enum SymbolKind { Input, Derived, Output }

    private static List<ClaraSymbol> Export(Dictionary<string, Symbol> symbols) =>
        symbols.Values.OrderBy(symbol => symbol.Slot)
            .Select(symbol => new ClaraSymbol(symbol.Name, symbol.Type, symbol.Slot, symbol.Description))
            .ToList();

    private static List<ClaraCheck> ToChecks(List<RawCheck> checks) =>
        checks.Select(check => new ClaraCheck(check.Expression, check.Message ?? check.Expression, check.Line)).ToList();

    private static List<Expression> CompileChecks(
        List<RawCheck> checks,
        Scope scope,
        bool allowOutputReads,
        string label,
        List<ClaraDiagnostic> diagnostics)
    {
        var compiled = new List<Expression>();
        scope.AllowOutputReads = allowOutputReads;
        try
        {
            foreach (var check in checks)
            {
                if (string.IsNullOrWhiteSpace(check.Message))
                {
                    diagnostics.Add(Warning("CLARA109", check.Line, check.Column, label,
                        "This check has no message. The message is what tells a future reader why the constraint exists."));
                }

                var typed = CompileExpression(check.Expression, scope, check.Line, check.Column, label, diagnostics);
                if (typed is not { } value)
                {
                    compiled.Add(Expression.Constant(true));
                    continue;
                }

                if (value.Type != ClaraType.Boolean)
                {
                    diagnostics.Add(Error("CLARA016", check.Line, check.Column, label,
                        $"A {label} entry must be a boolean condition but this is {ClaraTypes.Name(value.Type)}."));
                    compiled.Add(Expression.Constant(true));
                    continue;
                }

                compiled.Add(value.Expression);
            }
        }
        finally
        {
            scope.AllowOutputReads = false;
        }

        return compiled;
    }

    // Returns the index of the first failing check, or -1.
    private static Func<decimal[], int>? BuildCheckDelegate(List<Expression> checks, ParameterExpression frame)
    {
        if (checks.Count == 0)
        {
            return null;
        }

        Expression chain = Expression.Constant(-1);
        for (var index = checks.Count - 1; index >= 0; index--)
        {
            chain = Expression.Condition(checks[index], chain, Expression.Constant(index));
        }

        return Expression.Lambda<Func<decimal[], int>>(chain, frame).Compile();
    }

    private static List<ClaraExampleSpec> BindExamples(RawProgram program, Scope scope, List<ClaraDiagnostic> diagnostics)
    {
        var specs = new List<ClaraExampleSpec>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        if (program.Examples.Count == 0 && program.Rules.Count > 0)
        {
            diagnostics.Add(Warning("CLARA104", program.PolicyLine == 0 ? 1 : program.PolicyLine, 1, "examples",
                "The policy declares no examples. Examples are the regression contract — add at least one per branch."));
        }

        foreach (var example in program.Examples)
        {
            var scopeName = $"example '{example.Description}'";
            if (example.Description.Length == 0)
            {
                diagnostics.Add(Error("CLARA013", example.Line, 1, "examples", "An example needs a quoted description."));
                continue;
            }

            if (seen.TryGetValue(example.Description, out var firstLine))
            {
                diagnostics.Add(Warning("CLARA105", example.Line, 1, scopeName, $"Duplicate example description (also on line {firstLine})."));
            }
            else
            {
                seen[example.Description] = example.Line;
            }

            var inputs = new List<ClaraBinding>();
            var expected = new List<ClaraBinding>();
            var boundInputs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var bind in example.Inputs)
            {
                if (!scope.Inputs.TryGetValue(bind.Name, out var symbol))
                {
                    diagnostics.Add(Error("CLARA013", bind.Line, bind.NameColumn, scopeName, $"'{bind.Name}' is not a declared input."));
                    continue;
                }

                if (!boundInputs.Add(bind.Name))
                {
                    diagnostics.Add(Error("CLARA013", bind.Line, bind.NameColumn, scopeName, $"Input '{bind.Name}' is given twice."));
                    continue;
                }

                var value = CompileConstant(bind.Expression, scope, bind.Line, bind.ExprColumn, scopeName, diagnostics);
                if (value is not { } typed)
                {
                    continue;
                }

                if (!Assignable(symbol.Type, typed.Type))
                {
                    diagnostics.Add(Error("CLARA007", bind.Line, bind.ExprColumn, scopeName,
                        $"Input '{symbol.Name}' is {ClaraTypes.Name(symbol.Type)} but the value is {ClaraTypes.Name(typed.Type)}. {ConversionHint(symbol.Type)}"));
                    continue;
                }

                inputs.Add(new ClaraBinding(symbol.Name, symbol.Type, symbol.Slot, typed.Number));
            }

            foreach (var symbol in scope.Inputs.Values.OrderBy(symbol => symbol.Slot))
            {
                if (!boundInputs.Contains(symbol.Name))
                {
                    diagnostics.Add(Error("CLARA013", example.Line, 1, scopeName, $"Missing a value for input '{symbol.Name}'."));
                }
            }

            foreach (var bind in example.Expects)
            {
                if (!scope.Outputs.TryGetValue(bind.Name, out var symbol))
                {
                    diagnostics.Add(Error("CLARA013", bind.Line, bind.NameColumn, scopeName, $"'{bind.Name}' is not a declared output."));
                    continue;
                }

                var value = CompileConstant(bind.Expression, scope, bind.Line, bind.ExprColumn, scopeName, diagnostics);
                if (value is not { } typed)
                {
                    continue;
                }

                if (!Assignable(symbol.Type, typed.Type))
                {
                    diagnostics.Add(Error("CLARA007", bind.Line, bind.ExprColumn, scopeName,
                        $"Output '{symbol.Name}' is {ClaraTypes.Name(symbol.Type)} but the expected value is {ClaraTypes.Name(typed.Type)}. {ConversionHint(symbol.Type)}"));
                    continue;
                }

                expected.Add(new ClaraBinding(symbol.Name, symbol.Type, symbol.Slot, typed.Number));
            }

            if (expected.Count == 0)
            {
                diagnostics.Add(Error("CLARA013", example.Line, 1, scopeName, "An example must state at least one 'expect' — an example without an expectation proves nothing."));
            }

            specs.Add(new ClaraExampleSpec(example.Description, inputs, expected, example.Line));
        }

        return specs;
    }

    // ---------------------------------------------------------------- expressions

    private readonly record struct Typed(Expression Expression, ClaraType Type, bool Inexact);

    private sealed class ClaraSyntaxException(string code, int column, string message) : Exception(message)
    {
        public string Code { get; } = code;
        public int Column { get; } = column;
    }

    private static Typed? CompileExpression(string text, Scope scope, int line, int column, string scopeName, List<ClaraDiagnostic> diagnostics)
    {
        try
        {
            var tokens = Tokenize(text);
            var position = 0;
            var result = ParseOr(tokens, ref position, scope);
            if (position != tokens.Count)
            {
                throw new ClaraSyntaxException("CLARA009", tokens[position].Column, $"Unexpected '{tokens[position].Text}' after a complete expression.");
            }

            return result;
        }
        catch (ClaraSyntaxException ex)
        {
            diagnostics.Add(Error(ex.Code, line, column + ex.Column, scopeName, ex.Message));
            return null;
        }
    }

    // Constants and example values are evaluated at compile time; they may not reference inputs.
    private static TypedValue? CompileConstant(string text, Scope scope, int line, int column, string scopeName, List<ClaraDiagnostic> diagnostics)
    {
        var compiled = CompileExpression(text, scope, line, column, scopeName, diagnostics);
        if (compiled is not { } typed)
        {
            return null;
        }

        if (typed.Expression is not ConstantExpression constant)
        {
            diagnostics.Add(Error("CLARA006", line, column, scopeName, "This value must be computable at compile time — it may only use literals and constants, not inputs."));
            return null;
        }

        var number = typed.Type == ClaraType.Boolean
            ? (bool)constant.Value! ? 1m : 0m
            : (decimal)constant.Value!;
        return new TypedValue(typed.Type, number);
    }

    private static Typed ParseOr(List<Token> tokens, ref int position, Scope scope)
    {
        var left = ParseAnd(tokens, ref position, scope);
        while (MatchKeyword(tokens, ref position, "or", out var column))
        {
            var right = ParseAnd(tokens, ref position, scope);
            RequireBoolean(left, column, "or");
            RequireBoolean(right, column, "or");
            left = new Typed(Expression.OrElse(left.Expression, right.Expression), ClaraType.Boolean, false);
        }

        return left;
    }

    private static Typed ParseAnd(List<Token> tokens, ref int position, Scope scope)
    {
        var left = ParseComparison(tokens, ref position, scope);
        while (MatchKeyword(tokens, ref position, "and", out var column))
        {
            var right = ParseComparison(tokens, ref position, scope);
            RequireBoolean(left, column, "and");
            RequireBoolean(right, column, "and");
            left = new Typed(Expression.AndAlso(left.Expression, right.Expression), ClaraType.Boolean, false);
        }

        return left;
    }

    private static Typed ParseComparison(List<Token> tokens, ref int position, Scope scope)
    {
        var left = ParseAdditive(tokens, ref position, scope);
        while (position < tokens.Count && tokens[position].Kind == TokenKind.Operator &&
               tokens[position].Text is "=" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = tokens[position];
            position++;
            var right = ParseAdditive(tokens, ref position, scope);

            if (UnitClass(left.Type) != UnitClass(right.Type))
            {
                throw new ClaraSyntaxException("CLARA007", op.Column,
                    $"Cannot compare {ClaraTypes.Name(left.Type)} with {ClaraTypes.Name(right.Type)} — comparisons must stay within one unit.");
            }

            if (left.Type == ClaraType.Boolean && op.Text is not ("=" or "!="))
            {
                throw new ClaraSyntaxException("CLARA007", op.Column, $"Boolean values support '=' and '!=' only, not '{op.Text}'.");
            }

            Expression comparison = op.Text switch
            {
                "=" => Expression.Equal(left.Expression, right.Expression),
                "!=" => Expression.NotEqual(left.Expression, right.Expression),
                "<" => Expression.LessThan(left.Expression, right.Expression),
                "<=" => Expression.LessThanOrEqual(left.Expression, right.Expression),
                ">" => Expression.GreaterThan(left.Expression, right.Expression),
                _ => Expression.GreaterThanOrEqual(left.Expression, right.Expression)
            };

            left = new Typed(comparison, ClaraType.Boolean, false);
        }

        return left;
    }

    private static Typed ParseAdditive(List<Token> tokens, ref int position, Scope scope)
    {
        var left = ParseMultiplicative(tokens, ref position, scope);
        while (position < tokens.Count && tokens[position].Kind == TokenKind.Operator && tokens[position].Text is "+" or "-")
        {
            var op = tokens[position];
            position++;
            var right = ParseMultiplicative(tokens, ref position, scope);
            var type = AdditiveType(left.Type, right.Type, op.Text, op.Column);
            var expression = op.Text == "+"
                ? Expression.Add(left.Expression, right.Expression)
                : Expression.Subtract(left.Expression, right.Expression);
            left = new Typed(Fold(expression), type, left.Inexact || right.Inexact);
        }

        return left;
    }

    private static Typed ParseMultiplicative(List<Token> tokens, ref int position, Scope scope)
    {
        var left = ParseUnary(tokens, ref position, scope);
        while (position < tokens.Count && tokens[position].Kind == TokenKind.Operator && tokens[position].Text is "*" or "/")
        {
            var op = tokens[position];
            position++;
            var right = ParseUnary(tokens, ref position, scope);

            if (op.Text == "/" && right.Expression is ConstantExpression { Value: decimal divisor } && divisor == 0m)
            {
                throw new ClaraSyntaxException("CLARA015", op.Column, "Division by zero.");
            }

            var type = op.Text == "*"
                ? MultiplyType(left.Type, right.Type, op.Column)
                : DivideType(left.Type, right.Type, op.Column);

            var inexact = left.Inexact || right.Inexact || op.Text == "/" ||
                (type == ClaraType.Money && (left.Type != ClaraType.Money || right.Type != ClaraType.Money));

            var expression = op.Text == "*"
                ? Expression.Multiply(left.Expression, right.Expression)
                : Expression.Divide(left.Expression, right.Expression);
            left = new Typed(Fold(expression), type, inexact && type is ClaraType.Money or ClaraType.Percent or ClaraType.Number);
        }

        return left;
    }

    private static Typed ParseUnary(List<Token> tokens, ref int position, Scope scope)
    {
        if (MatchKeyword(tokens, ref position, "not", out var notColumn))
        {
            var operand = ParseUnary(tokens, ref position, scope);
            RequireBoolean(operand, notColumn, "not");
            return new Typed(Expression.Not(operand.Expression), ClaraType.Boolean, false);
        }

        if (position < tokens.Count && tokens[position].Kind == TokenKind.Operator && tokens[position].Text == "-")
        {
            var column = tokens[position].Column;
            position++;
            var operand = ParseUnary(tokens, ref position, scope);
            if (operand.Type == ClaraType.Boolean)
            {
                throw new ClaraSyntaxException("CLARA007", column, "Cannot negate a boolean.");
            }

            return new Typed(Fold(Expression.Negate(operand.Expression)), operand.Type, operand.Inexact);
        }

        return ParsePrimary(tokens, ref position, scope);
    }

    private static Typed ParsePrimary(List<Token> tokens, ref int position, Scope scope)
    {
        if (position >= tokens.Count)
        {
            throw new ClaraSyntaxException("CLARA009", tokens.Count > 0 ? tokens[^1].Column : 0, "The expression ends unexpectedly.");
        }

        var token = tokens[position];

        if (token.Kind == TokenKind.Literal)
        {
            position++;
            return new Typed(Expression.Constant(token.Number), token.Type, false);
        }

        if (token.Kind == TokenKind.LParen)
        {
            position++;
            var inner = ParseOr(tokens, ref position, scope);
            Expect(tokens, ref position, TokenKind.RParen, ")", token.Column);
            return inner;
        }

        if (token.Kind == TokenKind.Identifier)
        {
            position++;

            if (position < tokens.Count && tokens[position].Kind == TokenKind.LParen)
            {
                return ParseCall(token, tokens, ref position, scope);
            }

            if (token.Text is "true" or "false")
            {
                return new Typed(Expression.Constant(token.Text == "true"), ClaraType.Boolean, false);
            }

            if (scope.Constants.TryGetValue(token.Text, out var constant))
            {
                return constant.Type == ClaraType.Boolean
                    ? new Typed(Expression.Constant(constant.Number != 0m), ClaraType.Boolean, false)
                    : new Typed(Expression.Constant(constant.Number), constant.Type, false);
            }

            if (scope.Inputs.TryGetValue(token.Text, out var input))
            {
                scope.UsedInputs.Add(input.Name);
                return new Typed(Load(scope, input), input.Type, false);
            }

            if (scope.Derived.TryGetValue(token.Text, out var derived))
            {
                if (!scope.VisibleDerived.Contains(token.Text))
                {
                    throw new ClaraSyntaxException("CLARA006", token.Column,
                        $"'{token.Text}' is a derived stage that is not available here — a stage may only use inputs, constants and stages defined above it.");
                }

                return new Typed(Load(scope, derived), derived.Type, false);
            }

            if (scope.Outputs.TryGetValue(token.Text, out var output))
            {
                if (!scope.AllowOutputReads)
                {
                    throw new ClaraSyntaxException("CLARA006", token.Column,
                        $"'{token.Text}' is an output and cannot be read here — rules compute outputs from inputs, constants and derived stages. Only invariants may read outputs.");
                }

                return new Typed(Load(scope, output), output.Type, false);
            }

            throw new ClaraSyntaxException("CLARA006", token.Column, $"Unknown name '{token.Text}'. Declare it as an input, a constant or a derived stage.");
        }

        throw new ClaraSyntaxException("CLARA009", token.Column, $"Unexpected '{token.Text}'.");
    }

    private static Typed ParseCall(Token name, List<Token> tokens, ref int position, Scope scope)
    {
        if (name.Text == "round")
        {
            return ParseRound(name, tokens, ref position, scope);
        }

        Expect(tokens, ref position, TokenKind.LParen, "(", name.Column);
        var arguments = new List<Typed>();
        if (!(position < tokens.Count && tokens[position].Kind == TokenKind.RParen))
        {
            arguments.Add(ParseOr(tokens, ref position, scope));
            while (position < tokens.Count && tokens[position].Kind == TokenKind.Comma)
            {
                position++;
                arguments.Add(ParseOr(tokens, ref position, scope));
            }
        }

        Expect(tokens, ref position, TokenKind.RParen, ")", name.Column);

        switch (name.Text)
        {
            case "min":
            case "max":
            {
                if (arguments.Count < 2)
                {
                    throw new ClaraSyntaxException("CLARA008", name.Column, $"{name.Text}(...) needs at least two arguments.");
                }

                var type = arguments[0].Type;
                foreach (var argument in arguments)
                {
                    if (argument.Type == ClaraType.Boolean)
                    {
                        throw new ClaraSyntaxException("CLARA007", name.Column, $"{name.Text}(...) does not accept booleans.");
                    }

                    if (UnitClass(argument.Type) != UnitClass(type))
                    {
                        throw new ClaraSyntaxException("CLARA007", name.Column,
                            $"{name.Text}(...) requires one unit but received {ClaraTypes.Name(type)} and {ClaraTypes.Name(argument.Type)}.");
                    }
                }

                var method = name.Text == "min" ? DecimalMin : DecimalMax;
                var result = arguments[0].Expression;
                for (var index = 1; index < arguments.Count; index++)
                {
                    result = Expression.Call(method, result, arguments[index].Expression);
                }

                var widened = arguments.Any(argument => argument.Type == ClaraType.Number) && UnitClass(type) == UnitKind.Numeric
                    ? ClaraType.Number
                    : type;
                return new Typed(result, widened, arguments.Any(argument => argument.Inexact));
            }

            case "abs":
                return BuildUnary(name, arguments, DecimalAbs, keepType: true);
            case "floor":
                return BuildUnary(name, arguments, DecimalFloor, keepType: false);
            case "ceil":
                return BuildUnary(name, arguments, DecimalCeiling, keepType: false);
            default:
                throw new ClaraSyntaxException("CLARA008", name.Column,
                    $"Unknown function '{name.Text}'. CLARA provides min, max, abs, floor, ceil and round.");
        }
    }

    private static Typed BuildUnary(Token name, List<Typed> arguments, System.Reflection.MethodInfo method, bool keepType)
    {
        if (arguments.Count != 1)
        {
            throw new ClaraSyntaxException("CLARA008", name.Column, $"{name.Text}(x) takes exactly one argument.");
        }

        var argument = arguments[0];
        if (argument.Type == ClaraType.Boolean)
        {
            throw new ClaraSyntaxException("CLARA007", name.Column, $"{name.Text}(x) does not accept booleans.");
        }

        // floor/ceil of a plain number yields a whole number; money and percent keep their unit.
        var type = keepType || argument.Type is ClaraType.Money or ClaraType.Percent
            ? argument.Type
            : ClaraType.Integer;
        var inexact = keepType && argument.Inexact;
        return new Typed(Fold(Expression.Call(method, argument.Expression)), type, inexact);
    }

    // round is parsed by hand because its precision must be a compile-time constant and its mode is a keyword.
    private static Typed ParseRound(Token name, List<Token> tokens, ref int position, Scope scope)
    {
        Expect(tokens, ref position, TokenKind.LParen, "(", name.Column);
        var value = ParseOr(tokens, ref position, scope);
        if (value.Type == ClaraType.Boolean)
        {
            throw new ClaraSyntaxException("CLARA007", name.Column, "round(...) does not accept booleans.");
        }

        Expect(tokens, ref position, TokenKind.Comma, ",", name.Column);
        var precision = ParseOr(tokens, ref position, scope);
        if (precision.Type != ClaraType.Integer || precision.Expression is not ConstantExpression { Value: decimal places })
        {
            throw new ClaraSyntaxException("CLARA008", name.Column,
                "The number of decimals must be a whole-number literal or constant, so the precision is auditable.");
        }

        if (places is < 0 or > 8)
        {
            throw new ClaraSyntaxException("CLARA008", name.Column, "Round to between 0 and 8 decimals.");
        }

        var mode = MidpointRounding.AwayFromZero;
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Comma)
        {
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier ||
                !RoundingModes.TryGetValue(tokens[position].Text, out mode))
            {
                throw new ClaraSyntaxException("CLARA008", position < tokens.Count ? tokens[position].Column : name.Column,
                    "The rounding mode must be the word half_up or half_even.");
            }

            position++;
        }

        Expect(tokens, ref position, TokenKind.RParen, ")", name.Column);
        var call = Expression.Call(DecimalRound, value.Expression, Expression.Constant((int)places), Expression.Constant(mode));
        return new Typed(Fold(call), value.Type, false);
    }

    // ---------------------------------------------------------------- type system

    private enum UnitKind { Money, Percent, Numeric, Boolean }

    private static UnitKind UnitClass(ClaraType type) => type switch
    {
        ClaraType.Money => UnitKind.Money,
        ClaraType.Percent => UnitKind.Percent,
        ClaraType.Boolean => UnitKind.Boolean,
        _ => UnitKind.Numeric
    };

    private static ClaraType AdditiveType(ClaraType left, ClaraType right, string op, int column)
    {
        var verb = op == "+" ? "add" : "subtract";
        if (left == ClaraType.Boolean || right == ClaraType.Boolean)
        {
            throw new ClaraSyntaxException("CLARA007", column, $"Cannot {verb} boolean values.");
        }

        if (UnitClass(left) != UnitClass(right))
        {
            throw new ClaraSyntaxException("CLARA007", column,
                $"Cannot {verb} {ClaraTypes.Name(left)} and {ClaraTypes.Name(right)} — both sides must be the same unit.");
        }

        return left switch
        {
            ClaraType.Money => ClaraType.Money,
            ClaraType.Percent => ClaraType.Percent,
            _ => left == ClaraType.Integer && right == ClaraType.Integer ? ClaraType.Integer : ClaraType.Number
        };
    }

    private static ClaraType MultiplyType(ClaraType left, ClaraType right, int column)
    {
        if (left == ClaraType.Boolean || right == ClaraType.Boolean)
        {
            throw new ClaraSyntaxException("CLARA007", column, "Cannot multiply boolean values.");
        }

        if (left == ClaraType.Money && right == ClaraType.Money)
        {
            throw new ClaraSyntaxException("CLARA007", column, "Cannot multiply money by money — the result would be a meaningless unit.");
        }

        if (left == ClaraType.Percent && right == ClaraType.Percent)
        {
            throw new ClaraSyntaxException("CLARA007", column, "Cannot multiply percent by percent — apply a rate to an amount, not to another rate.");
        }

        if (left == ClaraType.Money || right == ClaraType.Money)
        {
            return ClaraType.Money;
        }

        if (left == ClaraType.Percent || right == ClaraType.Percent)
        {
            return ClaraType.Percent;
        }

        return left == ClaraType.Integer && right == ClaraType.Integer ? ClaraType.Integer : ClaraType.Number;
    }

    private static ClaraType DivideType(ClaraType left, ClaraType right, int column)
    {
        if (left == ClaraType.Boolean || right == ClaraType.Boolean)
        {
            throw new ClaraSyntaxException("CLARA007", column, "Cannot divide boolean values.");
        }

        return (UnitClass(left), UnitClass(right)) switch
        {
            (UnitKind.Money, UnitKind.Numeric) => ClaraType.Money,
            (UnitKind.Money, UnitKind.Money) => ClaraType.Number,
            (UnitKind.Percent, UnitKind.Numeric) => ClaraType.Percent,
            (UnitKind.Percent, UnitKind.Percent) => ClaraType.Number,
            (UnitKind.Numeric, UnitKind.Numeric) => ClaraType.Number,
            _ => throw new ClaraSyntaxException("CLARA007", column,
                $"Cannot divide {ClaraTypes.Name(left)} by {ClaraTypes.Name(right)}.")
        };
    }

    // Only whole numbers widen to numbers; money and percent never convert implicitly.
    private static bool Assignable(ClaraType target, ClaraType value) =>
        target == value || (target == ClaraType.Number && value == ClaraType.Integer);

    private static string ConversionHint(ClaraType target) => target switch
    {
        ClaraType.Money => "Write money literals as $0.00.",
        ClaraType.Percent => "Write percent literals as 1.5%.",
        ClaraType.Integer => "Write whole numbers without a decimal point.",
        ClaraType.Boolean => "Use true or false.",
        _ => "Write a plain number such as 2.5."
    };

    private static void RequireBoolean(Typed value, int column, string op)
    {
        if (value.Type != ClaraType.Boolean)
        {
            throw new ClaraSyntaxException("CLARA007", column, $"'{op}' requires boolean operands but received {ClaraTypes.Name(value.Type)}.");
        }
    }

    // ---------------------------------------------------------------- codegen helpers

    private static readonly System.Reflection.MethodInfo DecimalRound =
        typeof(Math).GetMethod(nameof(Math.Round), [typeof(decimal), typeof(int), typeof(MidpointRounding)])!;

    private static readonly System.Reflection.MethodInfo DecimalMin =
        typeof(Math).GetMethod(nameof(Math.Min), [typeof(decimal), typeof(decimal)])!;

    private static readonly System.Reflection.MethodInfo DecimalMax =
        typeof(Math).GetMethod(nameof(Math.Max), [typeof(decimal), typeof(decimal)])!;

    private static readonly System.Reflection.MethodInfo DecimalAbs =
        typeof(Math).GetMethod(nameof(Math.Abs), [typeof(decimal)])!;

    private static readonly System.Reflection.MethodInfo DecimalFloor =
        typeof(Math).GetMethod(nameof(Math.Floor), [typeof(decimal)])!;

    private static readonly System.Reflection.MethodInfo DecimalCeiling =
        typeof(Math).GetMethod(nameof(Math.Ceiling), [typeof(decimal)])!;

    private static Expression Slot(Scope scope, Symbol symbol) =>
        Expression.ArrayAccess(scope.Frame, Expression.Constant(symbol.Slot));

    private static Expression Load(Scope scope, Symbol symbol)
    {
        var slot = Expression.ArrayIndex(scope.Frame, Expression.Constant(symbol.Slot));
        return symbol.Type == ClaraType.Boolean
            ? Expression.NotEqual(slot, Expression.Constant(0m))
            : slot;
    }

    private static Expression Store(Typed value) =>
        value.Type == ClaraType.Boolean
            ? Expression.Condition(value.Expression, Expression.Constant(1m), Expression.Constant(0m))
            : value.Expression;

    // Constant-folds compile-time arithmetic so rounding precision and literal maths cost nothing at runtime.
    private static Expression Fold(Expression expression)
    {
        if (expression is BinaryExpression { Left: ConstantExpression, Right: ConstantExpression } ||
            expression is UnaryExpression { Operand: ConstantExpression } ||
            (expression is MethodCallExpression call && call.Arguments.All(argument => argument is ConstantExpression)))
        {
            try
            {
                var value = Expression.Lambda(expression).Compile().DynamicInvoke();
                return Expression.Constant(value, expression.Type);
            }
            catch (Exception)
            {
                return expression;
            }
        }

        return expression;
    }

    // ---------------------------------------------------------------- lexer

    private enum TokenKind { Literal, Identifier, Operator, LParen, RParen, Comma }

    private readonly record struct Token(TokenKind Kind, string Text, int Column, ClaraType Type = ClaraType.Number, decimal Number = 0m);

    private static bool MatchKeyword(List<Token> tokens, ref int position, string keyword, out int column)
    {
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier &&
            tokens[position].Text.Equals(keyword, StringComparison.Ordinal))
        {
            column = tokens[position].Column;
            position++;
            return true;
        }

        column = position < tokens.Count ? tokens[position].Column : 0;
        return false;
    }

    private static void Expect(List<Token> tokens, ref int position, TokenKind kind, string what, int fallbackColumn)
    {
        if (position >= tokens.Count || tokens[position].Kind != kind)
        {
            var column = position < tokens.Count ? tokens[position].Column : fallbackColumn;
            throw new ClaraSyntaxException("CLARA009", column, $"Expected '{what}'.");
        }

        position++;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < text.Length)
        {
            var start = index;
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '$')
            {
                index++;
                var negative = index < text.Length && text[index] == '-';
                if (negative)
                {
                    index++;
                }

                var digitsStart = index;
                while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '.' or ','))
                {
                    index++;
                }

                var raw = text[digitsStart..index].Replace(",", "");
                if (raw.Length == 0 || !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var money))
                {
                    throw new ClaraSyntaxException("CLARA009", start, $"Invalid money literal '{text[start..index]}'.");
                }

                tokens.Add(new Token(TokenKind.Literal, text[start..index], start, ClaraType.Money, negative ? -money : money));
                continue;
            }

            if (char.IsDigit(current))
            {
                var hasDot = false;
                while (index < text.Length && (char.IsDigit(text[index]) || (text[index] == '.' && !hasDot)))
                {
                    hasDot |= text[index] == '.';
                    index++;
                }

                var raw = text[start..index];
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    throw new ClaraSyntaxException("CLARA009", start, $"Invalid number literal '{raw}'.");
                }

                if (index < text.Length && text[index] == '%')
                {
                    index++;
                    tokens.Add(new Token(TokenKind.Literal, raw + "%", start, ClaraType.Percent, number / 100m));
                    continue;
                }

                tokens.Add(new Token(TokenKind.Literal, raw, start, hasDot ? ClaraType.Number : ClaraType.Integer, number));
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..index], start));
                continue;
            }

            switch (current)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "(", start));
                    index++;
                    break;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")", start));
                    index++;
                    break;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", start));
                    index++;
                    break;
                case '!':
                    if (index + 1 < text.Length && text[index + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Operator, "!=", start));
                        index += 2;
                        break;
                    }

                    throw new ClaraSyntaxException("CLARA009", start, "Use '!=' for 'is not equal to'.");
                case '<':
                case '>':
                    if (index + 1 < text.Length && text[index + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Operator, text[index..(index + 2)], start));
                        index += 2;
                        break;
                    }

                    tokens.Add(new Token(TokenKind.Operator, current.ToString(), start));
                    index++;
                    break;
                case '=':
                    if (index + 1 < text.Length && text[index + 1] == '=')
                    {
                        throw new ClaraSyntaxException("CLARA009", start, "CLARA uses a single '=' for comparison.");
                    }

                    tokens.Add(new Token(TokenKind.Operator, "=", start));
                    index++;
                    break;
                case '+':
                case '-':
                case '*':
                case '/':
                    tokens.Add(new Token(TokenKind.Operator, current.ToString(), start));
                    index++;
                    break;
                default:
                    throw new ClaraSyntaxException("CLARA009", start, $"Unexpected character '{current}'.");
            }
        }

        return tokens;
    }

    private static readonly Dictionary<string, MidpointRounding> RoundingModes = new(StringComparer.Ordinal)
    {
        ["half_up"] = MidpointRounding.AwayFromZero,
        ["half_even"] = MidpointRounding.ToEven
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "policy", "inputs", "constants", "output", "outputs", "rule", "rules", "when", "then", "otherwise",
        "because", "derive", "requires", "invariants", "examples", "example", "expect", "and", "or", "not",
        "true", "false", "min", "max", "abs", "floor", "ceil", "round", "half_up", "half_even",
        "money", "percent", "number", "integer", "boolean"
    };

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') &&
        text.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static ClaraDiagnostic Error(string code, int line, int column, string scope, string message) =>
        new(ClaraSeverity.Error, code, line, Math.Max(column, 1), scope, message);

    private static ClaraDiagnostic Warning(string code, int line, int column, string scope, string message) =>
        new(ClaraSeverity.Warning, code, line, Math.Max(column, 1), scope, message);
}

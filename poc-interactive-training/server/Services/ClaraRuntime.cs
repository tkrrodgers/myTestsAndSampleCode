using System.Globalization;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

public enum ClaraType { Money, Percent, Number, Integer, Boolean }

public static class ClaraTypes
{
    public static bool TryParse(string text, out ClaraType type)
    {
        switch (text.Trim())
        {
            case "money": type = ClaraType.Money; return true;
            case "percent": type = ClaraType.Percent; return true;
            case "number": type = ClaraType.Number; return true;
            case "integer": type = ClaraType.Integer; return true;
            case "boolean": type = ClaraType.Boolean; return true;
            default: type = ClaraType.Number; return false;
        }
    }

    public static string Name(ClaraType type) => type switch
    {
        ClaraType.Money => "money",
        ClaraType.Percent => "percent",
        ClaraType.Integer => "integer",
        ClaraType.Boolean => "boolean",
        _ => "number"
    };

    public static string Format(ClaraType type, decimal value) => type switch
    {
        ClaraType.Money => (value < 0 ? "-$" : "$") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture),
        ClaraType.Percent => (value * 100m).ToString("0.####", CultureInfo.InvariantCulture) + "%",
        ClaraType.Integer => value.ToString("0", CultureInfo.InvariantCulture),
        ClaraType.Boolean => value != 0m ? "true" : "false",
        _ => value.ToString("0.####", CultureInfo.InvariantCulture)
    };
}

public sealed record ClaraSymbol(string Name, ClaraType Type, int Slot, string? Description);

public sealed record ClaraRuleInfo(string Name, bool IsOtherwise, int Line, string? Because, string? Owner);

// A named precondition or postcondition. The message carries the local knowledge; the expression makes it testable.
public sealed record ClaraCheck(string Expression, string Message, int Line);

public sealed record ClaraBinding(string Name, ClaraType Type, int Slot, decimal Value);

public sealed record ClaraExampleSpec(
    string Description,
    IReadOnlyList<ClaraBinding> Inputs,
    IReadOnlyList<ClaraBinding> Expected,
    int Line);

public sealed record ClaraEvaluation(string? MatchedRule, IReadOnlyDictionary<string, decimal> Outputs);

public sealed record ClaraExecution(ClaraRuleInfo? MatchedRule, ClaraCheck? FailedRequirement, ClaraCheck? FailedInvariant);

public sealed record ClaraCompilation(
    bool Success,
    ClaraPolicy? Policy,
    IReadOnlyList<ClaraDiagnostic> Diagnostics,
    double CompileMilliseconds);

// A compiled, immutable policy. The generated delegate is pure and reentrant: callers own the frame,
// so a single policy instance can be executed concurrently without locking.
public sealed class ClaraPolicy
{
    private readonly Func<decimal[], int> _execute;
    private readonly Func<decimal[], int>? _checkRequires;
    private readonly Func<decimal[], int>? _checkInvariants;

    internal ClaraPolicy(
        string name,
        string? description,
        IReadOnlyList<ClaraSymbol> inputs,
        IReadOnlyList<ClaraSymbol> derived,
        IReadOnlyList<ClaraSymbol> outputs,
        IReadOnlyList<ClaraRuleInfo> rules,
        IReadOnlyList<ClaraCheck> requires,
        IReadOnlyList<ClaraCheck> invariants,
        IReadOnlyList<ClaraExampleSpec> examples,
        int frameSize,
        Func<decimal[], int> execute,
        Func<decimal[], int>? checkRequires,
        Func<decimal[], int>? checkInvariants)
    {
        Name = name;
        Description = description;
        Inputs = inputs;
        Derived = derived;
        Outputs = outputs;
        Rules = rules;
        Requires = requires;
        Invariants = invariants;
        Examples = examples;
        FrameSize = frameSize;
        _execute = execute;
        _checkRequires = checkRequires;
        _checkInvariants = checkInvariants;
    }

    public string Name { get; }

    public string? Description { get; }

    public IReadOnlyList<ClaraSymbol> Inputs { get; }

    // Named intermediate stages: part of the vocabulary and the audit trail, not of the contract.
    public IReadOnlyList<ClaraSymbol> Derived { get; }

    public IReadOnlyList<ClaraSymbol> Outputs { get; }

    public IReadOnlyList<ClaraRuleInfo> Rules { get; }

    public IReadOnlyList<ClaraCheck> Requires { get; }

    public IReadOnlyList<ClaraCheck> Invariants { get; }

    public IReadOnlyList<ClaraExampleSpec> Examples { get; }

    public int FrameSize { get; }

    public decimal[] CreateFrame() => new decimal[FrameSize];

    // Hot path. Preconditions and invariants are deliberately not evaluated here, so the cost of assurance
    // is paid at the boundary where it is wanted rather than on every call.
    // Returns the index of the rule that fired, or -1 when no rule matched.
    public int Execute(decimal[] frame) => _execute(frame);

    // Index of the first violated precondition, or -1.
    public int CheckRequires(decimal[] frame) => _checkRequires?.Invoke(frame) ?? -1;

    // Index of the first violated invariant, or -1. Meaningful only after Execute.
    public int CheckInvariants(decimal[] frame) => _checkInvariants?.Invoke(frame) ?? -1;

    public ClaraExecution ExecuteChecked(decimal[] frame)
    {
        var failedRequirement = CheckRequires(frame);
        if (failedRequirement >= 0)
        {
            return new ClaraExecution(null, Requires[failedRequirement], null);
        }

        var index = _execute(frame);
        var matched = index >= 0 ? Rules[index] : null;
        var failedInvariant = CheckInvariants(frame);
        return new ClaraExecution(matched, null, failedInvariant >= 0 ? Invariants[failedInvariant] : null);
    }

    public ClaraEvaluation Evaluate(IReadOnlyDictionary<string, decimal> inputs)
    {
        var frame = CreateFrame();
        foreach (var symbol in Inputs)
        {
            if (!inputs.TryGetValue(symbol.Name, out var value))
            {
                throw new ArgumentException($"Policy '{Name}' requires input '{symbol.Name}'.", nameof(inputs));
            }

            frame[symbol.Slot] = value;
        }

        var index = _execute(frame);
        var outputs = new Dictionary<string, decimal>(Outputs.Count, StringComparer.Ordinal);
        foreach (var symbol in Outputs)
        {
            outputs[symbol.Name] = frame[symbol.Slot];
        }

        return new ClaraEvaluation(index >= 0 ? Rules[index].Name : null, outputs);
    }

    internal ClaraExampleResult RunExample(ClaraExampleSpec example)
    {
        var frame = CreateFrame();
        foreach (var binding in example.Inputs)
        {
            frame[binding.Slot] = binding.Value;
        }

        string? note = null;
        string? matchedRule = null;
        var faulted = false;

        try
        {
            var failedRequirement = CheckRequires(frame);
            if (failedRequirement >= 0)
            {
                faulted = true;
                note = $"precondition violated: {Requires[failedRequirement].Message}";
            }
            else
            {
                var index = _execute(frame);
                if (index >= 0)
                {
                    matchedRule = Rules[index].Name;
                }
                else
                {
                    faulted = true;
                    note = "No rule matched — add an 'otherwise' rule.";
                }

                var failedInvariant = CheckInvariants(frame);
                if (failedInvariant >= 0)
                {
                    faulted = true;
                    note = $"invariant violated: {Invariants[failedInvariant].Message}";
                }
            }
        }
        catch (DivideByZeroException)
        {
            faulted = true;
            note = "Division by zero while evaluating this example.";
        }
        catch (OverflowException)
        {
            faulted = true;
            note = "Arithmetic overflow while evaluating this example.";
        }

        var expected = example.Expected.ToDictionary(binding => binding.Slot, binding => binding);
        var outputs = new List<string>(Outputs.Count);
        bool? passed = example.Expected.Count == 0 ? null : !faulted;

        foreach (var symbol in Outputs)
        {
            var actual = frame[symbol.Slot];
            outputs.Add($"{symbol.Name} = {ClaraTypes.Format(symbol.Type, actual)}");
            if (!expected.TryGetValue(symbol.Slot, out var expectation))
            {
                continue;
            }

            if (expectation.Value != actual)
            {
                passed = false;
                note ??= $"expected {symbol.Name} = {ClaraTypes.Format(symbol.Type, expectation.Value)}, got {ClaraTypes.Format(symbol.Type, actual)}";
            }
        }

        return new ClaraExampleResult(
            example.Description,
            example.Inputs.Select(binding => $"{binding.Name} = {ClaraTypes.Format(binding.Type, binding.Value)}").ToList(),
            outputs,
            Derived.Select(symbol => $"{symbol.Name} = {ClaraTypes.Format(symbol.Type, frame[symbol.Slot])}").ToList(),
            passed,
            note,
            matchedRule);
    }
}

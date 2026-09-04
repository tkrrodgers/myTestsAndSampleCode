using System.Diagnostics;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Executable conformance suite for the CLARA compiler. Each case pins one guarantee — a type rule, a lint,
// a diagnostic, or a runtime semantic — so the language's claims can be demonstrated rather than asserted.
public static class ClaraConformance
{
    private sealed record Case(
        string Name,
        string Source,
        bool ShouldCompile,
        string[] ExpectedCodes,
        int ExpectedPassingExamples = 0);

    public static ClaraConformanceReport Run()
    {
        var clock = Stopwatch.StartNew();
        var results = new List<ClaraConformanceCase>();

        foreach (var testCase in Cases)
        {
            var result = ClaraCompiler.Run(testCase.Source);
            var codes = result.Diagnostics.Select(diagnostic => diagnostic.Code).ToHashSet(StringComparer.Ordinal);
            var problems = new List<string>();

            if (result.Compiled != testCase.ShouldCompile)
            {
                problems.Add(testCase.ShouldCompile
                    ? $"expected a clean compile but got: {string.Join("; ", result.Errors)}"
                    : "expected a compile error but the policy compiled");
            }

            foreach (var expected in testCase.ExpectedCodes)
            {
                if (!codes.Contains(expected))
                {
                    problems.Add($"expected diagnostic {expected}, saw [{string.Join(", ", codes)}]");
                }
            }

            var passing = result.Examples.Count(example => example.Passed == true);
            if (passing != testCase.ExpectedPassingExamples)
            {
                problems.Add($"expected {testCase.ExpectedPassingExamples} passing examples, got {passing}");
            }

            results.Add(new ClaraConformanceCase(
                testCase.Name,
                problems.Count == 0,
                problems.Count == 0 ? "ok" : string.Join(" | ", problems)));
        }

        clock.Stop();
        return new ClaraConformanceReport(
            results.Count,
            results.Count(result => result.Passed),
            results,
            clock.Elapsed.TotalMilliseconds);
    }

    private const string MoneyPassThrough = """
        policy PassThrough
        inputs:
          amount: money
        rule Always:
          otherwise
          then result = amount
        output:
          result: money
        examples:
          example "identity":
            amount = $12.34
            expect result = $12.34
        """;

    private static readonly Case[] Cases =
    [
        new("reference late-fee policy compiles and self-verifies",
            RoundTripSamples.ClaraSample, true, [], 5),

        new("money pass-through is clean", MoneyPassThrough, true, [], 1),

        new("money cannot be added to a bare number", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount + 5
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $6.00
            """, false, ["CLARA007"]),

        new("money cannot be multiplied by money", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount * amount
            output:
              result: money
            examples:
              example "x":
                amount = $2.00
                expect result = $4.00
            """, false, ["CLARA007"]),

        new("a rate cannot be multiplied by a rate", """
            policy T
            inputs:
              rate: percent
            rule Always:
              otherwise
              then result = rate * rate
            output:
              result: percent
            examples:
              example "x":
                rate = 10%
                expect result = 1%
            """, false, ["CLARA007"]),

        new("money cannot be compared with a whole number", """
            policy T
            inputs:
              amount: money
            rule Positive:
              when amount > 0
              then result = amount
            rule Always:
              otherwise
              then result = $0.00
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA007"]),

        new("unknown names are rejected", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount + surcharge
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA006"]),

        new("outputs are write-only inside rules", """
            policy T
            inputs:
              amount: money
            rule Always:
              when result > $0.00
              then result = amount
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA006"]),

        new("inputs cannot be assigned", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then amount = $1.00
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA010", "CLARA012"]),

        new("every rule must assign every output", """
            policy T
            inputs:
              amount: money
            rule Small:
              when amount < $10.00
              then result = $0.00
            rule Always:
              otherwise
              then other = $1.00
            output:
              result: money
              other: money
            examples:
              example "x":
                amount = $1.00
                expect result = $0.00
                expect other = $1.00
            """, false, ["CLARA012"]),

        new("rules after otherwise are flagged unreachable", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount
            rule Never:
              when amount > $0.00
              then result = $0.00
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, true, ["CLARA100"], 1),

        new("a table without otherwise is flagged non-exhaustive", """
            policy T
            inputs:
              amount: money
            rule Positive:
              when amount > $0.00
              then result = amount
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, true, ["CLARA101"], 1),

        new("unused inputs are reported", """
            policy T
            inputs:
              amount: money
              ignored: integer
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                ignored = 4
                expect result = $1.00
            """, true, ["CLARA102"], 1),

        new("unrounded money results are flagged", """
            policy T
            inputs:
              amount: money
            constants:
              rate: percent = 10%
            rule Always:
              otherwise
              then result = amount * rate
            output:
              result: money
            examples:
              example "x":
                amount = $10.00
                expect result = $1.00
            """, true, ["CLARA103"], 1),

        new("an explicit round clears the money warning", """
            policy T
            inputs:
              amount: money
            constants:
              rate: percent = 10%
            rule Always:
              otherwise
              then result = round(amount * rate, 2)
            output:
              result: money
            examples:
              example "x":
                amount = $10.00
                expect result = $1.00
            """, true, [], 1),

        new("examples must supply every input", """
            policy T
            inputs:
              amount: money
              days: integer
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA013"]),

        new("example values are type checked", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "x":
                amount = 1
                expect result = $1.00
            """, false, ["CLARA007"]),

        new("duplicate declarations are rejected", """
            policy T
            inputs:
              amount: money
              amount: money
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA002"]),

        new("literal division by zero is rejected", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount / 0
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA015"]),

        new("rounding precision must be auditable", """
            policy T
            inputs:
              amount: money
              places: integer
            rule Always:
              otherwise
              then result = round(amount, places)
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                places = 2
                expect result = $1.00
            """, false, ["CLARA008"]),

        new("unknown functions are rejected", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = sqrt(amount)
            output:
              result: money
            examples:
              example "x":
                amount = $1.00
                expect result = $1.00
            """, false, ["CLARA008"]),

        new("reserved words cannot be declared", """
            policy T
            inputs:
              round: money
            rule Always:
              otherwise
              then result = $1.00
            output:
              result: money
            examples:
              example "x":
                round = $1.00
                expect result = $1.00
            """, false, ["CLARA005"]),

        new("half-up rounding is the default", """
            policy T
            inputs:
              value: number
            rule Always:
              otherwise
              then result = round(value, 0)
            output:
              result: number
            examples:
              example "midpoint":
                value = 2.5
                expect result = 3
            """, true, [], 1),

        new("banker's rounding is available explicitly", """
            policy T
            inputs:
              value: number
            rule Always:
              otherwise
              then result = round(value, 0, half_even)
            output:
              result: number
            examples:
              example "midpoint":
                value = 2.5
                expect result = 2
            """, true, [], 1),

        new("booleans flow through conditions and outputs", """
            policy T
            inputs:
              flagged: boolean
            rule Yes:
              when flagged
              then result = false
            rule No:
              otherwise
              then result = true
            output:
              result: boolean
            examples:
              example "on":
                flagged = true
                expect result = false
              example "off":
                flagged = false
                expect result = true
            """, true, [], 2),

        new("a wrong expectation fails rather than passing quietly", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "wrong":
                amount = $1.00
                expect result = $2.00
            """, true, [], 0),

        new("a hash inside a quoted description is not a comment", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "promo # 4 applies":
                amount = $1.00
                expect result = $1.00
            """, true, [], 1),

        new("a policy without examples is flagged", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            """, true, ["CLARA104"]),

        new("an example without an expectation is rejected", """
            policy T
            inputs:
              amount: money
            rule Always:
              otherwise
              then result = amount
            output:
              result: money
            examples:
              example "no expectation":
                amount = $1.00
            """, false, ["CLARA013"]),

        new("money divided by money yields a plain ratio", """
            policy T
            inputs:
              paid: money
              total: money
            rule Always:
              otherwise
              then share = paid / total
            output:
              share: number
            examples:
              example "half":
                paid = $50.00
                total = $100.00
                expect share = 0.5
            """, true, [], 1),

        // --- knowledge-carrying constructs ---

        new("the language-test reference policy is clean and self-verifying",
            LanguageTestSamples.ClaraSource, true, [], 8),

        new("undocumented symbols and unsourced rules are reported", MoneyPassThrough, true,
            ["CLARA106", "CLARA107", "CLARA108"], 1),

        new("derived stages compile and feed later stages", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            derive doubled: money "twice the amount"
              rule Always:
                otherwise
                then doubled = amount * 2
                because "spec"
            derive quadrupled: money "twice the doubled amount"
              rule Always2:
                otherwise
                then quadrupled = doubled * 2
                because "spec"
            rules:
              rule Emit:
                otherwise
                then result = quadrupled
                because "spec"
            output:
              result: money "doc"
            invariants:
              result >= $0.00 "no credits"
            examples:
              example "four times":
                amount = $2.00
                expect result = $8.00
            """, true, [], 1),

        new("a derived stage must be exhaustive", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            derive doubled: money "twice the amount"
              rule OnlyPositive:
                when amount > $0.00
                then doubled = amount * 2
                because "spec"
            rules:
              rule Emit:
                otherwise
                then result = doubled
                because "spec"
            output:
              result: money "doc"
            invariants:
              result >= $0.00 "no credits"
            examples:
              example "x":
                amount = $2.00
                expect result = $4.00
            """, false, ["CLARA017"]),

        new("a derive block may only assign its own stage", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            derive doubled: money "twice the amount"
              rule Always:
                otherwise
                then result = amount
                because "spec"
            rules:
              rule Emit:
                otherwise
                then result = doubled
                because "spec"
            output:
              result: money "doc"
            examples:
              example "x":
                amount = $2.00
                expect result = $4.00
            """, false, ["CLARA018"]),

        new("a stage cannot reference a stage defined below it", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            derive first: money "uses a later stage"
              rule Always:
                otherwise
                then first = second
                because "spec"
            derive second: money "doc"
              rule Always2:
                otherwise
                then second = amount
                because "spec"
            rules:
              rule Emit:
                otherwise
                then result = first
                because "spec"
            output:
              result: money "doc"
            examples:
              example "x":
                amount = $2.00
                expect result = $2.00
            """, false, ["CLARA006"]),

        new("a violated invariant fails the example even though the rule fired", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            rule Always:
              otherwise
              then result = amount
              because "spec"
            output:
              result: money "doc"
            invariants:
              result >= $0.00 "a charge is never a credit"
            examples:
              example "negative input trips the guarantee":
                amount = $-5.00
                expect result = $-5.00
            """, true, [], 0),

        new("a violated precondition fails the example", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            requires:
              amount >= $0.00 "the caller must never pass a negative amount"
            rule Always:
              otherwise
              then result = amount
              because "spec"
            output:
              result: money "doc"
            invariants:
              result >= $0.00 "no credits"
            examples:
              example "bad caller":
                amount = $-5.00
                expect result = $0.00
            """, true, [], 0),

        new("invariants may read outputs but rules may not", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            rule Always:
              otherwise
              then result = amount
              because "spec"
            output:
              result: money "doc"
            invariants:
              result = amount "the result is the amount unchanged"
            examples:
              example "x":
                amount = $2.00
                expect result = $2.00
            """, true, [], 1),

        new("a check must be a boolean condition", """
            policy T
              "doc"
            inputs:
              amount: money "doc"
            rule Always:
              otherwise
              then result = amount
              because "spec"
            output:
              result: money "doc"
            invariants:
              result + $1.00 "not a condition"
            examples:
              example "x":
                amount = $2.00
                expect result = $2.00
            """, false, ["CLARA016"])
    ];
}

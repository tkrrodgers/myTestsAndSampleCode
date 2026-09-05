using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Reference migrations used only by the headless harness gate. One is correct; the other makes the
// single most common mistake - using ordinary rounding where COBOL truncates, because the COMPUTE has
// no ROUNDED clause. If the grader ever scores these the same, the grader is broken.
public static class MigrationReference
{
    public const string Correct = """
        public static class InterestCalculator
        {
            public static decimal Calculate(string record)
            {
                bool negative = record.Substring(0, 1) == "-";
                decimal balance = decimal.Parse(record.Substring(1, 11)) / 100m;
                decimal rate = decimal.Parse(record.Substring(12, 6)) / 100m;
                if (negative)
                {
                    balance = -balance;
                }

                decimal exact = balance * rate / 1200m;

                // No ROUNDED on the COBOL COMPUTE, so the result truncates toward zero at 2 decimals.
                return decimal.Truncate(exact * 100m) / 100m;
            }
        }
        """;

    public const string Naive = """
        public static class InterestCalculator
        {
            public static decimal Calculate(string record)
            {
                bool negative = record.Substring(0, 1) == "-";
                decimal balance = decimal.Parse(record.Substring(1, 11)) / 100m;
                decimal rate = decimal.Parse(record.Substring(12, 6)) / 100m;
                if (negative)
                {
                    balance = -balance;
                }

                return System.Math.Round(balance * rate / 1200m, 2);
            }
        }
        """;

    public static bool Matches(IReadOnlyList<CobolOracleRun> oracle, MigrationOutput output)
    {
        var truth = oracle.FirstOrDefault(run => run.Input == output.Input)?.Output;
        if (string.IsNullOrWhiteSpace(truth) || output.Error is not null)
        {
            return false;
        }

        var text = truth.Trim();
        var negative = text.EndsWith('-');
        text = text.TrimEnd('+', '-');
        if (!decimal.TryParse(text, out var raw) || !decimal.TryParse(output.Value, out var actual))
        {
            return false;
        }

        var expected = (negative ? -raw : raw) / 100m;
        return expected == actual;
    }
}

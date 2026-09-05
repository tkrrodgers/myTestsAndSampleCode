using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Reference migrations used only by the headless harness gate. One is correct; the other is the
// plausible mistake a reader makes when the implied decimal point in PIC 9(3)V9 is not obvious.
// If the grader ever scores these the same, the grader is broken.
public static class MigrationReference
{
    public const string Correct = """
        public static class LateReturnBilling
        {
            public static decimal Calculate(string record)
            {
                int grade = int.Parse(record.Substring(5, 1));
                decimal hours = decimal.Parse(record.Substring(6, 4)) / 10m;
                decimal rate = decimal.Parse(record.Substring(10, 6)) / 100m;

                decimal gross = System.Math.Round(hours * rate, 2, System.MidpointRounding.AwayFromZero);
                decimal bonus = grade >= 7 && grade <= 9
                    ? System.Math.Round(gross * 0.075m, 2, System.MidpointRounding.AwayFromZero)
                    : 0m;

                return decimal.Truncate((gross + bonus) * 100m) / 100m;
            }
        }
        """;

    public const string Naive = """
        public static class LateReturnBilling
        {
            public static decimal Calculate(string record)
            {
                int grade = int.Parse(record.Substring(5, 1));
                decimal hours = decimal.Parse(record.Substring(6, 4));
                decimal rate = decimal.Parse(record.Substring(10, 6));

                decimal gross = hours * rate;
                decimal bonus = grade > 7 ? gross * 0.075m : 0m;
                return gross + bonus;
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

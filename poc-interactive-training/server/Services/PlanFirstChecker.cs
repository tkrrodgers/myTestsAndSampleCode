using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Competency 5 — plan-first with an executive summary and a diagram, treated as a graded skill rather
// than a nicety. The structural check is deterministic so "it produced a plan" cannot be asserted; the
// hardest gate is the last one, because jumping to code is the failure this competency exists to stop.
public static partial class PlanFirstChecker
{
    public static PlanReview Review(string response)
    {
        var text = response ?? string.Empty;
        var checks = new List<PlanCheck>();

        void Check(string name, bool passed, string requirement) =>
            checks.Add(new PlanCheck(name, passed, requirement));

        var summary = SummaryRegex().Match(text);
        var summaryWords = summary.Success
            ? summary.Groups["body"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;

        Check("Executive summary present", summary.Success,
            "A section headed 'Executive summary'");
        Check("Summary is readable by a non-engineer", summaryWords is >= 20 and <= 200,
            "Between 20 and 200 words — long enough to be useful, short enough to be read");
        Check("Diagram present", MermaidRegex().IsMatch(text),
            "A mermaid diagram block");
        Check("Risks listed", RiskRegex().IsMatch(text),
            "A risks or trade-offs section");
        Check("Verification stated", VerifyRegex().IsMatch(text),
            "How the change will be verified");
        Check("No code yet", !CodeFenceRegex().IsMatch(text),
            "No implementation code — the plan is reviewed before code exists");

        var passed = checks.Count(check => check.Passed);
        var score = (int)Math.Round(100.0 * passed / checks.Count);

        var verdict = checks.All(check => check.Passed)
            ? "Plan accepted. Leadership can read the summary, engineers can read the diagram, and nothing has been built yet."
            : checks.First(check => !check.Passed).Name == "No code yet"
                ? "Rejected: it jumped to code. Producing an implementation before the plan is reviewed is exactly the habit this gate exists to break."
                : "Rejected: the plan is missing a required part. Send it back before any code is written.";

        return new PlanReview(score, checks, verdict, summaryWords);
    }

    [GeneratedRegex(@"(?im)^#{0,4}\s*executive\s+summary\s*:?\s*$\r?\n(?<body>(?:.+\r?\n?)+?)(?=^#{1,4}\s|\z)", RegexOptions.Multiline)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"```\s*mermaid", RegexOptions.IgnoreCase)]
    private static partial Regex MermaidRegex();

    [GeneratedRegex(@"(?im)^#{0,4}\s*(risks?|trade-?offs?)\b")]
    private static partial Regex RiskRegex();

    [GeneratedRegex(@"(?i)\b(verif|test|prove|acceptance)\w*")]
    private static partial Regex VerifyRegex();

    [GeneratedRegex(@"```\s*(csharp|cs|java|python|js|ts|typescript|sql|cobol)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeFenceRegex();
}

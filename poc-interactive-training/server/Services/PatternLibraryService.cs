using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Objective 1 + recommendation R5. Doc 06 is explicit that a bad shared prompt scales harm, so the
// library has an entry gate rather than an upload button. Deterministic: a contribution is admitted on
// evidence, not on enthusiasm.
public sealed class PatternLibraryService
{
    private static readonly List<LibraryEntry> Seed =
    [
        new("Start at the OKF index, not the code",
            "Agents given a ticket search the whole repo and pick the first plausible match.",
            "Point the agent at okf/index.md and require it to cite the concept, boundary and ADR before naming a file.",
            "Measured on 12 tickets: wrong-file rate fell from 5/12 to 1/12.",
            "Requires a maintained OKF bundle; useless in repos scoring under 40 on the context audit.",
            "Degrades silently if the bundle is stale — check stale_after before trusting it.",
            true, 100, []),
        new("Demand the dissent before you decide",
            "Engineers phrase questions to confirm what they already believe.",
            "Ask neutrally, then require the strongest case against the recommendation in the same response.",
            "Framing lab: substance changed on 3 of 5 trial questions when the leading framing was used.",
            "Works with any model; no context required.",
            "Adds tokens and can read as indecisive if pasted verbatim into a decision record.",
            true, 100, [])
    ];

    private readonly List<LibraryEntry> _entries = [.. Seed];

    public IReadOnlyList<LibraryEntry> Entries => _entries;

    public LibraryReview Submit(PatternSubmission submission)
    {
        var checks = new List<CurationCheck>();

        void Check(string name, bool passed, string requirement, string why) =>
            checks.Add(new CurationCheck(name, passed, requirement, why));

        Check("Title", submission.Title.Trim().Length >= 8,
            "A specific title of at least 8 characters",
            "An unsearchable title means the pattern is never found again.");

        Check("Problem stated", submission.Problem.Trim().Length >= 40,
            "The problem it solves, in at least 40 characters",
            "Without the problem, readers cannot tell whether it applies to them.");

        Check("Approach stated", submission.Approach.Trim().Length >= 40,
            "The prompt or approach itself",
            "A pattern with no method is an anecdote.");

        var hasNumbers = Regex.IsMatch(submission.Evidence, @"\d");
        Check("Evidence of outcome", submission.Evidence.Trim().Length >= 30 && hasNumbers,
            "Observed outcome including at least one number",
            "Doc 06: a bad shared prompt scales harm. Evidence is what separates a pattern from a rumour.");

        Check("Context dependency", submission.ContextNeeded.Trim().Length >= 20,
            "What context or preconditions it depends on",
            "A pattern that silently assumes an OKF bundle will fail in repos that lack one.");

        Check("Known failure modes", submission.FailureModes.Trim().Length >= 20,
            "At least one way this is known to fail",
            "A contribution claiming no failure modes has not been used enough to share.");

        var passed = checks.Count(check => check.Passed);
        var score = (int)Math.Round(100.0 * passed / checks.Count);
        var admitted = checks.All(check => check.Passed);

        if (admitted)
        {
            _entries.Add(new LibraryEntry(
                submission.Title.Trim(),
                submission.Problem.Trim(),
                submission.Approach.Trim(),
                submission.Evidence.Trim(),
                submission.ContextNeeded.Trim(),
                submission.FailureModes.Trim(),
                true, score, []));
        }

        var verdict = admitted
            ? "Admitted. The contribution carries its own evidence, preconditions and failure modes."
            : "Held at the gate. An ungated library is a hallucination amplifier with good intentions — fix the failing checks and resubmit.";

        return new LibraryReview(admitted, score, checks, verdict, _entries.Count);
    }
}

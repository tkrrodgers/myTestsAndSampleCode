using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Competency 12 — action authority. Deterministic decision procedure implementing 09 §3.4 and 10 §5:
// an autonomous agent may proceed without per-call approval only on reversible actions inside its
// registered envelope. Anything irreversible, production-affecting, customer-visible, security-policy
// changing, or outside the envelope stops for exact approval or is denied.
public sealed class ActionAuthorityService
{
    public static IReadOnlyList<CandidateAction> Catalog { get; } =
    [
        new("read-repo", "Read files in the registered repository", true, false, false, false, true),
        new("draft-pr", "Open a draft pull request for human review", true, false, false, false, true),
        new("run-tests", "Run the test suite in CI", true, false, false, false, true),
        new("write-branch", "Commit to a feature branch in the registered repo", true, false, false, false, true),
        new("merge-main", "Merge to main", false, false, false, false, true),
        new("force-push", "Force-push and rewrite published history", false, false, false, false, true),
        new("deploy-prod", "Deploy to production", false, true, false, false, true),
        new("alter-iam", "Change an IAM role binding", false, true, false, true, true),
        new("email-customer", "Send a message to a customer", false, true, true, false, true),
        new("drop-table", "Drop a database table", false, true, false, false, true),
        new("write-other-repo", "Commit to a repository outside the registered envelope", true, false, false, false, false)
    ];

    public ActionAuthorityDecision Decide(AgentRegistration agent, AgentTierResult tier, CandidateAction action)
    {
        var autonomous = string.Equals(agent.Autonomy, "Acts-autonomously", StringComparison.OrdinalIgnoreCase);
        var suggestsOnly = string.Equals(agent.Autonomy, "Suggests", StringComparison.OrdinalIgnoreCase);

        var reasons = new List<string>();
        string verdict;

        if (!tier.Registrable)
        {
            reasons.Add("The agent is not registrable: non-negotiable fields are missing, so no authority can be granted.");
            verdict = "DENY";
        }
        else if (!action.InEnvelope)
        {
            reasons.Add("Target is outside the agent's registered action envelope.");
            verdict = "DENY";
        }
        else if (action.CustomerVisible)
        {
            reasons.Add("Customer-visible effect — always requires exact human approval, never autonomous.");
            verdict = "APPROVE";
        }
        else if (action.SecurityPolicy)
        {
            reasons.Add("Security-policy change — always requires exact human approval.");
            verdict = "APPROVE";
        }
        else if (!action.Reversible)
        {
            reasons.Add("Irreversible action — approval is required regardless of autonomy level.");
            verdict = "APPROVE";
        }
        else if (action.ProductionAffecting)
        {
            reasons.Add("Production-affecting action — approval is required regardless of autonomy level.");
            verdict = "APPROVE";
        }
        else if (suggestsOnly)
        {
            reasons.Add("Agent autonomy is 'Suggests': it may propose this action but not perform it.");
            verdict = "PROPOSE";
        }
        else if (autonomous)
        {
            reasons.Add("Reversible, inside the registered envelope, and the agent is authorised to act — proceeds without per-call approval.");
            reasons.Add("Still subject to the documented post-run review; the named human remains accountable.");
            verdict = "AUTO";
        }
        else
        {
            reasons.Add("Agent autonomy is 'Acts-with-approval': a human confirms the exact action before it runs.");
            verdict = "APPROVE";
        }

        if (tier.Tier >= 3 && verdict == "AUTO")
        {
            reasons.Add("R3 is a risk label, not permission: the run is logged and the kill switch must be tested.");
        }

        return new ActionAuthorityDecision(
            action.Id,
            action.Description,
            verdict,
            VerdictLabel(verdict),
            reasons,
            ExactActionChecks(action),
            Intervention(verdict, action, tier.Tier));
    }

    private static string VerdictLabel(string verdict) => verdict switch
    {
        "AUTO" => "Runtime-authorised — may proceed",
        "APPROVE" => "Stops for exact human approval",
        "PROPOSE" => "May only be proposed",
        _ => "Denied"
    };

    // 09 §3.4 — approving "deploy" is not approval; the operator approves a specific, verified action.
    private static List<string> ExactActionChecks(CandidateAction action) =>
    [
        $"Action: {action.Id} — confirm this is the operation, not a similar one.",
        "Target: confirm the exact repository, environment, resource and identity.",
        "Parameters: any changed target or parameter voids a prior approval.",
        action.Reversible ? "Reversibility: reversible — confirm the rollback path exists." : "Reversibility: IRREVERSIBLE — confirm a tested rollback or accept the loss.",
        "Digest: the approval applies to this canonical action digest only."
    ];

    private static IncidentPath Intervention(string verdict, CandidateAction action, int tier)
    {
        if (verdict is "AUTO" or "PROPOSE")
        {
            return new IncidentPath(
                "None",
                "No intervention required. Post-run review still applies.",
                [],
                "Named accountable human",
                "Normal review cadence");
        }

        var steps = new List<string>
        {
            "Stop: withhold approval; the action does not run.",
            "Verify: re-read the exact action, target and parameters against the request."
        };

        if (!action.Reversible)
        {
            steps.Add("Escalate: irreversible actions need the accountable owner, not just the operator.");
        }

        if (action.ProductionAffecting || action.CustomerVisible)
        {
            steps.Add("If it already ran: invoke the kill switch and execute the registered rollback plan.");
            steps.Add("Declare an incident and notify the named responder.");
        }

        steps.Add("Record the decision and the reason — a refusal is evidence, not an absence of work.");

        return new IncidentPath(
            action.CustomerVisible || action.ProductionAffecting ? (tier >= 3 ? "Sev1" : "Sev2") : "Sev3",
            verdict == "DENY"
                ? "The request fell outside what this agent is permitted to do."
                : "The action is permitted only with exact, verified human approval.",
            steps,
            tier >= 3 ? "Named incident responder + accountable owner" : "Accountable owner",
            tier >= 3 ? "Contain immediately" : "Same business day");
    }
}

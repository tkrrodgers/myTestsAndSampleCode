using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Phase 0 control: deterministic risk tiering and proportionate-controls lookup, implementing
// ai-across-ces/10-agent-inventory-and-registry.md §4-5. Tiering sets a MINIMUM tier via escalation
// rules; it is deliberately not an averaged score, so one danger cannot be offset by another.
// No model is involved — a prerequisite control must not depend on an LLM being available.
public sealed class AgentRegistryService
{
    public static IReadOnlyList<string> BlastRadiusLevels { get; } =
        ["Sandbox", "Repo", "Team-system", "Shared-prod", "Customer-facing"];

    public static IReadOnlyList<string> AutonomyLevels { get; } =
        ["Suggests", "Acts-with-approval", "Acts-autonomously"];

    public static IReadOnlyList<string> DataTiers { get; } =
        ["Public", "Internal", "Confidential", "Restricted"];

    public static IReadOnlyList<string> AgentStatuses { get; } =
        ["Proposed", "Piloting", "Active", "Quarantined", "Deprecated", "Retired"];

    public AgentTierResult Evaluate(AgentRegistration registration)
    {
        var missing = FindMissingRequiredFields(registration);
        var (tier, reasons) = DeriveTier(registration);
        var controls = ControlsFor(tier, registration.ToolEnabled);

        return new AgentTierResult(
            tier,
            TierName(tier),
            reasons,
            missing,
            missing.Count == 0,
            controls,
            ReviewCadence(tier));
    }

    private static List<string> FindMissingRequiredFields(AgentRegistration r)
    {
        var missing = new List<string>();
        void Require(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(field);
            }
        }

        // Non-negotiable per 10 §3: without these you cannot reproduce, tier, authorize, or stop a release.
        Require(r.Owner, "owner");
        Require(r.ReleaseId, "release_id");
        Require(r.Model, "model (pinned version)");
        Require(r.DataTier, "data_tier");
        Require(r.Autonomy, "autonomy");
        Require(r.BlastRadius, "blast_radius");
        Require(r.AgentStatus, "agent_status");

        if (r.ToolEnabled)
        {
            Require(r.RuntimeIdentity, "runtime_identity (tool-enabled)");
            Require(r.ActionPolicy, "action_policy (tool-enabled)");
            Require(r.ApprovalMode, "approval_mode (tool-enabled)");
        }

        if (!string.IsNullOrWhiteSpace(r.Model) && !LooksPinned(r.Model))
        {
            missing.Add("model must be a pinned version, not a floating alias");
        }

        return missing;
    }

    // A floating alias (e.g. "latest") cannot be reproduced or drift-gated.
    private static bool LooksPinned(string model) =>
        !model.Contains("latest", StringComparison.OrdinalIgnoreCase) &&
        model.Any(char.IsDigit);

    private static (int Tier, List<string> Reasons) DeriveTier(AgentRegistration r)
    {
        var blast = IndexOf(BlastRadiusLevels, r.BlastRadius);
        var autonomy = IndexOf(AutonomyLevels, r.Autonomy);
        var data = IndexOf(DataTiers, r.DataTier);

        var tier = 0;
        var reasons = new List<string>();

        void Raise(int candidate, string reason)
        {
            if (candidate > tier)
            {
                tier = candidate;
            }

            if (candidate >= tier)
            {
                reasons.Add(reason);
            }
        }

        // Baseline from the three axes.
        if (blast >= 1 || data >= 1)
        {
            Raise(1, "Repo-level reach or Internal data sets a standard baseline (R1).");
        }

        if (blast >= 2)
        {
            Raise(2, "Team-system blast radius reaches beyond a single repo (R2).");
        }

        if (autonomy >= 1 && blast >= 2)
        {
            Raise(2, "Acts-with-approval at team-system reach (R2).");
        }

        // Escalation rules from 10 §4 — apply the highest that matches.
        if (data == 2)
        {
            Raise(2, "Confidential data ⇒ R2 minimum.");
        }

        if (data == 3)
        {
            Raise(3, "Restricted data ⇒ R3.");
        }

        if (blast >= 3)
        {
            Raise(3, "Shared-prod or customer-facing blast radius ⇒ R3.");
        }

        if (blast == 4)
        {
            Raise(3, "Customer-visible output ⇒ R3.");
        }

        if (autonomy == 2 && blast >= 2)
        {
            Raise(3, "Autonomous write to a shared system ⇒ R3, regardless of data tier.");
        }

        if (r.DependsOnTier is int inherited && inherited > tier)
        {
            Raise(inherited, $"Invokes another agent tiered R{inherited}; a chain inherits at least the higher tier.");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Sandbox reach, suggests only, Public data (R0).");
        }

        return (tier, reasons);
    }

    private static int IndexOf(IReadOnlyList<string> levels, string? value)
    {
        var index = value is null ? -1 : levels.ToList().FindIndex(level => string.Equals(level, value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    private static string TierName(int tier) => tier switch
    {
        0 => "R0 — Trivial",
        1 => "R1 — Standard",
        2 => "R2 — Elevated",
        _ => "R3 — Critical"
    };

    private static string ReviewCadence(int tier) => tier >= 2 ? "Quarterly" : "Annual";

    // 10 §5 — the proportionate-controls matrix, read as a budget: expensive machinery targets R2/R3.
    private static List<ControlRequirement> ControlsFor(int tier, bool toolEnabled)
    {
        var toolLabel = toolEnabled ? "Required (tool-enabled)" : "Not required";
        return
        [
            new("Registered with named owner", "Required", true),
            new("Named human accountable for outcomes", "Required", true),
            new("Inherits the default guardrail set", "Required", true),
            new("Human review before any consequential/irreversible effect", "Required", true),
            new("Model version pinned", tier >= 1 ? "Required" : "Not required", tier >= 1),
            new("Prompt regression tests", tier >= 1 ? "Required" : "Not required", tier >= 1),
            new("Machine-enforced action envelope", tier >= 2 ? "Required" : toolLabel, tier >= 2 || toolEnabled),
            new("Role-relevant operator/reviewer competency", tier >= 2 ? "Required" : tier == 1 ? "Recommended" : "Not required", tier >= 2),
            new("Golden-baseline / trap QA", tier >= 2 ? "Required" : "Not required", tier >= 2),
            new("Drift gate before any model change", tier >= 2 ? "Required" : tier == 1 ? "Opportunistic" : "Not required", tier >= 2),
            new("Runtime logging / observability", tier >= 2 ? "Required" : "Not required", tier >= 2),
            new("Human-anchored gold eval set", tier >= 3 ? "Required" : "Not required", tier >= 3),
            new("Kill switch tested", tier >= 3 ? "Required" : "Not required", tier >= 3),
            new("Named incident responder + rollback plan", tier >= 3 ? "Required" : "Not required", tier >= 3),
            new("Second-model cross-check on high-stakes output", tier >= 3 ? "Required" : "Not required", tier >= 3)
        ];
    }
}

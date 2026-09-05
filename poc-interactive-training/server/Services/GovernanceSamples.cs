using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Fixtures and deterministic scoring for the two governance tabs.
public static partial class GovernanceSamples
{
    // --- Tab 17: ticket quality gate ---

    public const string WeakTicket = """
        RENT-5120: Fix the pricing thing

        The pricing is wrong for some customers and needs to be updated to match
        what finance expects. Please also clean up the code while you are in there.

        Should be a quick one.
        """;

    public static readonly TicketCriterion[] Rubric =
    [
        new("Testable acceptance criteria",
            "At least two numbered, individually checkable outcomes.",
            "Write acceptance criteria a reviewer can tick off one at a time."),
        new("Named system or component",
            "Names a concrete file, class, service or endpoint.",
            "Name the component you believe is affected, or say you do not know."),
        new("Concrete values",
            "States the actual numbers, rates or identifiers involved.",
            "Give the real values. 'Match what finance expects' is not a value."),
        new("Explicit scope boundary",
            "States what is deliberately out of scope.",
            "Say what must NOT change. 'Clean up while you are in there' is unbounded."),
        new("Named owner for open questions",
            "Identifies who decides anything the ticket leaves open.",
            "Name the person or role who answers the open question.")
    ];

    private static readonly string[] VagueTerms =
    [
        "some customers", "the thing", "clean up", "should be quick", "as needed",
        "etc", "and so on", "appropriate", "properly", "correctly", "make it work"
    ];

    // Runs before any model does, so the gate has a reproducible signal of its own.
    public static TicketPrecheck ScoreTicket(string ticket)
    {
        var text = ticket ?? "";
        var lower = text.ToLowerInvariant();

        var numbered = NumberedPattern().Matches(text).Count;
        var hasComponent = ComponentPattern().IsMatch(text);
        var hasValues = ValuePattern().IsMatch(text);
        var hasScope = lower.Contains("out of scope") || lower.Contains("not change") ||
                       lower.Contains("do not") || lower.Contains("excluded");
        var hasOwner = OwnerPattern().IsMatch(text);
        var vague = VagueTerms.Where(term => lower.Contains(term)).ToList();

        var signals = new List<ContextStructureSignal>
        {
            new("Testable acceptance criteria", numbered >= 2, numbered == 0 ? "none found" : $"{numbered} numbered item(s)"),
            new("Named system or component", hasComponent, hasComponent ? "a file, class or service is named" : "no component named"),
            new("Concrete values", hasValues, hasValues ? "numeric or identifier values present" : "no concrete values"),
            new("Explicit scope boundary", hasScope, hasScope ? "scope boundary stated" : "nothing declared out of scope"),
            new("Named owner for open questions", hasOwner, hasOwner ? "an owner or role is named" : "no owner named")
        };

        var met = signals.Count(signal => signal.Present);
        var penalty = Math.Min(vague.Count * 6, 30);
        var score = Math.Max(0, (int)Math.Round(met * 100.0 / signals.Count) - penalty);

        return new TicketPrecheck(score, score >= 70, signals, vague);
    }

    [GeneratedRegex(@"(?m)^\s*(\d+[\.\)]|[-*])\s+\S")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"[A-Za-z0-9_]+\.(cs|java|cbl|ts|py|sql)\b|\b[A-Z][a-zA-Z0-9]*(Service|Controller|Handler|Repository|Job)\b|/api/[a-z]")]
    private static partial Regex ComponentPattern();

    [GeneratedRegex(@"\$\s?\d|\d+(\.\d+)?\s?%|\bv?\d+\.\d+\b|\b\d{3,}\b")]
    private static partial Regex ValuePattern();

    [GeneratedRegex(@"(?i)\b(owner|ask|confirm with|decided by|sign-?off|approver)\b\s*[:\-]?\s*\S")]
    private static partial Regex OwnerPattern();

    // --- Tab 18: cloud design trap hunt ---

    public const string TerraformDesign = """
        # sandbox.tf - proposed GCP sandbox for the hybrid human/LLM team
        # Reviewed by: (pending)

        resource "google_service_account" "agent" {
          account_id   = "llm-agent-sa"
          display_name = "LLM agent service account"
        }

        resource "google_project_iam_member" "agent_role" {
          project = var.project_id
          role    = "roles/owner"
          member  = "serviceAccount:${google_service_account.agent.email}"
        }

        resource "google_service_account_key" "agent_key" {
          service_account_id = google_service_account.agent.name
        }

        resource "google_compute_instance" "notebook" {
          name         = "vertex-notebook"
          machine_type = "a2-highgpu-1g"
          zone         = "us-central1-a"

          boot_disk {
            initialize_params { image = "debian-cloud/debian-11" }
          }

          network_interface {
            network = "default"
            access_config {
              # ephemeral external IP
            }
          }
        }

        resource "google_storage_bucket" "scratch" {
          name          = "team-scratch-data"
          location      = "US"
          force_destroy = true

          uniform_bucket_level_access = false
        }
        """;

    // Sealed until the grade is shown. Each is objectively present in the snippet above.
    public static readonly string[] PlantedTraps =
    [
        "The agent service account is granted roles/owner instead of a least-privilege custom role.",
        "A downloadable service account key is created, rather than using Workload Identity Federation.",
        "The GPU notebook instance is given an external IP with no Cloud NAT or IAP restriction.",
        "There is no budget or billing alert anywhere in the design, on a project with A2 GPU instances.",
        "The scratch bucket sets force_destroy = true and disables uniform bucket-level access.",
        "No resource carries labels, so no cost or ownership attribution is possible.",
        "There is no lifecycle or scheduled shutdown for the GPU instance."
    ];
}

public sealed record TicketCriterion(string Name, string Test, string Fix);

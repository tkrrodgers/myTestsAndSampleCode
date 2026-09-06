using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Phase 0 control implementing correction C4: "not checked in" does not mean "not sent to a model".
// Classifies content into a data tier, decides whether it may leave the boundary, and produces a
// scrubbed copy plus the incident path if something disallowed is found. Deterministic and offline —
// this gate must hold even when no model is reachable.
public sealed partial class DataTierClassifier
{
    private sealed record Detector(string Label, string Category, int Tier, Regex Pattern, string Replacement, Func<string, bool>? Validate = null);

    private static readonly Detector[] Detectors =
    [
        new("Private key block", "Credential", 3, PrivateKeyRegex(), "[PRIVATE_KEY_REDACTED]"),
        new("Cloud / API key", "Credential", 3, ApiKeyRegex(), "[API_KEY_REDACTED]"),
        new("Connection string password", "Credential", 3, ConnectionSecretRegex(), "[SECRET_REDACTED]"),
        new("Bearer / JWT token", "Credential", 3, JwtRegex(), "[TOKEN_REDACTED]"),
        new("Payment card number", "Financial PII", 3, PanRegex(), "[PAN_REDACTED]", IsLuhnValid),
        new("US national ID pattern", "Sensitive PII", 3, NationalIdRegex(), "[NATIONAL_ID_REDACTED]"),
        new("Email address", "PII", 2, EmailRegex(), "[EMAIL_REDACTED]"),
        new("Phone number", "PII", 2, PhoneRegex(), "[PHONE_REDACTED]"),
        new("Private network address", "Internal", 1, PrivateIpRegex(), "[INTERNAL_IP_REDACTED]"),
        new("Internal hostname", "Internal", 1, InternalHostRegex(), "[INTERNAL_HOST_REDACTED]")
    ];

    public DataTierAssessment Assess(string content)
    {
        var findings = new List<DataTierFinding>();
        var scrubbed = content ?? string.Empty;
        var highestTier = 0;

        foreach (var detector in Detectors)
        {
            var matches = detector.Pattern.Matches(scrubbed)
                .Where(match => detector.Validate is null || detector.Validate(match.Value))
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            highestTier = Math.Max(highestTier, detector.Tier);
            findings.Add(new DataTierFinding(
                detector.Label,
                detector.Category,
                detector.Tier,
                matches.Count,
                Mask(matches[0].Value)));

            // Replace only validated matches so an unvalidated near-miss is left intact.
            scrubbed = detector.Pattern.Replace(scrubbed, match =>
                detector.Validate is null || detector.Validate(match.Value) ? detector.Replacement : match.Value);
        }

        var tierName = TierName(highestTier);
        var (decision, rationale) = Decide(highestTier);
        var incident = BuildIncidentPath(highestTier, findings);

        return new DataTierAssessment(
            highestTier,
            tierName,
            decision,
            rationale,
            findings,
            decision == "BLOCK" ? string.Empty : scrubbed,
            incident);
    }

    private static (string Decision, string Rationale) Decide(int tier) => tier switch
    {
        3 => ("BLOCK", "Restricted content (credentials or sensitive/financial PII). Must not leave the boundary or reach a model, scrubbed or otherwise, until the owner authorises it."),
        2 => ("SCRUB", "Confidential PII detected. Permitted only after scrubbing; log artifact references, never payloads."),
        1 => ("ALLOW-INTERNAL", "Internal-only identifiers detected. Permitted within the internal boundary with reference-only logging."),
        _ => ("ALLOW", "No sensitive patterns detected. Treat as Public/Internal and still log references rather than payloads.")
    };

    private static IncidentPath BuildIncidentPath(int tier, List<DataTierFinding> findings)
    {
        if (tier < 2)
        {
            return new IncidentPath(
                "None",
                "No incident. Proceed under normal handling rules.",
                [],
                "Repo owner",
                "n/a");
        }

        var credential = findings.Any(finding => finding.Category == "Credential");
        var severity = tier == 3 ? (credential ? "Sev1" : "Sev2") : "Sev3";

        var steps = new List<string>();
        if (credential)
        {
            steps.Add("Treat the credential as compromised: revoke and rotate it before anything else.");
        }

        steps.Add("Stop the run; do not retry with the same payload.");
        steps.Add("Contain: confirm the content was not sent to an external model or written to logs.");
        steps.Add("Purge any captured payload; retain only artifact references and hashes.");

        if (tier == 3)
        {
            steps.Add("Declare the incident and notify the named responder and Security.");
            steps.Add("If an agent acted on it, invoke the kill switch and roll back per its registered plan.");
        }

        steps.Add("Record the detection in the trap/finding bank so the gate catches it earlier next time.");

        return new IncidentPath(
            severity,
            tier == 3
                ? "Restricted content reached a boundary check. This is a declarable incident, not a warning."
                : "Confidential content was detected before release. Handle as a near-miss and record it.",
            steps,
            tier == 3 ? "Named incident responder + Security" : "Repo owner + AI Quality",
            tier == 3 ? "Contain immediately; declare within 1 hour" : "Resolve within 1 business day");
    }

    private static string TierName(int tier) => tier switch
    {
        3 => "Restricted",
        2 => "Confidential",
        1 => "Internal",
        _ => "Public"
    };

    private static string Mask(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= 6)
        {
            return new string('•', trimmed.Length);
        }

        return string.Concat(trimmed.AsSpan(0, 3), new string('•', Math.Min(8, trimmed.Length - 6)), trimmed.AsSpan(trimmed.Length - 3));
    }

    private static bool IsLuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(character => character - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var doubling = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index];
            if (doubling)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubling = !doubling;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9]{16,}|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z\-_]{20,})\b")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"(?i)\b(?:password|pwd|api[_-]?key|secret)\s*=\s*[^\s;""']{6,}")]
    private static partial Regex ConnectionSecretRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex PanRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex NationalIdRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\d.\-])(?:\+?1[ .\-])?\(?\d{3}\)?[ .\-]\d{3}[ .\-]\d{4}(?![\d.\-])")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b")]
    private static partial Regex PrivateIpRegex();

    [GeneratedRegex(@"\b[a-z0-9\-]+\.(?:internal|corp|local|intranet)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InternalHostRegex();
}

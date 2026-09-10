using System.Text.RegularExpressions;

namespace PocInteractiveTraining.Server.Services;

public sealed record AutopilotContractResult(
    int Steps,
    int Selectors,
    int Resolved,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unused,
    IReadOnlyList<string> Violations)
{
    public bool Passed => Missing.Count == 0 && Violations.Count == 0;
}

/// <summary>
/// Verifies the manifest against the markup. A demo must never start against a stale script,
/// so a missing selector disables auto-run rather than failing silently at the click.
/// </summary>
public sealed partial class AutopilotContract(IWebHostEnvironment environment)
{
    [GeneratedRegex(@"data-auto=""([^""]+)""")]
    private static partial Regex DataAuto();

    // Razor emits these from a loop, so the literal attribute carries an interpolation hole.
    private static readonly (string Prefix, int Count)[] Templated =
    [
        ("s05.step-", 6),
        ("s08.q", 12),
        ("s28.amp-", 0)
    ];

    public AutopilotContractResult Verify()
    {
        var markup = ReadMarkup();
        var declared = DataAuto().Matches(markup)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Expand the templated tokens the markup writes dynamically.
        foreach (var token in declared.Where(value => value.Contains('@')).ToList())
        {
            declared.Remove(token);
            var prefix = token[..token.IndexOf('@')];
            declared.Add(prefix + "*");
        }

        var required = AutopilotManifest.Selectors()
            .Select(AutopilotManifest.TokenOf)
            .Where(token => token is not null)
            .Select(token => token!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missing = required.Where(token => !Matches(declared, token)).ToList();
        var used = required.ToHashSet(StringComparer.Ordinal);
        var unused = declared
            .Where(token => !token.EndsWith('*') && !used.Contains(token))
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

        return new AutopilotContractResult(
            AutopilotManifest.Steps.Length,
            required.Count,
            required.Count - missing.Count,
            missing,
            unused,
            Invariants());
    }

    private static bool Matches(HashSet<string> declared, string token) =>
        declared.Contains(token) ||
        declared.Any(entry => entry.EndsWith('*') && token.StartsWith(entry[..^1], StringComparison.Ordinal));

    /// <summary>Structural rules that keep a step from silently becoming unconstrained.</summary>
    private static List<string> Invariants()
    {
        var problems = new List<string>();

        foreach (var step in AutopilotManifest.Steps)
        {
            if (step.Facts.Count == 0)
            {
                problems.Add($"{step.StepId}: no fact pack — narration would be unconstrained");
            }

            if (step.Kind != AutoKind.Observe && string.IsNullOrWhiteSpace(step.Selector))
            {
                problems.Add($"{step.StepId}: {step.Kind} step has no selector");
            }

            if (step.WaitType == AutoWait.State && string.IsNullOrWhiteSpace(step.WaitKey))
            {
                problems.Add($"{step.StepId}: state wait has no status key");
            }

            if ((step.Kind is AutoKind.Select or AutoKind.Fill) && step.Value is null)
            {
                problems.Add($"{step.StepId}: {step.Kind} step has no value");
            }
        }

        var covered = AutopilotManifest.Steps.Select(step => step.SceneNumber).Distinct().ToList();
        for (var scene = 1; scene <= 35; scene++)
        {
            if (!covered.Contains(scene))
            {
                problems.Add($"scene {scene}: no manifest steps — coverage is not proven");
            }
        }

        return problems;
    }

    private string ReadMarkup()
    {
        var path = Path.Combine(environment.ContentRootPath, "Components", "Pages", "Home.razor");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}

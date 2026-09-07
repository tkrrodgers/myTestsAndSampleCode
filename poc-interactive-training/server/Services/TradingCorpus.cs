namespace PocInteractiveTraining.Server.Services;

// The corpus is three real .NET broker-dealer services on disk, not a fixture. They share an
// identical set of framework classes (non-functional) and diverge entirely in their trading logic
// (functional), which is exactly the shape a real estate audit has to separate.
public sealed class TradingCorpus
{
    public sealed record RepoDescriptor(string Name, string AssetClass, string Divergence, string OkfQuality);

    public sealed record CodeFile(string Repo, string Name, string Path, string Role, string Source, int Lines);

    public sealed record OkfDocument(string Repo, string Path, string Type, string Title, string Text);

    // Role is ground truth from the source tree, used to score the clustering — never fed to the model.
    private static readonly string[] NonFunctionalFolders =
        ["Messaging", "Observability", "Persistence", "Pipeline", "Configuration"];

    public static readonly RepoDescriptor[] Repos =
    [
        new("EquityTradingPipeline", "Listed equities", "Market/limit/stop orders, FIFO tax lots, Rule 606 routing", "Strong"),
        new("FixedIncomeOptionsEngine", "Options and OTC bonds", "Multi-leg strategies, yield to maturity, Reg-T margin", "Strong"),
        new("CryptoFxSpotDesk", "Spot FX and crypto", "Instant settlement, LP quoting, AML wallet screening", "Weak")
    ];

    public IReadOnlyList<CodeFile> Files { get; }
    public IReadOnlyList<OkfDocument> OkfDocuments { get; } = [];
    public string? Root { get; }
    public bool IsAvailable => Files.Count > 0;
    public string StatusMessage { get; }

    public TradingCorpus(string? configuredRoot)
    {
        Root = ResolveRoot(configuredRoot);
        if (Root is null)
        {
            Files = [];
            StatusMessage = "Trading corpus not found; expected EquityTradingPipeline, FixedIncomeOptionsEngine and CryptoFxSpotDesk beside the workspace.";
            return;
        }

        var files = new List<CodeFile>();
        var okf = new List<OkfDocument>();
        foreach (var repo in Repos)
        {
            var repoRoot = Path.Combine(Root, repo.Name);
            if (!Directory.Exists(repoRoot))
            {
                continue;
            }

            var okfRoot = Path.Combine(repoRoot, "okf");
            if (Directory.Exists(okfRoot))
            {
                foreach (var path in Directory.EnumerateFiles(okfRoot, "*.md", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var text = File.ReadAllText(path);
                    okf.Add(new OkfDocument(
                        repo.Name,
                        Path.GetRelativePath(repoRoot, path).Replace('\\', '/'),
                        FrontmatterValue(text, "type") ?? "Index",
                        FrontmatterValue(text, "title") ?? Path.GetFileNameWithoutExtension(path),
                        text));
                }
            }

            foreach (var path in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

                // Generated assembly attributes and global usings say nothing about the service.
                if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                var source = File.ReadAllText(path);

                // Program.cs is the composition root: bootstrap, not business logic.
                var role = NonFunctionalFolders.Any(folder => relative.Contains("/" + folder + "/", StringComparison.Ordinal)) || relative == "Program.cs"
                    ? "Non-functional"
                    : "Functional";

                files.Add(new CodeFile(
                    repo.Name,
                    Path.GetFileNameWithoutExtension(path),
                    relative,
                    role,
                    source,
                    source.Split('\n').Length));
            }
        }

        Files = files;
        OkfDocuments = okf;
        var repoCount = files.Select(file => file.Repo).Distinct(StringComparer.Ordinal).Count();
        StatusMessage = files.Count == 0
            ? $"Trading corpus at {Root} contained no source files."
            : $"{files.Count} files across {repoCount} repo(s) from {Root} ({files.Count(file => file.Role == "Non-functional")} non-functional, {files.Count(file => file.Role == "Functional")} functional), {okf.Count} OKF document(s).";
    }

    private static string? FrontmatterValue(string text, string key)
    {
        foreach (var line in text.Split('\n').Take(20))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(key.Length + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    // Tier 3 for one repository: its own OKF bundle, which is where atomic repo context actually lives.
    public string Tier3Context(string repoName)
    {
        var documents = OkfDocuments.Where(document => document.Repo == repoName).ToList();
        if (documents.Count == 0)
        {
            return $"No OKF bundle found for {repoName}.";
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine($"REPOSITORY CONTEXT (Tier 3) — {repoName}");
        foreach (var document in documents)
        {
            text.AppendLine($"=== {document.Path} ===");
            text.AppendLine(document.Text);
        }

        foreach (var file in Files.Where(file => file.Repo == repoName && file.Role == "Non-functional" && file.Name is "OrderProcessingPipeline" or "PipelineContracts"))
        {
            text.AppendLine($"=== {file.Path} ===");
            text.AppendLine(file.Source);
        }

        return text.ToString();
    }

    private static string? ResolveRoot(string? configuredRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            candidates.Add(configuredRoot);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; level < 8 && directory is not null; level++, directory = directory.Parent)
            {
                candidates.Add(directory.FullName);
            }
        }

        return candidates.FirstOrDefault(candidate =>
            Repos.All(repo => Directory.Exists(Path.Combine(candidate, repo.Name))));
    }
}

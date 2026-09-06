using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Retrieves the reference documentation the judge is graded against. Without this the judge is told it
// has authoritative sources and then scores from memory anyway, which is the exact failure this platform
// warns about elsewhere.
public sealed partial class DocumentationFetcher
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxCharactersPerDocument = 15_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    // The catalog is compiled in, so this is belt-and-braces rather than the primary control: it stops a
    // future edit turning a prompt-supplied string into a server-side request to an arbitrary host.
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cloud.google.com",
        "docs.cloud.google.com",
        "developers.google.com"
    };

    private readonly HttpClient _http;
    private readonly Dictionary<string, (ReferenceDocument Document, DateTimeOffset FetchedAt)> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DocumentationFetcher(HttpClient http)
    {
        _http = http;
        _http.Timeout = Timeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PocInteractiveTraining/1.0 (training demo; documentation grounding)");
    }

    public async Task<IReadOnlyList<ReferenceDocument>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var documents = new List<ReferenceDocument>();
        foreach (var url in urls)
        {
            documents.Add(await FetchOneAsync(url, cancellationToken));
        }

        return documents;
    }

    private async Task<ReferenceDocument> FetchOneAsync(string url, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(url, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < CacheLifetime)
            {
                return cached.Document with { FromCache = true };
            }
        }
        finally
        {
            _gate.Release();
        }

        var document = await RetrieveAsync(url, cancellationToken);
        if (document.Retrieved)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _cache[url] = (document, DateTimeOffset.UtcNow);
            }
            finally
            {
                _gate.Release();
            }
        }

        return document;
    }

    private async Task<ReferenceDocument> RetrieveAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Contains(uri.Host))
        {
            return Failed(url, "Rejected: not an https URL on an allowed documentation host.");
        }

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failed(url, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            var html = Encoding.UTF8.GetString(buffer.ToArray());
            var title = TitleTag().Match(html) is { Success: true } match
                ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim()
                : uri.AbsolutePath;
            var text = ExtractText(html);
            if (text.Length < 400)
            {
                return Failed(url, "Retrieved, but no readable article body was found — the page layout has probably changed.");
            }

            var truncated = text.Length > MaxCharactersPerDocument;
            var supplied = truncated ? text[..MaxCharactersPerDocument] : text;
            return new ReferenceDocument(
                url,
                title,
                true,
                supplied.Length,
                supplied,
                truncated,
                DateTimeOffset.UtcNow,
                null,
                false);
        }
        catch (TaskCanceledException)
        {
            return Failed(url, $"Timed out after {Timeout.TotalSeconds:N0}s.");
        }
        catch (HttpRequestException ex)
        {
            return Failed(url, ex.Message);
        }
    }

    private static ReferenceDocument Failed(string url, string error) =>
        new(url, url, false, 0, string.Empty, false, DateTimeOffset.UtcNow, error, false);

    // Whole-page stripping yields navigation chrome, not documentation: on Google's devsite the sidebar,
    // language picker and product list dwarf the article. Isolate the article body first, or ground the
    // judge on a menu.
    private static string ExtractText(string html)
    {
        var body = IsolateArticleBody(html);
        var stripped = ScriptOrStyle().Replace(body, " ");
        stripped = Comments().Replace(stripped, " ");
        stripped = BlockBreak().Replace(stripped, "\n");
        stripped = Tags().Replace(stripped, " ");
        stripped = WebUtility.HtmlDecode(stripped);
        stripped = HorizontalSpace().Replace(stripped, " ");
        stripped = BlankLines().Replace(stripped, "\n");
        return stripped.Trim();
    }

    private static string IsolateArticleBody(string html)
    {
        foreach (var marker in ArticleMarkers)
        {
            var start = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }

            var open = html.IndexOf('>', start);
            if (open < 0)
            {
                continue;
            }

            var end = html.IndexOf("</devsite-content", open, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                end = html.IndexOf("<devsite-page-rating", open, StringComparison.OrdinalIgnoreCase);
            }

            if (end < 0)
            {
                end = html.IndexOf("</article", open, StringComparison.OrdinalIgnoreCase);
            }

            if (end > open)
            {
                return html[(open + 1)..end];
            }
        }

        return string.Empty;
    }

    private static readonly string[] ArticleMarkers =
    [
        "<div class=\"devsite-article-body",
        "devsite-article-body",
        "<article",
        "<main"
    ];

    [GeneratedRegex(@"<(script|style|noscript)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"</(p|div|li|h[1-6]|tr|section|article)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"[ \t\f\v\r]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n\s*\n\s*")]
    private static partial Regex BlankLines();
}

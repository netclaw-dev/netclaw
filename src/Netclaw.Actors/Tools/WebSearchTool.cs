using System.ComponentModel;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Searches the web via DuckDuckGo Lite and returns results as text.
/// Uses randomized user-agent headers and rate limiting to be a good citizen.
/// </summary>
[NetclawTool("web_search",
    "Search the web and return a list of results with titles, URLs, and snippets",
    Grant = "web")]
public sealed partial class WebSearchTool : NetclawTool<WebSearchTool.Params>
{
    private const string DdgLiteUrl = "https://lite.duckduckgo.com/lite/";
    private const int DefaultMaxResults = 10;

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
    ];

    private static readonly string[] AcceptLanguages =
    [
        "en-US,en;q=0.9",
        "en-US,en;q=0.9,es;q=0.8",
        "en-GB,en;q=0.9,en-US;q=0.8",
        "en-US,en;q=0.5",
        "en-CA,en;q=0.9,en-US;q=0.8",
    ];

    private readonly HttpClient _httpClient;
    private readonly ToolConfig _config;
    private readonly Random _random = new();

    // Rate limiting: randomized delay between requests (500-2000ms)
    private static readonly object _rateLimitLock = new();
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public record Params(
        [property: Description("The search query")] string Query,
        [property: Description("Maximum number of results to return (default 10, max 30)")] int? MaxResults = null);

    public WebSearchTool(ToolConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? new HttpClient();
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query))
            return "Error: 'query' parameter is required.";

        var maxResults = Math.Clamp(args.MaxResults ?? DefaultMaxResults, 1, 30);

        // Randomized rate limiting (500-2000ms gap between requests)
        await MaybeDelaySearchAsync(ct);

        try
        {
            var html = await FetchSearchResultsAsync(args.Query, ct);

            // Detect DuckDuckGo CAPTCHA / bot detection
            if (html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase))
                return "Error: Search blocked by DuckDuckGo bot detection (CAPTCHA). Try again later or use a different search provider.";

            var results = ParseResults(html, maxResults);

            if (results.Count == 0)
                return $"No results found for: {args.Query}";

            return FormatResults(results, args.Query);
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Search request failed: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Search request timed out.";
        }
    }

    private async Task<string> FetchSearchResultsAsync(string query, CancellationToken ct)
    {
        var url = $"{DdgLiteUrl}?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Full browser header fingerprint to avoid bot detection
        request.Headers.UserAgent.ParseAdd(UserAgents[_random.Next(UserAgents.Length)]);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd(AcceptLanguages[_random.Next(AcceptLanguages.Length)]);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.Connection.Add("keep-alive");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.Zero };
        if (_random.Next(2) == 0)
            request.Headers.TryAddWithoutValidation("DNT", "1");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task MaybeDelaySearchAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_rateLimitLock)
        {
            var minGap = TimeSpan.FromMilliseconds(500 + _random.Next(1500));
            var elapsed = DateTimeOffset.UtcNow - _lastRequestTime;
            if (elapsed >= minGap)
            {
                _lastRequestTime = DateTimeOffset.UtcNow;
                return;
            }
            delay = minGap - elapsed;
            _lastRequestTime = DateTimeOffset.UtcNow + delay;
        }
        await Task.Delay(delay, ct);
    }

    /// <summary>
    /// Parse DuckDuckGo Lite HTML into search results.
    /// Each result is a group of table rows: result-link anchor, result-snippet td, link-text span.
    /// </summary>
    internal static List<SearchResult> ParseResults(string html, int maxResults)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<SearchResult>();

        // Find all result links
        var links = doc.DocumentNode.SelectNodes("//a[@class='result-link']");
        if (links is null)
            return results;

        foreach (var link in links)
        {
            if (results.Count >= maxResults)
                break;

            var rawUrl = WebUtility.HtmlDecode(link.GetAttributeValue("href", ""));
            var url = CleanDuckDuckGoUrl(rawUrl);
            var title = WebUtility.HtmlDecode(link.InnerText).Trim();

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(title))
                continue;

            // The snippet is in the next tr's td.result-snippet
            // Navigate: a -> td -> tr -> next sibling tr -> td.result-snippet
            var parentTr = link.Ancestors("tr").FirstOrDefault();
            var snippetTr = parentTr?.NextSibling;
            // Skip whitespace text nodes
            while (snippetTr is { NodeType: not HtmlNodeType.Element })
                snippetTr = snippetTr.NextSibling;

            var snippetTd = snippetTr?.SelectSingleNode(".//td[@class='result-snippet']");
            var snippet = snippetTd is not null
                ? WebUtility.HtmlDecode(snippetTd.InnerText).Trim()
                : "";

            results.Add(new SearchResult(title, url, snippet));
        }

        return results;
    }

    private static string FormatResults(List<SearchResult> results, string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Search results for: {query}");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"{i + 1}. {r.Title}");
            sb.AppendLine($"   URL: {r.Url}");
            if (!string.IsNullOrEmpty(r.Snippet))
                sb.AppendLine($"   {r.Snippet}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// DDG Lite wraps result URLs in a redirect: //duckduckgo.com/l/?uddg=ENCODED_URL&amp;...
    /// Extract the real destination URL.
    /// </summary>
    internal static string CleanDuckDuckGoUrl(string rawUrl)
    {
        if (rawUrl.Contains("duckduckgo.com/l/?uddg=", StringComparison.Ordinal))
        {
            var uddgIndex = rawUrl.IndexOf("uddg=", StringComparison.Ordinal);
            if (uddgIndex >= 0)
            {
                var encoded = rawUrl[(uddgIndex + 5)..];
                var ampIndex = encoded.IndexOf('&');
                if (ampIndex >= 0)
                    encoded = encoded[..ampIndex];
                var decoded = Uri.UnescapeDataString(encoded);
                if (!string.IsNullOrEmpty(decoded))
                    return decoded;
            }
        }
        return rawUrl;
    }

    internal record SearchResult(string Title, string Url, string Snippet);
}

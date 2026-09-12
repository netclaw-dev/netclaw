// -----------------------------------------------------------------------
// <copyright file="DuckDuckGoBackend.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using HtmlAgilityPack;

namespace Netclaw.Search;

/// <summary>
/// Search backend that scrapes DuckDuckGo Lite HTML results.
/// Uses randomized user-agent headers and rate limiting to reduce bot detection.
/// </summary>
public sealed class DuckDuckGoBackend : ISearchBackend
{
    private const string DdgLiteUrl = "https://lite.duckduckgo.com/lite/";

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
    private readonly TimeProvider _timeProvider;
    private readonly Random _random = new();

    private static readonly object RateLimitLock = new();
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public DuckDuckGoBackend(HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        await MaybeDelayAsync(ct);

        try
        {
            var html = await FetchSearchResultsAsync(query, ct);

            if (html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase))
                return new SearchBackendResult.Error(
                    "Search blocked by DuckDuckGo bot detection (CAPTCHA). " +
                    "Configure Brave Search or SearXNG as an alternative backend in your search config.");

            var results = ParseResults(html, maxResults);
            return new SearchBackendResult.Success(results);
        }
        catch (HttpRequestException ex)
        {
            return new SearchBackendResult.Error($"DuckDuckGo search request failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SearchBackendResult.Error("DuckDuckGo search request timed out.");
        }
    }

    private async Task<string> FetchSearchResultsAsync(string query, CancellationToken ct)
    {
        var url = $"{DdgLiteUrl}?q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

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

    private async Task MaybeDelayAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (RateLimitLock)
        {
            var minGap = TimeSpan.FromMilliseconds(500 + _random.Next(1500));
            var now = _timeProvider.GetUtcNow();
            var elapsed = now - _lastRequestTime;
            if (elapsed >= minGap)
            {
                _lastRequestTime = now;
                return;
            }
            delay = minGap - elapsed;
            _lastRequestTime = now + delay;
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

            var parentTr = link.Ancestors("tr").FirstOrDefault();
            var snippetTr = parentTr?.NextSibling;
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
                var ampIndex = encoded.IndexOf('&', StringComparison.Ordinal);
                if (ampIndex >= 0)
                    encoded = encoded[..ampIndex];
                var decoded = Uri.UnescapeDataString(encoded);
                if (!string.IsNullOrEmpty(decoded))
                    return decoded;
            }
        }
        return rawUrl;
    }
}

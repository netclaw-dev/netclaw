using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Netclaw.Search;

/// <summary>
/// Search backend using the Brave Search API.
/// Authenticates via X-Subscription-Token header.
/// </summary>
public sealed partial class BraveSearchBackend : ISearchBackend
{
    private const string BaseUrl = "https://api.search.brave.com/res/v1/web/search";
    private const int MaxRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly TimeProvider _timeProvider;

    public BraveSearchBackend(string apiKey, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        _apiKey = apiKey;
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var url = $"{BaseUrl}?q={Uri.EscapeDataString(query)}&count={maxResults}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("X-Subscription-Token", _apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

                using var response = await _httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new SearchBackendResult.Error(
                        "Brave Search API authentication failed. Check your API key in secrets.json (Search.BraveApiKey).");

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == MaxRetries - 1)
                        break;

                    var delay = ParseRetryAfter(response.Headers.RetryAfter, attempt, _timeProvider);
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                if (!string.IsNullOrEmpty(contentType) &&
                    !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    return new SearchBackendResult.Error(
                        $"Brave Search returned unexpected content " +
                        $"(status: {(int)response.StatusCode}, content-type: {contentType}, " +
                        $"content-encoding: {contentEncoding ?? "none"}).");

                string json;
                if (string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
                {
                    using var compressed = await response.Content.ReadAsStreamAsync(ct);
                    using var decompressor = new GZipStream(compressed, CompressionMode.Decompress);
                    using var reader = new StreamReader(decompressor);
                    json = await reader.ReadToEndAsync(ct);
                }
                else if (!string.IsNullOrEmpty(contentEncoding) &&
                         !string.Equals(contentEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                {
                    return new SearchBackendResult.Error(
                        $"Brave Search returned unsupported encoding " +
                        $"(status: {(int)response.StatusCode}, content-type: {contentType}, " +
                        $"content-encoding: {contentEncoding}).");
                }
                else
                {
                    json = await response.Content.ReadAsStringAsync(ct);
                }

                var results = ParseResults(json, maxResults);
                return new SearchBackendResult.Success(results);
            }
            catch (HttpRequestException ex)
            {
                return new SearchBackendResult.Error($"Brave Search request failed: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new SearchBackendResult.Error("Brave Search request timed out.");
            }
        }

        return new SearchBackendResult.Error(
            "Brave Search API rate limit exceeded. Wait before retrying or upgrade your plan.");
    }

    /// <summary>
    /// Parses the Retry-After response header into a wait duration.
    /// Falls back to exponential backoff (5s, 10s, 20s) when the header is absent or in the past.
    /// </summary>
    internal static TimeSpan ParseRetryAfter(RetryConditionHeaderValue? retryAfter, int attempt, TimeProvider timeProvider)
    {
        if (retryAfter is not null)
        {
            if (retryAfter.Delta.HasValue)
                return retryAfter.Delta.Value;

            if (retryAfter.Date.HasValue)
            {
                var remaining = retryAfter.Date.Value - timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    return remaining;
            }
        }

        // Exponential backoff: 5s, 10s, 20s
        return TimeSpan.FromSeconds(5 * Math.Pow(2, attempt));
    }

    internal static List<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("web", out var web))
            return results;

        if (!web.TryGetProperty("results", out var resultsArray))
            return results;

        foreach (var item in resultsArray.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(title))
                continue;

            results.Add(new SearchResult(
                StripHtml(title),
                url,
                StripHtml(description)));
        }

        return results;
    }

    /// <summary>
    /// Brave Search API returns HTML markup (e.g. &lt;strong&gt;) and entities in
    /// titles and descriptions. Strip tags and decode for clean LLM consumption.
    /// </summary>
    internal static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var noTags = HtmlTagRegex().Replace(input, "");
        return WebUtility.HtmlDecode(noTags);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}

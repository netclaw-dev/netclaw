// -----------------------------------------------------------------------
// <copyright file="SearXngBackend.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Search;

/// <summary>
/// Search backend using a self-hosted SearXNG instance.
/// Requires the instance to have JSON format enabled in <c>settings.yml</c>
/// (<c>search.formats</c> must include <c>json</c>). Authenticated instances
/// are not supported. See https://netclaw.dev/docs/configuration/search-providers/.
/// </summary>
public sealed class SearXngBackend : ISearchBackend
{
    private const int MaxRetries = 3;
    private const string DocsUrl = "https://netclaw.dev/docs/configuration/search-providers/";
    private const string FormatErrorMessage =
        $"SearXNG returned a non-JSON response. Enable JSON output in settings.yml: "
        + $"set 'search.formats' to include 'json'. See {DocsUrl} for the supported configuration.";

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly TimeProvider _timeProvider;

    public SearXngBackend(string endpoint, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        // Don't mutate caller-supplied clients; only set a default timeout when we own the instance.
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var url = $"{_endpoint}/search?q={Uri.EscapeDataString(query)}&format=json";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(SearchRetryHelpers.UserAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(request, ct);

                // 403 = format not enabled in settings.yml. Permanent server-side
                // configuration error; do not retry.
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return new SearchBackendResult.Error(FormatErrorMessage);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == MaxRetries - 1)
                        break;

                    var delay = SearchRetryHelpers.ParseRetryAfter(response.Headers.RetryAfter, attempt, _timeProvider);
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var body = await response.Content.ReadAsStringAsync(ct);

                // HTML body on 200 = format silently ignored by some configurations.
                // Same actionable cause as 403; same terminal error.
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                    || body.TrimStart().StartsWith('<'))
                    return new SearchBackendResult.Error(FormatErrorMessage);

                try
                {
                    var results = ParseResults(body, maxResults);
                    return new SearchBackendResult.Success(results);
                }
                catch (JsonException ex)
                {
                    return new SearchBackendResult.Error(
                        $"SearXNG returned malformed JSON ({_endpoint}): {ex.Message}. See {DocsUrl}.");
                }
            }
            catch (HttpRequestException ex)
            {
                return new SearchBackendResult.Error($"SearXNG endpoint unreachable ({_endpoint}): {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new SearchBackendResult.Error($"SearXNG request timed out ({_endpoint}).");
            }
        }

        return new SearchBackendResult.Error(
            $"SearXNG rate limit exceeded ({_endpoint}). The instance's bot-detection limiter is throttling requests; " +
            $"reduce concurrency or whitelist the Netclaw user agent. See {DocsUrl}.");
    }

    internal static List<SearchResult> ParseResults(string json, int maxResults)
    {
        var results = new List<SearchResult>();

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var resultsArray))
            return results;

        foreach (var item in resultsArray.EnumerateArray())
        {
            if (results.Count >= maxResults)
                break;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(title))
                continue;

            results.Add(new SearchResult(title, url, content));
        }

        return results;
    }
}

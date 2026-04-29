// -----------------------------------------------------------------------
// <copyright file="SearXngBackend.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Search;

/// <summary>
/// Search backend using a self-hosted SearXNG instance.
/// Requires the instance to have JSON format enabled in settings.yml.
/// </summary>
public sealed class SearXngBackend : ISearchBackend
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;

    public SearXngBackend(string endpoint, HttpClient? httpClient = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var url = $"{_endpoint}/search?q={Uri.EscapeDataString(query)}&format=json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var body = await response.Content.ReadAsStringAsync(ct);

            // Detect HTML response — means JSON format is not enabled
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
                || body.TrimStart().StartsWith('<'))
            {
                return new SearchBackendResult.Error(
                    "SearXNG returned HTML instead of JSON. Enable JSON format in your SearXNG settings.yml: " +
                    "search.formats should include 'json'.");
            }

            var results = ParseResults(body, maxResults);
            return new SearchBackendResult.Success(results);
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

// -----------------------------------------------------------------------
// <copyright file="MattermostProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Cli.Mattermost;

public sealed record MattermostProbeResult(
    bool Success,
    string? ErrorMessage,
    string? BotUsername);

public sealed record ResolvedMattermostChannel(
    string ChannelId,
    string ChannelName,
    string DisplayName);

public sealed record MattermostChannelResolutionResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<ResolvedMattermostChannel> Resolved,
    IReadOnlyList<string> Unresolved);

public interface IMattermostProbe
{
    Task<MattermostProbeResult> ProbeAsync(string serverUrl, string botToken, CancellationToken ct = default);

    Task<MattermostChannelResolutionResult> ResolveChannelIdsAsync(
        string serverUrl, string botToken, IReadOnlyList<string> channelIds, CancellationToken ct = default);
}

public sealed class MattermostProbe : IMattermostProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public MattermostProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<MattermostProbeResult> ProbeAsync(string serverUrl, string botToken, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, serverUrl, "/api/v4/users/me", botToken);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                return new MattermostProbeResult(false, MapHttpError(response.StatusCode, body), null);
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var username = root.TryGetProperty("username", out var usernameProp)
                ? usernameProp.GetString()
                : null;
            return new MattermostProbeResult(true, null, username);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MattermostProbeResult(false,
                "Connection timed out after 10 seconds. Check your network connection.", null);
        }
        catch (OperationCanceledException)
        {
            return new MattermostProbeResult(false, "Validation cancelled.", null);
        }
        catch (HttpRequestException ex)
        {
            return new MattermostProbeResult(false, $"Connection failed: {ex.Message}", null);
        }
        catch (InvalidOperationException ex)
        {
            return new MattermostProbeResult(false, ex.Message, null);
        }
    }

    public async Task<MattermostChannelResolutionResult> ResolveChannelIdsAsync(
        string serverUrl, string botToken, IReadOnlyList<string> channelIds, CancellationToken ct = default)
    {
        var normalized = channelIds
            .Select(static id => id.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return new MattermostChannelResolutionResult(true, null, [], []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ResolveTimeout);

        try
        {
            var resolved = new List<ResolvedMattermostChannel>();
            var unresolved = new List<string>();

            foreach (var channelId in normalized)
            {
                var result = await FetchChannelAsync(serverUrl, botToken, channelId, timeoutCts.Token);
                if (result is null)
                {
                    unresolved.Add(channelId);
                    continue;
                }

                resolved.Add(result);
            }

            return new MattermostChannelResolutionResult(
                unresolved.Count == 0, null, resolved, unresolved);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MattermostChannelResolutionResult(false,
                "Channel resolution timed out after 30 seconds.", [], []);
        }
        catch (OperationCanceledException)
        {
            return new MattermostChannelResolutionResult(false,
                "Channel resolution cancelled.", [], []);
        }
        catch (HttpRequestException ex)
        {
            return new MattermostChannelResolutionResult(false,
                $"Channel resolution failed: {ex.Message}", [], []);
        }
        catch (InvalidOperationException ex)
        {
            return new MattermostChannelResolutionResult(false, ex.Message, [], []);
        }
    }

    private async Task<ResolvedMattermostChannel?> FetchChannelAsync(
        string serverUrl, string botToken, string channelId, CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            serverUrl,
            $"/api/v4/channels/{Uri.EscapeDataString(channelId)}",
            botToken);
        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(MapHttpError(response.StatusCode, body));
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var displayName = root.TryGetProperty("display_name", out var displayNameProp)
            ? displayNameProp.GetString()
            : null;

        return id is null
            ? null
            : new ResolvedMattermostChannel(id, name ?? id, displayName ?? name ?? id);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string serverUrl, string path, string botToken)
    {
        if (!Uri.TryCreate(serverUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Mattermost server URL must be an absolute http:// or https:// URL.");

        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        return request;
    }

    private static string MapHttpError(HttpStatusCode statusCode, string body)
    {
        var message = TryExtractMattermostMessage(body);
        return (statusCode, message) switch
        {
            (HttpStatusCode.Unauthorized, _) => "Bot token is invalid. Check the Mattermost bot access token.",
            (HttpStatusCode.Forbidden, _) => "Access denied. Check bot permissions.",
            (HttpStatusCode.NotFound, _) => "Resource not found. Check the ID is correct.",
            (HttpStatusCode.TooManyRequests, _) => "Rate limited by Mattermost API. Try again in a few seconds.",
            (_, { Length: > 0 }) => $"Mattermost API error: {(int)statusCode} {statusCode}: {message}",
            _ => $"Mattermost API error: {(int)statusCode} {statusCode}"
        };
    }

    private static string? TryExtractMattermostMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var messageProp)
                ? messageProp.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

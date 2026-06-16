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

    // Accepts channel IDs (26-char) OR names/display-names (#town-square). Enumerates the bot's team
    // channels once and matches each reference by id, url slug name, or human display name, so
    // operators can enter readable names instead of opaque ids (the resolved id is what the runtime
    // ACL matches).
    public async Task<MattermostChannelResolutionResult> ResolveChannelIdsAsync(
        string serverUrl, string botToken, IReadOnlyList<string> channelRefs, CancellationToken ct = default)
    {
        var normalized = channelRefs
            .Select(static reference => reference.Trim().TrimStart('#'))
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return new MattermostChannelResolutionResult(true, null, [], []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ResolveTimeout);

        try
        {
            var byId = new Dictionary<string, ResolvedMattermostChannel>(StringComparer.Ordinal);
            var byName = new Dictionary<string, ResolvedMattermostChannel>(StringComparer.OrdinalIgnoreCase);
            foreach (var teamId in await FetchBotTeamIdsAsync(serverUrl, botToken, timeoutCts.Token))
            {
                foreach (var channel in await FetchTeamChannelsAsync(serverUrl, botToken, teamId, timeoutCts.Token))
                {
                    byId[channel.ChannelId] = channel;
                    // Match either the url slug or the human display name; first match wins.
                    byName.TryAdd(channel.ChannelName, channel);
                    byName.TryAdd(channel.DisplayName, channel);
                }
            }

            var resolved = new List<ResolvedMattermostChannel>();
            var unresolved = new List<string>();
            foreach (var reference in normalized)
            {
                if (byId.TryGetValue(reference, out var idMatch))
                    resolved.Add(idMatch);
                else if (byName.TryGetValue(reference, out var nameMatch))
                    resolved.Add(nameMatch);
                else
                    unresolved.Add(reference);
            }

            return new MattermostChannelResolutionResult(unresolved.Count == 0, null, resolved, unresolved);
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

    private async Task<IReadOnlyList<string>> FetchBotTeamIdsAsync(string serverUrl, string botToken, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, serverUrl, "/api/v4/users/me/teams", botToken);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(MapHttpError(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var teamIds = new List<string>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("id", out var idProp) && idProp.GetString() is { } id)
                teamIds.Add(id);
        }

        return teamIds;
    }

    private async Task<IReadOnlyList<ResolvedMattermostChannel>> FetchTeamChannelsAsync(string serverUrl, string botToken, string teamId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, serverUrl,
            $"/api/v4/users/me/teams/{Uri.EscapeDataString(teamId)}/channels", botToken);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return []; // a team whose channels we cannot list is skipped, not fatal

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var channels = new List<ResolvedMattermostChannel>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var displayName = element.TryGetProperty("display_name", out var displayProp) ? displayProp.GetString() : null;
            if (id is not null)
                channels.Add(new ResolvedMattermostChannel(id, name ?? id, displayName ?? name ?? id));
        }

        return channels;
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

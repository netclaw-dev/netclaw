// -----------------------------------------------------------------------
// <copyright file="DiscordProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Cli.Discord;

public sealed record DiscordProbeResult(
    bool Success,
    string? ErrorMessage,
    string? BotUsername);

public sealed record ResolvedDiscordChannel(
    string ChannelId,
    string ChannelName,
    string? GuildName)
{
    public string ToDisplayName() => GuildName is not null
        ? $"{GuildName} / #{ChannelName}"
        : $"#{ChannelName}";
}

public sealed record DiscordChannelResolutionResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<ResolvedDiscordChannel> Resolved,
    IReadOnlyList<string> Unresolved);

public interface IDiscordProbe
{
    Task<DiscordProbeResult> ProbeAsync(string botToken, CancellationToken ct = default);

    Task<DiscordChannelResolutionResult> ResolveChannelIdsAsync(
        string botToken, IReadOnlyList<string> channelIds, CancellationToken ct = default);
}

public sealed class DiscordProbe : IDiscordProbe
{
    private const string BaseUrl = "https://discord.com/api/v10";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public DiscordProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<DiscordProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/users/@me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                return new DiscordProbeResult(false, MapHttpError(response.StatusCode, errorBody), null);
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var username = root.TryGetProperty("username", out var userProp) ? userProp.GetString() : null;
            return new DiscordProbeResult(true, null, username);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DiscordProbeResult(false,
                "Connection timed out after 10 seconds. Check your network connection.", null);
        }
        catch (OperationCanceledException)
        {
            return new DiscordProbeResult(false, "Validation cancelled.", null);
        }
        catch (HttpRequestException ex)
        {
            return new DiscordProbeResult(false, $"Connection failed: {ex.Message}", null);
        }
    }

    // Accepts channel IDs (snowflakes) OR display names (#general). Enumerates the bot's guild text
    // channels once and matches each reference by id or by name, so operators can enter human-readable
    // names instead of snowflakes (the resolved id is what the runtime ACL matches).
    public async Task<DiscordChannelResolutionResult> ResolveChannelIdsAsync(
        string botToken, IReadOnlyList<string> channelRefs, CancellationToken ct = default)
    {
        var normalized = channelRefs
            .Select(reference => reference.Trim().TrimStart('#'))
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return new DiscordChannelResolutionResult(true, null, [], []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ResolveTimeout);

        try
        {
            var byId = new Dictionary<string, ResolvedDiscordChannel>(StringComparer.Ordinal);
            var byName = new Dictionary<string, ResolvedDiscordChannel>(StringComparer.OrdinalIgnoreCase);
            foreach (var (guildId, guildName) in await FetchBotGuildsAsync(botToken, timeoutCts.Token))
            {
                foreach (var (channelId, channelName) in await FetchGuildTextChannelsAsync(botToken, guildId, timeoutCts.Token))
                {
                    var channel = new ResolvedDiscordChannel(channelId, channelName, guildName);
                    byId[channelId] = channel;
                    // First match wins when a channel name is duplicated across guilds.
                    byName.TryAdd(channelName, channel);
                }
            }

            var resolved = new List<ResolvedDiscordChannel>();
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

            return new DiscordChannelResolutionResult(unresolved.Count == 0, null, resolved, unresolved);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DiscordChannelResolutionResult(false,
                "Channel resolution timed out after 30 seconds.", [], []);
        }
        catch (OperationCanceledException)
        {
            return new DiscordChannelResolutionResult(false,
                "Channel resolution cancelled.", [], []);
        }
        catch (HttpRequestException ex)
        {
            return new DiscordChannelResolutionResult(false,
                $"Channel resolution failed: {ex.Message}", [], []);
        }
    }

    private async Task<IReadOnlyList<(string Id, string Name)>> FetchBotGuildsAsync(string botToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/users/@me/guilds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(MapHttpError(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var guilds = new List<(string, string)>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (id is not null && name is not null)
                guilds.Add((id, name));
        }

        return guilds;
    }

    private async Task<IReadOnlyList<(string Id, string Name)>> FetchGuildTextChannelsAsync(string botToken, string guildId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/guilds/{guildId}/channels");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return []; // a guild whose channels we cannot list is skipped, not fatal

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var channels = new List<(string, string)>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            // Discord channel types: 0 = GUILD_TEXT, 5 = GUILD_ANNOUNCEMENT — both accept messages.
            var type = element.TryGetProperty("type", out var typeProp) && typeProp.TryGetInt32(out var t) ? t : -1;
            if (type != 0 && type != 5)
                continue;

            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (id is not null && name is not null)
                channels.Add((id, name));
        }

        return channels;
    }

    private static string MapHttpError(System.Net.HttpStatusCode statusCode, string body)
    {
        var code = TryExtractDiscordErrorCode(body);
        return (statusCode, code) switch
        {
            (System.Net.HttpStatusCode.Unauthorized, _) =>
                "Bot token is invalid. Check your Discord application's bot token.",
            (System.Net.HttpStatusCode.Forbidden, 50001) =>
                "Bot lacks access. Ensure it has been invited to the server.",
            (System.Net.HttpStatusCode.Forbidden, _) =>
                "Access denied. Check bot permissions.",
            (System.Net.HttpStatusCode.NotFound, _) =>
                "Resource not found. Check the ID is correct.",
            (System.Net.HttpStatusCode.TooManyRequests, _) =>
                "Rate limited by Discord API. Try again in a few seconds.",
            _ => $"Discord API error: {(int)statusCode} {statusCode}"
        };
    }

    private static int? TryExtractDiscordErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var codeProp) &&
                codeProp.TryGetInt32(out var code))
                return code;
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }
}

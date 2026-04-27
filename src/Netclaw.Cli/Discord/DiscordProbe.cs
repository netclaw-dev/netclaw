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

    public async Task<DiscordChannelResolutionResult> ResolveChannelIdsAsync(
        string botToken, IReadOnlyList<string> channelIds, CancellationToken ct = default)
    {
        var normalized = channelIds
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return new DiscordChannelResolutionResult(true, null, [], []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ResolveTimeout);

        try
        {
            var channelTasks = normalized.Select(id =>
                FetchChannelAsync(botToken, id, timeoutCts.Token));
            var channelResults = await Task.WhenAll(channelTasks);

            var channelPairs = normalized.Zip(channelResults).ToList();

            var uniqueGuildIds = channelPairs
                .Where(p => p.Second is not null && p.Second.Value.GuildId is not null)
                .Select(p => p.Second!.Value.GuildId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var guildTasks = uniqueGuildIds.Select(async gid =>
                (GuildId: gid, Name: await FetchGuildNameAsync(botToken, gid, timeoutCts.Token)));
            var guildResults = await Task.WhenAll(guildTasks);
            var guildNames = guildResults
                .Where(g => g.Name is not null)
                .ToDictionary(g => g.GuildId, g => g.Name!, StringComparer.Ordinal);

            var resolved = new List<ResolvedDiscordChannel>();
            var unresolved = new List<string>();

            foreach (var (channelId, channelInfo) in channelPairs)
            {
                if (channelInfo is null)
                {
                    unresolved.Add(channelId);
                    continue;
                }

                var (channelName, guildId) = channelInfo.Value;
                guildNames.TryGetValue(guildId ?? "", out var guildName);
                resolved.Add(new ResolvedDiscordChannel(channelId, channelName, guildName));
            }

            return new DiscordChannelResolutionResult(
                unresolved.Count == 0, null, resolved, unresolved);
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

    private async Task<(string Name, string? GuildId)?> FetchChannelAsync(
        string botToken, string channelId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/channels/{channelId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        var guildId = root.TryGetProperty("guild_id", out var guildProp) ? guildProp.GetString() : null;

        return name is not null ? (name, guildId) : null;
    }

    private async Task<string?> FetchGuildNameAsync(
        string botToken, string guildId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/guilds/{guildId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
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

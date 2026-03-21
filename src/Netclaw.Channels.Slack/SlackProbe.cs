using System.Net.Http.Headers;
using System.Text.Json;

namespace Netclaw.Channels.Slack;

/// <summary>
/// Result of probing the Slack <c>auth.test</c> API.
/// </summary>
public sealed record SlackProbeResult(
    bool Success,
    string? ErrorMessage,
    string? TeamName,
    SlackUserId? BotUserId);

/// <summary>
/// A Slack channel resolved from a user-provided name to its API ID.
/// </summary>
public sealed record ResolvedSlackChannel(string Name, string Id);

/// <summary>
/// Result of resolving user-provided channel names to Slack channel IDs
/// via the <c>conversations.list</c> API.
/// </summary>
public sealed record SlackChannelResolutionResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<ResolvedSlackChannel> Resolved,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Probes Slack's <c>auth.test</c> endpoint to validate a bot token.
/// </summary>
public interface ISlackProbe
{
    Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default);

    /// <summary>
    /// Resolves user-provided channel names to Slack channel IDs via <c>conversations.list</c>.
    /// </summary>
    Task<SlackChannelResolutionResult> ResolveChannelNamesAsync(
        string botToken, IReadOnlyList<string> channelNames, CancellationToken ct = default);
}

/// <summary>
/// Production implementation that calls <c>https://slack.com/api/auth.test</c>.
/// </summary>
public sealed class SlackProbe : ISlackProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChannelResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public SlackProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (ok)
            {
                var team = root.TryGetProperty("team", out var teamProp) ? teamProp.GetString() : null;
                var userId = root.TryGetProperty("user_id", out var userProp) ? userProp.GetString() : null;
                return new SlackProbeResult(true, null, team,
                    userId is not null ? new SlackUserId(userId) : null);
            }

            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown_error";
            return new SlackProbeResult(false, MapSlackError(error), null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SlackProbeResult(false,
                "Connection timed out after 10 seconds. Check your network connection.", null, null);
        }
        catch (OperationCanceledException)
        {
            return new SlackProbeResult(false, "Validation cancelled.", null, null);
        }
        catch (HttpRequestException ex)
        {
            return new SlackProbeResult(false, $"Connection failed: {ex.Message}", null, null);
        }
    }

    public async Task<SlackChannelResolutionResult> ResolveChannelNamesAsync(
        string botToken, IReadOnlyList<string> channelNames, CancellationToken ct = default)
    {
        // Normalize input: strip # prefix, trim, deduplicate (case-insensitive)
        var normalized = channelNames
            .Select(n => n.TrimStart('#').Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            return new SlackChannelResolutionResult(true, null, [], []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ChannelResolveTimeout);

        var remaining = new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedSlackChannel>();
        string? cursor = null;

        try
        {
            do
            {
                var url = "https://slack.com/api/conversations.list?types=public_channel,private_channel&exclude_archived=true&limit=200";
                if (!string.IsNullOrEmpty(cursor))
                    url += $"&cursor={Uri.EscapeDataString(cursor)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);

                using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                if (!ok)
                {
                    var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown_error";
                    return new SlackChannelResolutionResult(false, MapSlackError(error), resolved, remaining.ToList());
                }

                if (root.TryGetProperty("channels", out var channels))
                {
                    foreach (var channel in channels.EnumerateArray())
                    {
                        var name = channel.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        var nameNormalized = channel.TryGetProperty("name_normalized", out var normProp) ? normProp.GetString() : null;
                        var id = channel.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                        if (id is null) continue;

                        // Check if this channel matches any remaining name (case-insensitive)
                        string? matchedInput = null;
                        foreach (var input in remaining)
                        {
                            if (string.Equals(input, name, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(input, nameNormalized, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedInput = input;
                                break;
                            }
                        }

                        if (matchedInput is not null)
                        {
                            resolved.Add(new ResolvedSlackChannel(matchedInput, id));
                            remaining.Remove(matchedInput);
                        }
                    }
                }

                // Early exit when all names resolved
                if (remaining.Count == 0)
                    break;

                // Pagination
                cursor = null;
                if (root.TryGetProperty("response_metadata", out var meta) &&
                    meta.TryGetProperty("next_cursor", out var cursorProp))
                {
                    var nextCursor = cursorProp.GetString();
                    if (!string.IsNullOrEmpty(nextCursor))
                        cursor = nextCursor;
                }
            } while (cursor is not null);

            return new SlackChannelResolutionResult(
                remaining.Count == 0, null, resolved, remaining.ToList());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SlackChannelResolutionResult(false,
                "Channel lookup timed out after 30 seconds.", resolved, remaining.ToList());
        }
        catch (OperationCanceledException)
        {
            return new SlackChannelResolutionResult(false, "Channel lookup cancelled.", resolved, remaining.ToList());
        }
        catch (HttpRequestException ex)
        {
            return new SlackChannelResolutionResult(false,
                $"Channel lookup failed: {ex.Message}", resolved, remaining.ToList());
        }
    }

    private static string MapSlackError(string? errorCode) => errorCode switch
    {
        "invalid_auth" => "Bot token is invalid. Check your Slack app's Bot User OAuth Token.",
        "account_inactive" => "Bot account is deactivated.",
        "token_revoked" => "Bot token has been revoked. Generate a new one.",
        "token_expired" => "Bot token has expired. Generate a new one.",
        "not_authed" => "No authentication token provided.",
        "missing_scope" => "Bot token lacks channels:read scope.",
        _ => $"Slack API error: {errorCode}"
    };
}

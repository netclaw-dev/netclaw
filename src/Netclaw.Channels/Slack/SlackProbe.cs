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
    string? BotUserId);

/// <summary>
/// Probes Slack's <c>auth.test</c> endpoint to validate a bot token.
/// </summary>
public interface ISlackProbe
{
    Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default);
}

/// <summary>
/// Production implementation that calls <c>https://slack.com/api/auth.test</c>.
/// </summary>
public sealed class SlackProbe : ISlackProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

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
                return new SlackProbeResult(true, null, team, userId);
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

    private static string MapSlackError(string? errorCode) => errorCode switch
    {
        "invalid_auth" => "Bot token is invalid. Check your Slack app's Bot User OAuth Token.",
        "account_inactive" => "Bot account is deactivated.",
        "token_revoked" => "Bot token has been revoked. Generate a new one.",
        "token_expired" => "Bot token has expired. Generate a new one.",
        "not_authed" => "No authentication token provided.",
        _ => $"Slack API error: {errorCode}"
    };
}

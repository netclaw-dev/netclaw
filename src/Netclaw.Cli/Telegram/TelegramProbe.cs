// -----------------------------------------------------------------------
// <copyright file="TelegramProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Cli.Telegram;

public sealed record TelegramProbeResult(bool Success, string? ErrorMessage, string? BotUsername);

public sealed record ResolvedTelegramChat(string ChatId, string DisplayName);

public sealed record TelegramChatResolutionResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<ResolvedTelegramChat> Resolved,
    IReadOnlyList<string> Unresolved);

public interface ITelegramProbe
{
    Task<TelegramProbeResult> ProbeAsync(string botToken, CancellationToken ct = default);

    Task<TelegramChatResolutionResult> ResolveChatIdsAsync(
        string botToken,
        IReadOnlyList<string> chatIds,
        CancellationToken ct = default);
}

public sealed class TelegramProbe(HttpClient httpClient) : ITelegramProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<TelegramProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await httpClient.GetAsync(ApiUrl(botToken, "getMe"), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new TelegramProbeResult(false, TelegramError(response.StatusCode), null);

            using var document = JsonDocument.Parse(body);
            var result = document.RootElement.GetProperty("result");
            var username = result.TryGetProperty("username", out var value) ? value.GetString() : null;
            return new TelegramProbeResult(true, null, username);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TelegramProbeResult(false, "Connection timed out after 10 seconds.", null);
        }
        catch (HttpRequestException ex)
        {
            return new TelegramProbeResult(false, $"Connection failed: {ex.Message}", null);
        }
        catch (JsonException)
        {
            return new TelegramProbeResult(false, "Telegram returned an invalid response.", null);
        }
    }

    public async Task<TelegramChatResolutionResult> ResolveChatIdsAsync(
        string botToken,
        IReadOnlyList<string> chatIds,
        CancellationToken ct = default)
    {
        var resolved = new List<ResolvedTelegramChat>();
        var unresolved = new List<string>();
        try
        {
            foreach (var chatId in chatIds.Distinct(StringComparer.Ordinal))
            {
                if (!long.TryParse(chatId, out var numericId) || numericId == 0)
                {
                    unresolved.Add(chatId);
                    continue;
                }

                using var response = await httpClient.GetAsync(
                    $"{ApiUrl(botToken, "getChat")}?chat_id={Uri.EscapeDataString(chatId)}", ct);
                if (!response.IsSuccessStatusCode)
                {
                    unresolved.Add(chatId);
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var result = document.RootElement.GetProperty("result");
                var type = result.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                if (string.Equals(type, "private", StringComparison.Ordinal))
                {
                    unresolved.Add(chatId);
                    continue;
                }

                var canonicalId = result.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var title = result.TryGetProperty("title", out var titleValue)
                    ? titleValue.GetString()
                    : result.TryGetProperty("username", out var usernameValue)
                        ? $"@{usernameValue.GetString()}"
                        : canonicalId;
                resolved.Add(new ResolvedTelegramChat(canonicalId, title ?? canonicalId));
            }

            return new TelegramChatResolutionResult(unresolved.Count == 0, null, resolved, unresolved);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TelegramChatResolutionResult(false, "Connection timed out after 10 seconds.", resolved, unresolved);
        }
        catch (HttpRequestException ex)
        {
            return new TelegramChatResolutionResult(false, $"Connection failed: {ex.Message}", resolved, unresolved);
        }
        catch (JsonException)
        {
            return new TelegramChatResolutionResult(false, "Telegram returned an invalid response.", resolved, unresolved);
        }
    }

    private static string ApiUrl(string token, string method) => $"https://api.telegram.org/bot{token}/{method}";

    private static string TelegramError(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized => "Bot token is invalid.",
        System.Net.HttpStatusCode.TooManyRequests => "Telegram rate limited the request.",
        _ => $"Telegram API error: {(int)status} {status}"
    };
}

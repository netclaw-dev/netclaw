using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

internal static class SessionSidecarRunner
{
    public static async Task<T?> RunJsonAsync<T>(
        IChatClient client,
        string systemPrompt,
        string userPrompt,
        TimeSpan timeout,
        Action<string> logWarning)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var messages = new List<ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, systemPrompt),
                new(Microsoft.Extensions.AI.ChatRole.User, userPrompt)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                logWarning("Sidecar returned empty response");
                return default;
            }

            return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            logWarning($"Sidecar failed: {ex.Message}");
            return default;
        }
    }
}

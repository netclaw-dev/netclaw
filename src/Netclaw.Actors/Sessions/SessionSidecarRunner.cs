using System.Text.Json;
using System.Text.Json.Nodes;
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

            var normalized = NormalizeJsonPayload<T>(text);

            return JsonSerializer.Deserialize<T>(normalized, new JsonSerializerOptions
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

    private static string NormalizeJsonPayload<T>(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
                var fence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0)
                    text = text[..fence];
            }
        }

        text = text.Trim();

        if (typeof(T) == typeof(IReadOnlyList<MemoryProposal>) || typeof(T) == typeof(List<MemoryProposal>))
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "proposals", "items", "memories" })
                {
                    if (obj[key] is JsonArray arr)
                        return arr.ToJsonString();
                }
            }
        }

        if (typeof(T) == typeof(RecallQueryPlan))
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "plan", "queryPlan", "recallPlan" })
                {
                    if (obj[key] is JsonObject inner)
                        return inner.ToJsonString();
                }
            }
        }

        return text;
    }
}

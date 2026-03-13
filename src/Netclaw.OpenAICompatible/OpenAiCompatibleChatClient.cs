using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Netclaw.OpenAICompatible;

public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleEndpoint _endpoint;
    private readonly string _modelId;
    public OpenAiCompatibleChatClient(HttpClient httpClient, OpenAiCompatibleEndpoint endpoint, string modelId)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _modelId = modelId;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: false);
        using var request = BuildRequest(payload);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, payload, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseChatResponse(document.RootElement);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: true);
        using var request = BuildRequest(payload);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, payload, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pendingToolCalls = new Dictionary<int, PendingToolCall>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var ssePayload = line[5..].Trim();
            if (ssePayload == "[DONE]")
                yield break;

            using var document = JsonDocument.Parse(ssePayload);
            foreach (var update in ParseStreamingUpdates(document.RootElement, pendingToolCalls))
                yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private Dictionary<string, object?> BuildPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var toolList = options?.Tools?.ToList();

        var body = new Dictionary<string, object?>
        {
            ["model"] = options?.ModelId ?? _modelId,
            ["messages"] = NormalizeMessages(messages).ToArray(),
            ["stream"] = stream
        };

        if (options?.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (options?.TopP is { } topP)
            body["top_p"] = topP;
        if (options?.TopK is { } topK)
            body["top_k"] = topK;
        if (options?.MaxOutputTokens is { } maxTokens)
            body["max_tokens"] = maxTokens;
        if (options?.StopSequences is { Count: > 0 } stop)
            body["stop"] = stop;
        if (toolList is { Count: > 0 })
            body["tools"] = toolList.Select(ToTool).ToArray();

        return body;
    }

    private static IEnumerable<object> NormalizeMessages(IEnumerable<ChatMessage> messages)
    {
        var normalized = new List<object>();
        var systemSegments = new List<string>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                    systemSegments.Add(message.Text);

                continue;
            }

            normalized.Add(ToMessage(message));
        }

        if (systemSegments.Count > 0)
        {
            normalized.Insert(0, new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = string.Join("\n\n", systemSegments)
            });
        }

        return normalized;
    }

    private HttpRequestMessage BuildRequest(Dictionary<string, object?> payload)
    {
        var serializedPayload = JsonSerializer.Serialize(payload, JsonOptions);

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.ChatCompletionsPath)
        {
            Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken);

        throw new HttpRequestException(
            $"OpenAI-compatible request failed: status={(int)response.StatusCode} path={_endpoint.ChatCompletionsPath} payload={JsonSerializer.Serialize(payload, JsonOptions)} response={responseBody}");
    }

    private static object ToMessage(ChatMessage message)
    {
        var text = message.Text;
        return new Dictionary<string, object?>
        {
            ["role"] = ToRole(message.Role),
            ["content"] = text
        };
    }

    private static string ToRole(ChatRole? role)
        => role switch
        {
            null => "user",
            _ when role == ChatRole.System => "system",
            _ when role == ChatRole.Assistant => "assistant",
            _ when role == ChatRole.Tool => "tool",
            _ => "user"
        };

    private static object ToTool(AITool tool)
    {
        var schemaProperty = tool.GetType().GetProperty("JsonSchema");
        var schema = schemaProperty?.GetValue(tool) is JsonElement jsonSchema
            ? JsonSerializer.Deserialize<object>(jsonSchema.GetRawText(), JsonOptions)
            : new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>()
            };

        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = schema
            }
        };
    }

    private static ChatResponse ParseChatResponse(JsonElement root)
    {
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var contents = new List<AIContent>();

        if (message.TryGetProperty("reasoning_content", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            contents.Add(new TextReasoningContent(reasoning.GetString()!));
        }

        if (message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            contents.Add(new TextContent(content.GetString()!));
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = root.TryGetProperty("model", out var model) ? model.GetString() : null,
            ResponseId = root.TryGetProperty("id", out var id) ? id.GetString() : null,
            FinishReason = ParseFinishReason(choice)
        };
    }

    private static IEnumerable<ChatResponseUpdate> ParseStreamingUpdates(JsonElement root, Dictionary<int, PendingToolCall> pendingToolCalls)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            yield break;

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta))
            yield break;

        var contents = new List<AIContent>();

        if (delta.TryGetProperty("reasoning_content", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            contents.Add(new TextReasoningContent(reasoning.GetString()!));
        }

        if (delta.TryGetProperty("content", out var text)
            && text.ValueKind == JsonValueKind.String
            && text.GetString() is { Length: > 0 } value)
        {
            contents.Add(new TextContent(value));
        }

        if (delta.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var index = toolCall.TryGetProperty("index", out var indexElement)
                    && indexElement.ValueKind == JsonValueKind.Number
                    ? indexElement.GetInt32()
                    : pendingToolCalls.Count;

                if (!pendingToolCalls.TryGetValue(index, out var pending))
                {
                    pending = new PendingToolCall();
                    pendingToolCalls[index] = pending;
                }

                if (toolCall.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    pending.Id = id.GetString();
                }

                if (!toolCall.TryGetProperty("function", out var function)
                    || function.ValueKind != JsonValueKind.Object)
                    continue;

                if (function.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    pending.Name = name.GetString();
                }

                if (function.TryGetProperty("arguments", out var arguments)
                    && arguments.ValueKind == JsonValueKind.String
                    && arguments.GetString() is { Length: > 0 } argumentsChunk)
                {
                    pending.Arguments.Append(argumentsChunk);
                }
            }
        }

        var finishReason = ParseFinishReason(choice);
        if (finishReason == ChatFinishReason.ToolCalls && pendingToolCalls.Count > 0)
        {
            foreach (var pending in pendingToolCalls.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
            {
                contents.Add(new FunctionCallContent(
                    pending.Id ?? Guid.NewGuid().ToString("N"),
                    pending.Name ?? string.Empty,
                    TryDeserializeArguments(pending.Arguments.ToString())));
            }

            pendingToolCalls.Clear();
        }

        if (contents.Count == 0 && finishReason is null)
            yield break;

        yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ModelId = root.TryGetProperty("model", out var model) ? model.GetString() : null,
            ResponseId = root.TryGetProperty("id", out var responseId) ? responseId.GetString() : null,
            FinishReason = finishReason
        };
    }

    private static Dictionary<string, object?>? TryDeserializeArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatFinishReason? ParseFinishReason(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var finishReason)
            || finishReason.ValueKind != JsonValueKind.String)
            return null;

        return finishReason.GetString() switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "tool_calls" => ChatFinishReason.ToolCalls,
            _ => null
        };
    }

    private sealed class PendingToolCall
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}

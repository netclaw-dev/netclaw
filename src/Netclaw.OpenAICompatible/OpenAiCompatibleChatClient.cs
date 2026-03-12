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
        using var request = BuildRequest(messages, options, stream: false);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseChatResponse(document.RootElement);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(messages, options, stream: true);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].Trim();
            if (payload == "[DONE]")
                yield break;

            using var document = JsonDocument.Parse(payload);
            foreach (var update in ParseStreamingUpdates(document.RootElement))
                yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private HttpRequestMessage BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options?.ModelId ?? _modelId,
            ["messages"] = messages.Select(ToMessage).ToArray(),
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
        if (options?.Tools is { Count: > 0 } tools)
            body["tools"] = tools.Select(ToTool).ToArray();

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.ChatCompletionsPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);

        return request;
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

    private static IEnumerable<ChatResponseUpdate> ParseStreamingUpdates(JsonElement root)
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
                var callId = toolCall.TryGetProperty("id", out var id) ? id.GetString() : null;
                var function = toolCall.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var argumentsJson = function.TryGetProperty("arguments", out var arguments)
                    ? arguments.GetString()
                    : null;

                var parsedArgs = string.IsNullOrWhiteSpace(argumentsJson)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson!, JsonOptions);

                contents.Add(new FunctionCallContent(callId ?? Guid.NewGuid().ToString("N"), name, parsedArgs));
            }
        }

        if (contents.Count == 0 && !choice.TryGetProperty("finish_reason", out _))
            yield break;

        yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ModelId = root.TryGetProperty("model", out var model) ? model.GetString() : null,
            ResponseId = root.TryGetProperty("id", out var responseId) ? responseId.GetString() : null,
            FinishReason = ParseFinishReason(choice)
        };
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
}

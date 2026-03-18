using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// IChatClient implementation for the OpenAI Codex backend at
/// <c>chatgpt.com/backend-api/codex/responses</c>.
/// </summary>
/// <remarks>
/// This backend is completely separate from <c>api.openai.com</c>.
/// OAuth tokens issued by the Codex CLI client cannot call api.openai.com at all.
/// The Codex backend requires:
/// <list type="bullet">
///   <item><c>Authorization: Bearer {token}</c></item>
///   <item><c>ChatGPT-Account-Id</c> header (extracted from JWT)</item>
///   <item><c>"store": false</c> in the request body</item>
/// </list>
/// Reference: https://github.com/anomalyco/opencode (plugin/codex.ts)
/// </remarks>
public sealed class OpenAiCodexChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly SensitiveString _accessToken;
    private readonly string? _accountId;

    private const string CodexResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";

    public OpenAiCodexChatClient(HttpClient httpClient, string model, SensitiveString accessToken)
    {
        _httpClient = httpClient;
        _model = model;
        _accessToken = accessToken;
        _accountId = JwtAccountIdExtractor.Extract(accessToken.Value);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, options, stream: false);
        using var request = CreateRequest(requestBody);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(messages, options, stream: true);
        using var request = CreateRequest(requestBody);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line.AsSpan(6);
            if (data is "[DONE]") yield break;

            using var doc = JsonDocument.Parse(data.ToString());
            var root = doc.RootElement;

            // Extract text delta from response.output[].content[].text
            if (root.TryGetProperty("type", out var type) &&
                type.GetString() == "response.output_text.delta" &&
                root.TryGetProperty("delta", out var delta) &&
                delta.ValueKind == JsonValueKind.String)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new TextContent(delta.GetString() ?? string.Empty)]);
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private const string ProviderLabel = "OpenAI Codex";

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken);

        var statusCode = (int)response.StatusCode;
        var technicalMessage =
            $"OpenAI Codex request failed: status={statusCode} endpoint={CodexResponsesEndpoint} response={responseBody}";
        var userMessage = ExtractUserMessage(responseBody, statusCode);

        throw new ProviderException(userMessage, technicalMessage, statusCode);
    }

    internal static string ExtractUserMessage(string? responseBody, int statusCode)
    {
        return ProviderErrorHelper.ExtractUserMessage(responseBody, statusCode, ProviderLabel,
            code => code == 401
                ? "OpenAI Codex token is expired or invalid. Re-authenticate with 'netclaw provider fix <name>'."
                : null);
    }

    private HttpRequestMessage CreateRequest(string jsonBody)
    {
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        // Codex backend rejects "application/json; charset=utf-8" — strip the charset
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, CodexResponsesEndpoint)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken.Value);
        if (_accountId is not null)
            request.Headers.Add("ChatGPT-Account-Id", _accountId);
        return request;
    }

    private string BuildRequestBody(
        IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["store"] = false,
            ["stream"] = stream,
        };

        // Responses API: system messages go in top-level "instructions",
        // everything else goes in "input" array
        var input = new JsonArray();
        string? instructions = null;
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                // Concatenate multiple system messages (rare but possible)
                instructions = instructions is null
                    ? msg.Text ?? string.Empty
                    : $"{instructions}\n{msg.Text}";
                continue;
            }

            var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
            var item = new JsonObject
            {
                ["role"] = role,
                ["content"] = msg.Text ?? string.Empty,
            };
            input.Add(item);
        }

        if (instructions is not null)
            body["instructions"] = instructions;

        body["input"] = input;

        if (options?.MaxOutputTokens is > 0)
            body["max_output_tokens"] = options.MaxOutputTokens;

        if (options?.Temperature is not null)
            body["temperature"] = (decimal)options.Temperature.Value;

        return body.ToJsonString();
    }

    private static ChatResponse ParseResponse(JsonElement root)
    {
        var text = new StringBuilder();

        // Responses API format: output[].content[].text
        if (root.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var content))
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t))
                            text.Append(t.GetString());
                    }
                }
            }
        }

        var message = new ChatMessage(ChatRole.Assistant, text.ToString());
        return new ChatResponse(message);
    }
}

// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Netclaw.Providers.SelfHosted;

public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleEndpoint _endpoint;
    private readonly string _modelId;
    private readonly ILogger _logger;

    public OpenAiCompatibleChatClient(HttpClient httpClient, OpenAiCompatibleEndpoint endpoint, string modelId,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _modelId = modelId;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: false);
        using var request = BuildRequest(payload);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
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

        // Wall-clock prompt_ms fallback for backends that don't emit
        // server-side timings — locked at first content delta, includes
        // network RTT.
        var requestSentAt = System.Diagnostics.Stopwatch.GetTimestamp();
        double? wallClockPromptMs = null;

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, payload, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pendingToolCalls = new Dictionary<int, PendingToolCall>();
        var accumulatedText = new StringBuilder();
        var hadStructuredToolCalls = false;
        ChatResponseUpdate? finalUpdate = null;
        var filter = new ToolCallTextFilter();
        int textDeltaCount = 0, textDeltaChars = 0;
        int thinkingDeltaCount = 0, thinkingDeltaChars = 0;
        int toolCallDeltaCount = 0, suppressedDeltaCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                // SSE comment lines (`:` prefix) and event-type lines — yield keepalive
                // so the watchdog knows the connection is alive during prefill/queuing.
                yield return KeepaliveUpdate;
                continue;
            }

            var ssePayload = line[5..].Trim();
            if (ssePayload == "[DONE]")
                break;

            using var document = JsonDocument.Parse(ssePayload);
            foreach (var update in ParseStreamingUpdates(document.RootElement, pendingToolCalls, wallClockPromptMs))
            {
                if (update.Contents.Count > 0)
                    wallClockPromptMs ??=
                        System.Diagnostics.Stopwatch.GetElapsedTime(requestSentAt).TotalMilliseconds;

                var suppressThisUpdate = false;
                foreach (var item in update.Contents)
                {
                    switch (item)
                    {
                        case TextContent tc:
                            accumulatedText.Append(tc.Text);
                            textDeltaCount++;
                            textDeltaChars += tc.Text?.Length ?? 0;
                            if (filter.ShouldSuppress(tc.Text))
                                suppressThisUpdate = true;
                            break;
                        case TextReasoningContent rc:
                            thinkingDeltaCount++;
                            thinkingDeltaChars += rc.Text?.Length ?? 0;
                            break;
                        case FunctionCallContent:
                            hadStructuredToolCalls = true;
                            toolCallDeltaCount++;
                            break;
                    }
                }

                if (update.FinishReason is not null)
                    finalUpdate = update;

                // Suppress text updates that contain tool call XML, but yield a
                // content-free keepalive so the caller knows the stream is alive.
                if (suppressThisUpdate)
                {
                    suppressedDeltaCount++;
                    yield return KeepaliveUpdate;
                    continue;
                }

                yield return update;
            }
        }

        _logger.LogDebug(
            "SSE stream content breakdown: textDeltas={TextDeltas} textChars={TextChars} thinkingDeltas={ThinkingDeltas} thinkingChars={ThinkingChars} toolCallDeltas={ToolCallDeltas} suppressedDeltas={SuppressedDeltas} finishReason={FinishReason}",
            textDeltaCount, textDeltaChars, thinkingDeltaCount, thinkingDeltaChars, toolCallDeltaCount,
            suppressedDeltaCount, finalUpdate?.FinishReason?.ToString() ?? "null");

        // Fallback: if the model stopped without structured tool calls but the text
        // contains XML-like tool call blocks, emit a synthetic tool call update.
        if (!hadStructuredToolCalls
            && finalUpdate?.FinishReason != ChatFinishReason.ToolCalls
            && accumulatedText.Length > 0
            && filter.IsActive)
        {
            var textToolCalls = TextToolCallParser.ExtractFromText(accumulatedText.ToString());
            if (textToolCalls.Count > 0)
            {
                // Emit any cleaned non-tool-call text that was suppressed
                var cleaned = ToolCallTextFilter.GetCleanedText(accumulatedText);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(cleaned)]);
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, [.. textToolCalls.Cast<AIContent>()])
                {
                    FinishReason = ChatFinishReason.ToolCalls
                };
            }
        }
        // Original fallback for non-filtered text tool calls
        else if (!hadStructuredToolCalls
            && finalUpdate?.FinishReason != ChatFinishReason.ToolCalls
            && accumulatedText.Length > 0
            && !filter.IsActive)
        {
            var textToolCalls = TextToolCallParser.ExtractFromText(accumulatedText.ToString());
            if (textToolCalls.Count > 0)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [.. textToolCalls.Cast<AIContent>()])
                {
                    FinishReason = ChatFinishReason.ToolCalls
                };
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private JsonObject BuildPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? _modelId,
            ["messages"] = NormalizeMessages(messages, _logger),
            ["stream"] = stream
        };

        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
            // llama-server sends prefill progress as SSE data events when enabled.
            // Harmless on servers that don't support it (unknown fields are ignored).
            body["return_progress"] = true;
        }

        if (options?.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (options?.TopP is { } topP)
            body["top_p"] = topP;
        if (options?.TopK is { } topK)
            body["top_k"] = topK;
        if (options?.MaxOutputTokens is { } maxTokens)
            body["max_tokens"] = maxTokens;
        if (options?.StopSequences is { Count: > 0 } stop)
            body["stop"] = new JsonArray(stop.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        if (options?.Tools is { Count: > 0 } tools)
            body["tools"] = new JsonArray(tools.Select(ToTool).ToArray<JsonNode>());

        // Pass through additional properties as top-level JSON fields.
        // Enables provider-specific options like chat_template_kwargs for llama.cpp.
        if (options?.AdditionalProperties is { Count: > 0 } additional)
        {
            foreach (var (key, value) in additional)
            {
                body[key] = value is not null
                    ? JsonSerializer.SerializeToNode(value, JsonOptions)
                    : null;
            }
        }

        LogPrefixDiagnostic(body);

        return body;
    }

    /// <summary>
    /// Emits per-turn byte hashes for the static portion of the outbound LLM
    /// request so KV cache prefix drift can be diagnosed from the daemon log.
    /// PR #1171 fixed the largest source of drift (volatile content merging
    /// into the leading system message); PR #1176 moved the volatile tail
    /// inside the last User message's <c>&lt;context&gt;</c> wrapper. The
    /// diagnostic emits SHA-256 hashes of three independent regions so a
    /// log-line diff between consecutive turns isolates which region drifted.
    /// Post-#1176 the trailing message is normally the wrapped User turn (or
    /// a Tool result mid-loop), never a System tail — <c>tail_is_system</c>
    /// stays false on the new wire format and is kept as a defensive signal
    /// in case an upstream regression re-emits a trailing System.
    /// </summary>
    private void LogPrefixDiagnostic(JsonObject body)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        var messages = body["messages"] as JsonArray;
        if (messages is null || messages.Count == 0)
            return;

        // Leading system: messages[0] when role=system. Hash the content
        // string only — the {role,content} envelope is invariant, so any
        // drift here means the persisted prompt content itself changed.
        var systemPrefixHash = "(none)";
        var systemPrefixChars = 0;
        if (messages[0] is JsonObject first
            && first["role"]?.GetValue<string>() == "system")
        {
            var content = first["content"]?.GetValue<string>() ?? string.Empty;
            systemPrefixChars = content.Length;
            systemPrefixHash = ComputeShortHash(content);
        }

        // Defensive signal: post-#1176 the assembler does NOT emit a
        // trailing System tail (volatile content goes inside the last User
        // message). If this flips to true an upstream regression re-emitted
        // a trailing System — that's exactly the regression #1176 fixed.
        var tailIsSystem = messages.Count > 1
            && messages[^1] is JsonObject last
            && last["role"]?.GetValue<string>() == "system";

        // History prefix: everything between the leading system and the new
        // user turn (and excluding any trailing System tail if one slipped
        // through). This is the region that should be byte-stable across
        // consecutive turns; if it isn't, either history was rewritten
        // mid-session or a message was re-serialized differently.
        var historyEnd = tailIsSystem ? messages.Count - 1 : messages.Count;
        var historyStart = systemPrefixHash == "(none)" ? 0 : 1;
        if (historyEnd > historyStart
            && messages[historyEnd - 1] is JsonObject newTurn
            && newTurn["role"]?.GetValue<string>() == "user")
        {
            historyEnd -= 1;
        }

        var historyPrefixHash = "(none)";
        var historyMsgCount = 0;
        if (historyEnd > historyStart)
        {
            var sb = new StringBuilder();
            for (var i = historyStart; i < historyEnd; i++)
                sb.Append(messages[i]!.ToJsonString(JsonOptions));
            historyPrefixHash = ComputeShortHash(sb.ToString());
            historyMsgCount = historyEnd - historyStart;
        }

        var toolsHash = "(none)";
        var toolsCount = 0;
        if (body["tools"] is JsonArray toolsArray && toolsArray.Count > 0)
        {
            toolsHash = ComputeShortHash(toolsArray.ToJsonString(JsonOptions));
            toolsCount = toolsArray.Count;
        }

        _logger.LogDebug(
            "kv_prefix_diagnostic system_prefix_hash={SystemPrefixHash} system_prefix_chars={SystemPrefixChars} history_prefix_hash={HistoryPrefixHash} history_msg_count={HistoryMsgCount} tail_is_system={TailIsSystem} tools_hash={ToolsHash} tools_count={ToolsCount} total_messages={TotalMessages}",
            systemPrefixHash, systemPrefixChars, historyPrefixHash, historyMsgCount, tailIsSystem, toolsHash, toolsCount, messages.Count);
    }

    private static string ComputeShortHash(string content)
    {
        // First 8 bytes (16 hex chars) is plenty to detect drift between
        // consecutive turns; full SHA-256 would spam logs without adding
        // diagnostic value at this scale.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static JsonArray NormalizeMessages(IEnumerable<ChatMessage> messages, ILogger? logger = null)
    {
        var normalized = new JsonArray();
        var all = messages.ToList();

        // All System-role messages are merged into a single leading
        // system message. Strict OpenAI-compatible servers (vLLM with
        // Qwen/Llama chat templates among them) reject non-leading
        // System messages with HTTP 400 "System message must be at the
        // beginning." The session assembler is the source of truth and
        // is expected to keep volatile per-turn context inside the last
        // User-role message (wrapped in <context> tags), never as a
        // trailing System. If a non-leading System slips through here
        // it's a programming error upstream — we merge it into the
        // leading prefix defensively so the request still reaches the
        // provider, but we log Warning so the upstream bug surfaces.
        var leadingSegments = new List<string>();
        var passThrough = new List<ChatMessage>(all.Count);
        var sawNonLeadingSystem = false;
        var stillLeading = true;

        foreach (var message in all)
        {
            if (message.Role == ChatRole.System)
            {
                if (stillLeading)
                {
                    if (!string.IsNullOrWhiteSpace(message.Text))
                        leadingSegments.Add(message.Text);
                }
                else
                {
                    sawNonLeadingSystem = true;
                    if (!string.IsNullOrWhiteSpace(message.Text))
                        leadingSegments.Add(message.Text);
                }
            }
            else
            {
                stillLeading = false;
                passThrough.Add(message);
            }
        }

        if (sawNonLeadingSystem)
        {
            logger?.LogWarning(
                "NormalizeMessages received a non-leading System-role message — merging into the leading system prefix defensively. "
                + "This indicates an upstream assembler bug: volatile per-turn context should be wrapped inside the last User-role message (<context>...</context>), not emitted as a separate System message.");
        }

        if (leadingSegments.Count > 0)
        {
            normalized.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = string.Join("\n\n", leadingSegments)
            });
        }

        foreach (var message in passThrough)
        {
            normalized.Add(ToMessage(message));
        }

        return normalized;
    }

    private HttpRequestMessage BuildRequest(JsonObject payload)
    {
        var serializedPayload = payload.ToJsonString(JsonOptions);

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.ChatCompletionsPath)
        {
            Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, JsonObject payload, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken);

        var statusCode = (int)response.StatusCode;
        var technicalMessage =
            $"OpenAI-compatible request failed: status={statusCode} path={_endpoint.ChatCompletionsPath} payload={payload.ToJsonString(JsonOptions)} response={responseBody}";
        var userMessage = ExtractUserMessage(responseBody, statusCode);

        throw new Configuration.ProviderException(userMessage, technicalMessage, statusCode);
    }

    /// <summary>
    /// Extracts a user-friendly error message from an OpenAI-compatible error response.
    /// Falls back to a generic message if the response can't be parsed.
    /// </summary>
    internal static string ExtractUserMessage(string? responseBody, int statusCode)
        => ProviderErrorHelper.ExtractUserMessage(responseBody, statusCode, "LLM provider");

    internal static JsonObject ToMessage(ChatMessage message)
    {
        // Classify contents by MEAI type
        var textSegments = new List<string>();
        var imageParts = new List<JsonObject>();
        var toolCalls = new List<JsonObject>();
        FunctionResultContent? toolResult = null;

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    textSegments.Add(text.Text);
                    break;

                case DataContent data:
                    var mime = data.MediaType ?? "application/octet-stream";
                    var b64 = Convert.ToBase64String(data.Data.ToArray());
                    imageParts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{mime};base64,{b64}"
                        }
                    });
                    break;

                case FunctionCallContent tc:
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = tc.CallId ?? Guid.NewGuid().ToString("N"),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name ?? string.Empty,
                            ["arguments"] = tc.Arguments is not null
                                ? JsonSerializer.Serialize(tc.Arguments, JsonOptions)
                                : "{}"
                        }
                    });
                    break;

                case FunctionResultContent tr:
                    toolResult = tr;
                    break;
            }
        }

        // Tool result message
        if (message.Role == ChatRole.Tool && toolResult is not null)
        {
            return new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = toolResult.CallId,
                ["content"] = SerializeToolResult(toolResult.Result)
            };
        }

        // Assistant message with tool calls
        if (message.Role == ChatRole.Assistant && toolCalls.Count > 0)
        {
            var msg = new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray(toolCalls.ToArray<JsonNode>())
            };
            if (textSegments.Count > 0)
                msg["content"] = textSegments[0];
            return msg;
        }

        // Multimodal: images present → content array
        if (imageParts.Count > 0)
        {
            var parts = new JsonArray();

            if (textSegments.Count > 0)
            {
                foreach (var seg in textSegments)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = seg
                    });
            }
            else if (!string.IsNullOrWhiteSpace(message.Text))
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = message.Text
                });
            }

            foreach (var img in imageParts)
                parts.Add(img);

            return new JsonObject
            {
                ["role"] = ToRole(message.Role),
                ["content"] = parts
            };
        }

        // Text-only (simple string, backward compatible)
        var textContent = textSegments.Count > 0
            ? string.Join("", textSegments)
            : message.Text ?? string.Empty;

        return new JsonObject
        {
            ["role"] = ToRole(message.Role),
            ["content"] = textContent
        };
    }

    internal static string SerializeToolResult(object? result) => result switch
    {
        null => string.Empty,
        string s => s,
        JsonElement je => je.GetRawText(),
        _ => JsonSerializer.Serialize(result, JsonOptions)
    };

    private static string ToRole(ChatRole? role)
        => role switch
        {
            null => "user",
            _ when role == ChatRole.System => "system",
            _ when role == ChatRole.Assistant => "assistant",
            _ when role == ChatRole.Tool => "tool",
            _ => "user"
        };

    private static JsonObject ToTool(AITool tool)
    {
        var schemaProperty = tool.GetType().GetProperty("JsonSchema");
        JsonNode? schema;
        if (schemaProperty?.GetValue(tool) is JsonElement jsonSchema)
        {
            schema = JsonNode.Parse(jsonSchema.GetRawText());
        }
        else
        {
            schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject()
            };
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
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
        var finishReason = ParseFinishReason(choice);

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

        // Fallback: extract tool calls from text when model uses XML-like format
        if (finishReason != ChatFinishReason.ToolCalls)
        {
            var textContent = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            var textToolCalls = TextToolCallParser.ExtractFromText(textContent);
            if (textToolCalls.Count > 0)
            {
                contents.RemoveAll(c => c is TextContent);
                var cleaned = TextToolCallParser.StripToolCallText(textContent!);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    contents.Add(new TextContent(cleaned));
                contents.AddRange(textToolCalls);
                finishReason = ChatFinishReason.ToolCalls;
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = root.TryGetProperty("model", out var model) ? model.GetString() : null,
            ResponseId = root.TryGetProperty("id", out var id) ? id.GetString() : null,
            FinishReason = finishReason,
            Usage = ParseUsage(root)
        };
    }

    private static IEnumerable<ChatResponseUpdate> ParseStreamingUpdates(
        JsonElement root,
        Dictionary<int, PendingToolCall> pendingToolCalls,
        double? wallClockPromptMs)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            // The final usage-only chunk has an empty choices array — still parse usage
            var usageOnly = ParseUsage(root, wallClockPromptMs);
            if (usageOnly is not null)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usageOnly)]);
            yield break;
        }

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
        // llama.cpp and other local servers often emit finish_reason:"stop" even when tool calls are present
        if (pendingToolCalls.Count > 0 && (finishReason == ChatFinishReason.ToolCalls || finishReason == ChatFinishReason.Stop))
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

        // Usage may appear in the final streaming chunk (when stream_options.include_usage is set)
        var usage = ParseUsage(root, wallClockPromptMs);
        if (usage is not null)
            contents.Add(new UsageContent(usage));

        if (contents.Count == 0 && finishReason is null)
        {
            // Content-less data events (e.g. prompt_progress during prefill) — yield
            // keepalive so the watchdog timer resets while the server is working.
            yield return KeepaliveUpdate;
            yield break;
        }

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

    // Field paths don't overlap between backends, so order is irrelevant.
    private static readonly IReadOnlyList<ITimingsExtractor> TimingsExtractors =
    [
        new LlamaCppTimingsExtractor(),
        new VllmTimingsExtractor(),
    ];

    /// <summary>
    /// Parses the <c>usage</c> object from an OpenAI-compatible response or
    /// streaming chunk. Runs every registered <see cref="ITimingsExtractor"/>
    /// against the root element so backend-specific telemetry (llama.cpp's
    /// <c>timings</c> object, vLLM's <c>usage.prompt_tokens_details.cached_tokens</c>)
    /// lands in <see cref="UsageDetails"/>. When the server doesn't supply
    /// its own prompt latency, <paramref name="wallClockPromptMs"/> fills the
    /// integer-encoded <c>prompt_us</c> field — typically a streaming-mode
    /// measurement between request send and first-content-byte. Returns null
    /// when the usage field is absent or not an object.
    /// </summary>
    internal static UsageDetails? ParseUsage(JsonElement root, double? wallClockPromptMs = null)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        long? promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt64() : null;
        long? completionTokens = usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number
            ? ct.GetInt64() : null;
        long? totalTokens = usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number
            ? tt.GetInt64() : null;

        if (promptTokens is null && completionTokens is null && totalTokens is null)
            return null;

        var details = new UsageDetails
        {
            InputTokenCount = promptTokens,
            OutputTokenCount = completionTokens,
            TotalTokenCount = totalTokens ?? (promptTokens ?? 0) + (completionTokens ?? 0)
        };

        foreach (var extractor in TimingsExtractors)
            extractor.Extract(root, details);

        // Wall-clock fallback: only kicks in when no server-side prompt
        // latency was supplied (vLLM and most generic backends). Includes
        // network RTT — flagged as client-measured in the metric, not
        // server-side honest prompt eval time.
        if (wallClockPromptMs is { } promptMs &&
            (details.AdditionalCounts is null ||
             !details.AdditionalCounts.ContainsKey(TimingsKeys.PromptUs)))
        {
            var additional = details.AdditionalCounts ??= [];
            additional[TimingsKeys.PromptUs] = (long)(promptMs * 1000);
        }

        return details;
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

    private static readonly ChatResponseUpdate KeepaliveUpdate = new() { Role = ChatRole.Assistant };

    internal sealed class ToolCallTextFilter
    {
        private const string Marker = "<tool_call";
        private const int OverlapSize = 9; // Marker.Length - 1

        private bool _suppressionActive;
        private readonly char[] _overlap = new char[OverlapSize];
        private int _overlapLength;

        /// <summary>
        /// Checks whether the given delta text (newest chunk only) should be suppressed.
        /// Uses a small overlap buffer to detect the marker split across delta boundaries.
        /// </summary>
        public bool ShouldSuppress(string? delta)
        {
            if (_suppressionActive)
                return true;

            if (string.IsNullOrEmpty(delta))
                return false;

            // Search window = overlap tail from previous delta(s) + current delta
            int windowLength = _overlapLength + delta.Length;
            Span<char> window = windowLength <= 256
                ? stackalloc char[windowLength]
                : new char[windowLength];

            _overlap.AsSpan(0, _overlapLength).CopyTo(window);
            delta.AsSpan().CopyTo(window[_overlapLength..]);

            if (window.IndexOf(Marker.AsSpan()) >= 0)
            {
                _suppressionActive = true;
                return true;
            }

            // Retain the last OverlapSize chars for cross-boundary detection
            if (windowLength >= OverlapSize)
            {
                window[(windowLength - OverlapSize)..].CopyTo(_overlap);
                _overlapLength = OverlapSize;
            }
            else
            {
                window[..windowLength].CopyTo(_overlap);
                _overlapLength = windowLength;
            }

            return false;
        }

        public bool IsActive => _suppressionActive;

        public static string GetCleanedText(StringBuilder accumulatedText)
            => TextToolCallParser.StripToolCallText(accumulatedText.ToString());
    }
}

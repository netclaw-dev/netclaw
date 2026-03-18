using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Pipeline policy for the OpenAI Codex backend at <c>chatgpt.com/backend-api/codex</c>.
/// </summary>
/// <remarks>
/// The Codex backend has stricter validation than <c>api.openai.com</c>:
/// <list type="bullet">
///   <item>Requires <c>ChatGPT-Account-Id</c> header (extracted from JWT)</item>
///   <item>Requires <c>"store": false</c> in the request body</item>
///   <item>Requires <c>"instructions"</c> to be present (even if empty)</item>
///   <item>Rejects system messages in <c>"input"</c> — must be in <c>"instructions"</c></item>
///   <item>Rejects <c>"strict": null</c> in tool definitions (must be omitted or boolean)</item>
/// </list>
/// </remarks>
internal sealed class OpenAiCodexRequestPolicy(string? accountId) : PipelinePolicy
{
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Modify(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Modify(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Modify(PipelineMessage message)
    {
        var request = message.Request;

        // Inject ChatGPT-Account-Id header (extracted from JWT by JwtAccountIdExtractor)
        if (accountId is not null)
            request.Headers.Set("ChatGPT-Account-Id", accountId);

        // Rewrite request body
        if (request.Content is null)
            return;

        using var stream = new MemoryStream();
        request.Content.WriteTo(stream, default);
        var bytes = stream.ToArray();

        var node = JsonNode.Parse(bytes);
        if (node is not JsonObject obj)
            return;

        // Codex backend requires "store": false
        obj["store"] = false;

        // Move system messages from "input" to "instructions" — the Codex backend
        // rejects system role messages in the input array.
        MoveSystemMessagesToInstructions(obj);

        // Strip "strict": null from tool definitions — Codex backend rejects null values
        StripNullStrict(obj);

        var modified = JsonSerializer.SerializeToUtf8Bytes(obj);
        request.Content = BinaryContent.Create(BinaryData.FromBytes(modified));
    }

    /// <summary>
    /// Extracts system messages from the <c>"input"</c> array, concatenates their text
    /// content, and sets the result as <c>"instructions"</c>. Removes the system messages
    /// from <c>"input"</c>. If no system messages exist, defaults to empty string
    /// (the Codex backend requires <c>"instructions"</c> to be present).
    /// </summary>
    private static void MoveSystemMessagesToInstructions(JsonObject body)
    {
        if (body["input"] is not JsonArray input)
        {
            // Ensure instructions always present
            if (!body.ContainsKey("instructions") || body["instructions"] is null)
                body["instructions"] = "";
            return;
        }

        var systemTexts = new List<string>();
        var toRemove = new List<JsonNode>();

        foreach (var item in input)
        {
            if (item is not JsonObject msg)
                continue;

            if (msg["role"]?.GetValue<string>() != "system")
                continue;

            // Extract text from content array: [{type: "input_text", text: "..."}]
            if (msg["content"] is JsonArray contentArray)
            {
                foreach (var part in contentArray)
                {
                    if (part is JsonObject partObj &&
                        partObj["text"]?.GetValue<string>() is { } text)
                    {
                        systemTexts.Add(text);
                    }
                }
            }

            toRemove.Add(item);
        }

        foreach (var item in toRemove)
            input.Remove(item);

        // Merge with any existing instructions from the SDK
        var existing = body["instructions"]?.GetValue<string>() ?? "";
        var combined = systemTexts.Count > 0
            ? string.Join("\n", [existing, ..systemTexts]).TrimStart('\n')
            : existing;

        body["instructions"] = combined;
    }

    /// <summary>
    /// Removes <c>"strict": null</c> from each tool in the <c>tools</c> array.
    /// The OpenAI SDK serializes unset <c>strict</c> as <c>null</c>, but the
    /// Codex backend at chatgpt.com rejects it.
    /// </summary>
    private static void StripNullStrict(JsonObject body)
    {
        if (body["tools"] is not JsonArray tools)
            return;

        foreach (var tool in tools)
        {
            if (tool is not JsonObject toolObj)
                continue;

            if (toolObj.ContainsKey("strict") && toolObj["strict"] is null)
                toolObj.Remove("strict");
        }
    }
}

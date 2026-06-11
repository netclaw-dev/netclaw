// -----------------------------------------------------------------------
// <copyright file="OpenAiCodexRequestPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace Netclaw.Providers.OpenAi;

/// <summary>
/// Pipeline policy for the OpenAI Codex backend at <c>chatgpt.com/backend-api/codex</c>.
/// </summary>
/// <remarks>
/// The Codex backend has stricter validation than <c>api.openai.com</c>:
/// <list type="bullet">
///   <item>Requires <c>ChatGPT-Account-Id</c> header from OpenAI OAuth metadata</item>
///   <item>Requires <c>"store": false</c> in the request body</item>
///   <item>Requires <c>"instructions"</c> to be present (even if empty)</item>
///   <item>Rejects system messages in <c>"input"</c> — must be in <c>"instructions"</c></item>
///   <item>Rejects <c>"strict": null</c> in tool definitions (must be omitted or boolean)</item>
/// </list>
/// </remarks>
internal sealed class OpenAiCodexRequestPolicy(string accountId) : PipelinePolicy
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
        // Set even when the body is empty/non-JSON; the Codex backend requires
        // this OAuth workspace selector on every request.
        message.Request.Headers.Set("ChatGPT-Account-Id", accountId);

        PipelineRequestBodyEditor.EditJsonBody(message, obj =>
        {
            // Codex backend requires "store": false
            obj["store"] = false;

            // Move system messages from "input" to "instructions" — the Codex backend
            // rejects system role messages in the input array.
            MoveSystemMessagesToInstructions(obj);

            // Strip "strict": null from tool definitions — Codex backend rejects null values
            StripNullStrict(obj);
        });
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

            if (toolObj.TryGetPropertyValue("strict", out var strictValue) && strictValue is null)
                toolObj.Remove("strict");
        }
    }
}

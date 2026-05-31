// -----------------------------------------------------------------------
// <copyright file="StreamingResponseReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions.Pipelines;

/// <summary>
/// Per-update diagnostics accumulated while consuming a streaming LLM response.
/// Both the session and sub-agent paths surface these counters in their logs.
/// </summary>
internal readonly record struct StreamDiagnostics(
    int UpdateCount,
    int EmptyUpdateCount,
    int TextDeltaCount,
    int TextChars,
    int ThinkingDeltaCount,
    int ThinkingChars,
    int ToolCallDeltaCount,
    string? FinishReason);

/// <summary>
/// Canonical classification of a single streaming update, computed once in the
/// reader so callers do not each re-derive "is this progress or a keepalive?".
/// <para>
/// A content-free keepalive (e.g. llama-server's <c>prompt_progress</c> heartbeat,
/// or a usage-only chunk) proves the socket and server are alive but carries no
/// model output: <see cref="HasSubstantiveContent"/> is false. Distinguishing it
/// from substantive deltas lets a two-phase watchdog refresh the generous prefill
/// budget on keepalives yet only promote to the tighter inter-delta budget once
/// real tokens arrive (<see cref="IsFirstSubstantive"/>).
/// </para>
/// </summary>
internal readonly record struct StreamUpdateClassification(
    bool HasSubstantiveContent,
    bool IsFirstSubstantive);

/// <summary>
/// The fully consumed streaming response plus its accumulated diagnostics.
/// </summary>
internal readonly record struct StreamReadResult(
    ChatResponse Response,
    StreamDiagnostics Diagnostics);

/// <summary>
/// Single owner of the streaming LLM consumption loop shared by the main-session
/// (<see cref="SessionLlmInvoker"/>) and sub-agent (<c>SubAgentActor.InvokeLlmAsync</c>)
/// paths. It owns the <c>await foreach</c>, per-update counting, the final
/// <see cref="ChatResponse"/> assembly (with an empty-response fallback), and the
/// one canonical update classification. Callers supply <paramref name="onUpdate"/>
/// to adapt each classified update to their own actor messages (UI deltas, watchdog
/// pings) — that dispatch is the only legitimately path-specific part.
/// </summary>
internal static class StreamingResponseReader
{
    public static async Task<StreamReadResult> ReadAsync(
        IChatClient client,
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options,
        Action<ChatResponseUpdate, StreamUpdateClassification, StreamDiagnostics> onUpdate,
        CancellationToken ct)
    {
        var updates = new List<ChatResponseUpdate>();

        var updateCount = 0;
        var emptyUpdateCount = 0;
        var textDeltaCount = 0;
        var textChars = 0;
        var thinkingDeltaCount = 0;
        var thinkingChars = 0;
        var toolCallDeltaCount = 0;
        string? finishReason = null;
        var anySubstantiveSeen = false;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
        {
            updates.Add(update);
            updateCount++;

            if (update.Contents.Count == 0 && update.FinishReason is null)
                emptyUpdateCount++;

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        textDeltaCount++;
                        textChars += text.Text!.Length;
                        break;
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        thinkingDeltaCount++;
                        thinkingChars += reasoning.Text!.Length;
                        break;
                    case FunctionCallContent:
                        toolCallDeltaCount++;
                        break;
                }
            }

            if (update.FinishReason is not null)
                finishReason = update.FinishReason.ToString();

            var classification = Classify(update, anySubstantiveSeen);
            if (classification.IsFirstSubstantive)
                anySubstantiveSeen = true;

            onUpdate(
                update,
                classification,
                new StreamDiagnostics(
                    updateCount,
                    emptyUpdateCount,
                    textDeltaCount,
                    textChars,
                    thinkingDeltaCount,
                    thinkingChars,
                    toolCallDeltaCount,
                    finishReason));
        }

        // The full response is reassembled from the retained updates. The fallback
        // only fires when the stream yielded nothing (or no coalescable message),
        // in which case an empty assistant message is the correct degenerate result —
        // ExtractText then surfaces it as the empty-response marker for the caller.
        var response = updates.ToChatResponse();
        if (response.Messages.Count == 0)
            response.Messages.Add(new AiChatMessage(ChatRole.Assistant, []));

        return new StreamReadResult(
            response,
            new StreamDiagnostics(
                updateCount,
                emptyUpdateCount,
                textDeltaCount,
                textChars,
                thinkingDeltaCount,
                thinkingChars,
                toolCallDeltaCount,
                finishReason));
    }

    /// <summary>
    /// Marks the first substantive update so a two-phase watchdog knows when to
    /// promote. <paramref name="anySubstantiveSeen"/> is the caller's running
    /// "have we seen real output yet?" flag.
    /// </summary>
    internal static StreamUpdateClassification Classify(ChatResponseUpdate update, bool anySubstantiveSeen)
    {
        var hasSubstantive = IsSubstantiveUpdate(update);
        return new StreamUpdateClassification(
            HasSubstantiveContent: hasSubstantive,
            IsFirstSubstantive: hasSubstantive && !anySubstantiveSeen);
    }

    /// <summary>
    /// True when an update represents real model progress. A finish reason, non-empty
    /// text/thinking, a tool call, or any non-usage content all count. Only a
    /// content-free heartbeat or a usage-only chunk (with no finish reason) is treated
    /// as a non-substantive keepalive. This mirrors the pre-extraction predicate so a
    /// provider that streams an error/refusal or other non-text content before the
    /// first token is still recognized as progress, not silently treated as a hang.
    /// </summary>
    internal static bool IsSubstantiveUpdate(ChatResponseUpdate update)
    {
        if (update.FinishReason is not null)
            return true;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                case FunctionCallContent:
                    return true;
                case UsageContent:
                    break;
                default:
                    return true;
            }
        }

        return false;
    }
}

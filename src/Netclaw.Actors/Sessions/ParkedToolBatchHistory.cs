// -----------------------------------------------------------------------
// <copyright file="ParkedToolBatchHistory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

internal static class ParkedToolBatchHistory
{
    /// <summary>
    /// Every production synthetic result for an abandoned call starts with this text. Journaled results from
    /// earlier versions use the same text. A synthetic result closes the call without an answer, so a late
    /// answer to that call is "expired", not "already answered".
    /// </summary>
    public const string AbandonedResultPrefix = "Tool call was not completed — ";

    public static IReadOnlyList<SerializableChatMessage> FindToolResultsFor(
        IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage assistantMessage)
    {
        var ids = assistantMessage.ToolCalls
            .Select(c => c.CallId.Value)
            .ToHashSet(StringComparer.Ordinal);

        return history
            .Where(m => m.Role == ChatRole.Tool
                && m.ToolCallId is not null
                && ids.Contains(m.ToolCallId.Value.Value))
            .ToArray();
    }

    public static bool HasToolResult(
        IReadOnlyList<SerializableChatMessage> history,
        string callId)
        => history.Any(m => m.Role == ChatRole.Tool
            && m.ToolCallId?.Value == callId);

    /// <summary>
    /// True when the call has a result that is not a synthetic abandonment result. Such a result comes from a
    /// run or a denial, so the approval prompt for the call had an answer. This only selects notice text.
    /// It never grants authority.
    /// </summary>
    public static bool HasAnsweredToolResult(
        IReadOnlyList<SerializableChatMessage> history,
        string callId)
        => history.Any(m => m.Role == ChatRole.Tool
            && m.ToolCallId?.Value == callId
            && !IsAbandonedResult(m));

    private static bool IsAbandonedResult(SerializableChatMessage message)
        => message.Content.StartsWith(AbandonedResultPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Locates the tail assistant message carrying unanswered tool calls.
    /// When <paramref name="callId"/> is set, a newer unanswered batch for a
    /// different call expires this response instead of re-driving stale work.
    /// </summary>
    public static SerializableChatMessage? FindRedrivableAssistantMessage(
        IReadOnlyList<SerializableChatMessage> history,
        string? callId)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var candidate = history[i];
            if (candidate.Role != ChatRole.Assistant || candidate.ToolCalls.Count == 0)
                continue;

            if (callId is not null
                && candidate.ToolCalls.All(tc => tc.CallId.Value != callId))
            {
                return null;
            }

            if (callId is not null && HasToolResult(history, callId))
                return null;

            if (callId is null && candidate.ToolCalls.All(tc => HasToolResult(history, tc.CallId.Value)))
                continue;

            return candidate;
        }

        return null;
    }

    public static IReadOnlyList<SerializableChatMessage> BuildSyntheticAbandonResults(
        IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage assistantMessage,
        string resultContent)
    {
        var results = new List<SerializableChatMessage>();
        foreach (var call in assistantMessage.ToolCalls)
        {
            if (HasToolResult(history, call.CallId.Value))
                continue;

            results.Add(CreateAbandonedToolResult(call, resultContent));
        }

        return results;
    }

    private static SerializableChatMessage CreateAbandonedToolResult(
        SerializableToolCall call,
        string resultContent)
        => new()
        {
            Role = ChatRole.Tool,
            Content = resultContent,
            ToolCallId = call.CallId,
            Name = call.Name.Value
        };
}

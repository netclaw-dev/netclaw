// -----------------------------------------------------------------------
// <copyright file="ParkedToolBatchHistory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

internal static class ParkedToolBatchHistory
{
    public static IReadOnlyList<SerializableChatMessage> FindToolResultsFor(
        IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage assistantMessage)
    {
        var ids = assistantMessage.ToolCalls
            .Select(c => c.CallId.Value)
            .ToHashSet(StringComparer.Ordinal);

        var occurrence = FindCanonicalOccurrence(history, assistantMessage);
        var results = new List<SerializableChatMessage>();
        for (var i = occurrence + 1; i < history.Count; i++)
        {
            var message = history[i];
            if (message.Role == ChatRole.Assistant)
                break;
            if (message.Role == ChatRole.Tool && message.ToolCallId is { } callId
                && ids.Contains(callId.Value))
                results.Add(message);
        }
        return results;
    }

    public static bool HasToolResult(
        IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage assistantMessage,
        string callId)
        => FindToolResultsFor(history, assistantMessage).Any(message => message.ToolCallId?.Value == callId);

    public static bool HasHistoricalToolResult(
        IReadOnlyList<SerializableChatMessage> history,
        string callId)
        => history.Any(m => m.Role == ChatRole.Tool
            && m.ToolCallId?.Value == callId);

    private static int FindCanonicalOccurrence(
        IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage assistantMessage)
    {
        var occurrence = -1;
        for (var i = 0; i < history.Count; i++)
        {
            if (!ReferenceEquals(history[i], assistantMessage))
                continue;
            if (occurrence >= 0)
                throw new InvalidDataException("The canonical assistant occurrence is ambiguous.");
            occurrence = i;
        }
        return occurrence >= 0 ? occurrence
            : throw new InvalidDataException("The canonical assistant occurrence is absent from history.");
    }

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

            if (callId is not null && HasToolResult(history, candidate, callId))
                return null;

            if (callId is null && candidate.ToolCalls.All(tc => HasToolResult(history, candidate, tc.CallId.Value)))
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
            if (HasToolResult(history, assistantMessage, call.CallId.Value))
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

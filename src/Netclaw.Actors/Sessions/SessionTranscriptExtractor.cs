// -----------------------------------------------------------------------
// <copyright file="SessionTranscriptExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

internal static class SessionTranscriptExtractor
{
    public static IReadOnlyList<SessionTranscriptEntry> ExtractTurn(
        IReadOnlyList<SerializableChatMessage> history,
        SerializableChatMessage userMessage,
        SerializableChatMessage assistantReply,
        string? turnId,
        long timestampMs)
    {
        var startIndex = -1;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            if (history[index] != userMessage)
                continue;

            startIndex = index;
            break;
        }

        var messages = startIndex >= 0
            ? history.Skip(startIndex).ToList()
            : [userMessage];
        messages.Add(assistantReply);

        return Extract(messages, turnId, timestampMs);
    }

    public static IReadOnlyList<SessionTranscriptEntry> Extract(
        IEnumerable<SerializableChatMessage> history,
        string? turnId = null,
        long timestampMs = 0)
    {
        var entries = new List<SessionTranscriptEntry>();
        var calls = new Dictionary<string, SerializableToolCall>(StringComparer.Ordinal);

        foreach (var message in history)
        {
            foreach (var call in message.ToolCalls)
                calls[call.CallId.Value] = call;

            switch (message.Role)
            {
                case ChatRole.User when !string.IsNullOrWhiteSpace(message.Content):
                    entries.Add(new SessionTranscriptEntry
                    {
                        Type = SessionTranscriptEntryTypes.User,
                        TurnId = turnId,
                        TimestampMs = timestampMs,
                        Role = "user",
                        Text = message.Content
                    });
                    break;
                case ChatRole.Assistant when !string.IsNullOrWhiteSpace(message.Content):
                    entries.Add(new SessionTranscriptEntry
                    {
                        Type = SessionTranscriptEntryTypes.Assistant,
                        TurnId = turnId,
                        TimestampMs = timestampMs,
                        Role = "assistant",
                        Text = message.Content
                    });
                    break;
                case ChatRole.Assistant when message.ToolCalls.Count > 0:
                    break;
                case ChatRole.Tool when message.ToolCallId is { } callId:
                    calls.TryGetValue(callId.Value, out var call);
                    entries.Add(new SessionTranscriptEntry
                    {
                        Type = SessionTranscriptEntryTypes.Tool,
                        TurnId = turnId,
                        TimestampMs = timestampMs,
                        CallId = callId.Value,
                        ToolName = message.Name ?? call?.Name.Value ?? "unknown",
                        ArgumentsJson = call?.ArgumentsJson,
                        Rationale = ToolCallMeta.Parse(call?.MetaJson)?.Rationale,
                        Result = message.Content
                    });
                    calls.Remove(callId.Value);
                    break;
                case ChatRole.System:
                    break;
                default:
                    entries.Add(new SessionTranscriptEntry
                    {
                        Type = SessionTranscriptEntryTypes.Diagnostic,
                        TurnId = turnId,
                        TimestampMs = timestampMs,
                        Text = $"Legacy transcript detail for role '{message.Role}' is not supported."
                    });
                    break;
            }
        }

        foreach (var call in calls.Values)
        {
            entries.Add(new SessionTranscriptEntry
            {
                Type = SessionTranscriptEntryTypes.Diagnostic,
                TurnId = turnId,
                TimestampMs = timestampMs,
                CallId = call.CallId.Value,
                ToolName = call.Name.Value,
                ArgumentsJson = call.ArgumentsJson,
                Text = "Legacy tool call has no settled result."
            });
        }

        return entries;
    }
}

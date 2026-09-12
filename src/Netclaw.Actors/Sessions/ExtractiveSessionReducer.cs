// -----------------------------------------------------------------------
// <copyright file="ExtractiveSessionReducer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Deterministic extractive chat reducer that keeps the system prompt and the
/// last N non-system messages, dropping everything else. Tool call and tool
/// result messages within the window are preserved (unlike MEAI's
/// <c>MessageCountingChatReducer</c> which drops them silently).
///
/// Enforces the netclaw-session "Tool call/result pair integrity" requirement
/// by truncating only at user-message boundaries (OpenCode's approach). In a
/// well-formed MEAI conversation, tool call/result pairs are bracketed by
/// user messages: User → Assistant(with tool_calls) → Tool(result) → Assistant
/// → User. A window that starts on a user message cannot split a pair, since
/// the pair's components are contiguous and either all kept or all discarded.
///
/// System nudges (user-role messages prefixed with <c>[system:</c>) are not
/// user turns and are skipped during the backward walk — they are recall
/// content / empty-response nudges injected by the actor, not actual user
/// input.
///
/// This reducer is synchronous (<see cref="Task.FromResult{TResult}"/>) and
/// cannot fail. The actor calls it via <c>await</c> inside a
/// <c>CommandAsync</c> handler, so async reducers are also supported.
/// </summary>
public sealed class ExtractiveSessionReducer : IChatReducer
{
    private const string SystemNudgePrefix = SessionState.SystemNudgePrefix;

    private readonly int _keepRecentMessages;

    public ExtractiveSessionReducer(int keepRecentMessages)
    {
        _keepRecentMessages = Math.Max(0, keepRecentMessages);
    }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];

        var hasSystemPrompt = list.Count > 0 && list[0].Role == ChatRole.System;
        var systemOffset = hasSystemPrompt ? 1 : 0;
        var nonSystemCount = list.Count - systemOffset;
        var keepCount = Math.Min(_keepRecentMessages, nonSystemCount);

        // Nothing to reduce — return as-is
        if (keepCount >= nonSystemCount)
        {
            return Task.FromResult<IEnumerable<ChatMessage>>(list);
        }

        // Keep-zero: drop everything post-system. The caller is explicitly
        // saying "only the system prompt survives".
        if (keepCount == 0)
        {
            if (!hasSystemPrompt)
                return Task.FromResult<IEnumerable<ChatMessage>>(new List<ChatMessage>());

            return Task.FromResult<IEnumerable<ChatMessage>>(new List<ChatMessage> { list[0] });
        }

        var startIndex = list.Count - keepCount;

        // Pair integrity: walk backward from the naive cut to the nearest
        // user-message boundary. A user-role message that is not a system
        // nudge is a safe truncation point — tool call/result pairs in the
        // conversation are contiguous within a user-to-user envelope, so a
        // cut on a user boundary cannot split a pair.
        while (startIndex > systemOffset && !IsUserTurn(list[startIndex]))
        {
            startIndex--;
        }

        // Defense in depth: if the walk-back fell through to systemOffset
        // and that message is a Tool-role orphan (e.g. recovered from broken
        // state or a buggy prior compaction), advance forward past leading
        // Tool messages. A kept window that starts with a Tool orphan is
        // rejected by downstream providers — we'd rather shrink the window
        // than emit an unsendable request.
        while (startIndex < list.Count && list[startIndex].Role == ChatRole.Tool)
        {
            startIndex++;
        }

        var result = new List<ChatMessage>(list.Count - startIndex + systemOffset);

        if (hasSystemPrompt)
            result.Add(list[0]);

        for (var i = startIndex; i < list.Count; i++)
        {
            result.Add(list[i]);
        }

        return Task.FromResult<IEnumerable<ChatMessage>>(result);
    }

    private static bool IsUserTurn(ChatMessage message)
    {
        if (message.Role != ChatRole.User)
            return false;

        // Skip system nudges — they use User role but are injected by the
        // actor (recall content, empty-response nudges), not actual user
        // input.
        var text = message.Text;
        if (!string.IsNullOrEmpty(text) && text.StartsWith(SystemNudgePrefix, StringComparison.Ordinal))
            return false;

        return true;
    }
}

using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Deterministic extractive chat reducer that keeps the system prompt and the
/// last N non-system messages, dropping everything else. Tool call and tool
/// result messages within the window are preserved (unlike MEAI's
/// <c>MessageCountingChatReducer</c> which drops them silently).
///
/// This reducer is synchronous (<see cref="Task.FromResult{TResult}"/>) and
/// cannot fail. The actor calls it via <c>await</c> inside a
/// <c>CommandAsync</c> handler, so async reducers are also supported.
/// </summary>
public sealed class ExtractiveSessionReducer : IChatReducer
{
    private readonly int _keepRecentMessages;

    public ExtractiveSessionReducer(int keepRecentMessages)
    {
        _keepRecentMessages = Math.Max(0, keepRecentMessages);
    }

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        var hasSystemPrompt = list.Count > 0 && list[0].Role == ChatRole.System;
        var systemOffset = hasSystemPrompt ? 1 : 0;
        var nonSystemCount = list.Count - systemOffset;
        var keepCount = Math.Min(_keepRecentMessages, nonSystemCount);

        // Nothing to reduce — return as-is
        if (keepCount >= nonSystemCount)
        {
            return Task.FromResult<IEnumerable<ChatMessage>>(list);
        }

        var result = new List<ChatMessage>(keepCount + systemOffset);

        if (hasSystemPrompt)
            result.Add(list[0]);

        // Keep last N non-system messages (tool calls and results included)
        var startIndex = list.Count - keepCount;
        for (var i = startIndex; i < list.Count; i++)
        {
            result.Add(list[i]);
        }

        return Task.FromResult<IEnumerable<ChatMessage>>(result);
    }
}

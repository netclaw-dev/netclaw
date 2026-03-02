using System.Collections.Immutable;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Immutable conversation state for an LLM session. Decoupled from the actor
/// so that state transitions (event application) are pure functions testable
/// without an ActorSystem.
///
/// The actor holds a single <c>SessionState</c> field and replaces it on each
/// event via the <c>Apply</c> methods. Transient concerns (subscribers, message
/// buffer, behavior) remain on the actor.
/// </summary>
public sealed record SessionState
{
    public static readonly SessionState Empty = new();
    internal const string SystemNudgePrefix = "[system:";

    public ImmutableList<SerializableChatMessage> History { get; init; } =
        ImmutableList<SerializableChatMessage>.Empty;

    public int TurnCount { get; init; }

    public string? Title { get; init; }

    // ── Event application (pure functions) ──

    public SessionState Apply(SystemPromptSet evt)
    {
        var systemMsg = new SerializableChatMessage
        {
            Role = ChatRole.System,
            Content = evt.Content
        };

        // System prompt is always the first message. Replace if present.
        if (History.Count > 0 && History[0].Role == ChatRole.System)
        {
            return this with { History = History.SetItem(0, systemMsg) };
        }

        return this with { History = History.Insert(0, systemMsg) };
    }

    public SessionState Apply(TurnRecorded evt)
    {
        return this with
        {
            History = History.Add(evt.UserMessage).Add(evt.AssistantReply),
            TurnCount = TurnCount + 1
        };
    }

    public SessionState Apply(SessionTitleSet evt)
    {
        return this with { Title = evt.Title };
    }

    public SessionState Apply(SessionCompacted evt)
    {
        // Preserve system prompt if present
        var builder = ImmutableList.CreateBuilder<SerializableChatMessage>();
        if (History.Count > 0 && History[0].Role == ChatRole.System)
        {
            builder.Add(History[0]);
        }

        builder.AddRange(evt.CompactedMessages);
        return this with { History = builder.ToImmutable() };
    }

    // ── Command helpers ──

    /// <summary>
    /// Add a user message to history (before firing an LLM call).
    /// This is transient state that gets persisted as part of <see cref="TurnRecorded"/>.
    /// </summary>
    public SessionState AddUserMessage(string content, List<SerializableMediaReference>? mediaReferences = null)
    {
        var msg = new SerializableChatMessage
        {
            Role = ChatRole.User,
            Content = content
        };

        if (mediaReferences is { Count: > 0 })
            msg.MediaReferences = mediaReferences;

        return this with { History = History.Add(msg) };
    }

    /// <summary>
    /// Add an error reply to history when an LLM call fails.
    /// </summary>
    public SessionState AddErrorReply(string errorMessage)
    {
        return this with
        {
            History = History.Add(new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                Content = errorMessage
            }),
            TurnCount = TurnCount + 1
        };
    }

    /// <summary>
    /// Add a transient system nudge to history to correct LLM behavior (e.g., empty response recovery).
    /// Not persisted as a turn — just injected into the conversation to guide the next LLM call.
    /// </summary>
    public SessionState AddSystemNudge(string nudge)
    {
        return this with
        {
            History = History.Add(new SerializableChatMessage
            {
                Role = ChatRole.User,
                Content = $"{SystemNudgePrefix} {nudge}]"
            })
        };
    }

    /// <summary>
    /// Find the last user message in history (for building persistence events).
    /// </summary>
    public SerializableChatMessage? FindLastUserMessage()
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            var message = History[i];
            if (message.Role != ChatRole.User)
                continue;

            if (IsSystemNudge(message))
                continue;

            return message;
        }

        return null;
    }

    internal static bool IsSystemNudge(SerializableChatMessage message)
    {
        return message.Role == ChatRole.User
            && message.Content.StartsWith(SystemNudgePrefix, StringComparison.Ordinal);
    }

    // ── Compaction helpers ──

    /// <summary>
    /// Phase 1 of compaction: Clear old tool results while preserving recent ones.
    /// Replaces old tool result content with a placeholder while keeping the tool
    /// call structure intact (no orphaned tool calls).
    /// </summary>
    /// <param name="keepRecent">Number of recent tool call/result groups to preserve in full.</param>
    /// <returns>A new state with old tool results cleared, and the count of results that were cleared.</returns>
    public (SessionState State, int ClearedCount) ClearOldToolResults(int keepRecent)
    {
        if (keepRecent < 0) keepRecent = 0;

        // Find all tool result message indices (Role == Tool)
        var toolResultIndices = new List<int>();
        for (var i = 0; i < History.Count; i++)
        {
            if (History[i].Role == ChatRole.Tool)
            {
                toolResultIndices.Add(i);
            }
        }

        if (toolResultIndices.Count <= keepRecent)
        {
            return (this, 0);
        }

        // Clear all but the last N tool results
        var indicesToClear = toolResultIndices
            .Take(toolResultIndices.Count - keepRecent)
            .ToHashSet();

        var builder = History.ToBuilder();
        var clearedCount = 0;

        foreach (var idx in indicesToClear)
        {
            var msg = builder[idx];
            builder[idx] = new SerializableChatMessage
            {
                Role = ChatRole.Tool,
                Content = $"[Tool result cleared — {msg.Name ?? "unknown"} call {msg.ToolCallId ?? "?"}]",
                ToolCallId = msg.ToolCallId,
                Name = msg.Name
            };
            clearedCount++;
        }

        return (this with { History = builder.ToImmutable() }, clearedCount);
    }

    // ── Snapshot conversion ──

    public SessionSnapshot ToSnapshot()
    {
        return new SessionSnapshot
        {
            History = new List<SerializableChatMessage>(History),
            TurnCount = TurnCount,
            Title = Title
        };
    }

    public static SessionState FromSnapshot(SessionSnapshot snapshot)
    {
        return new SessionState
        {
            History = ImmutableList.CreateRange(snapshot.History),
            TurnCount = snapshot.TurnCount,
            Title = snapshot.Title
        };
    }
}

using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Bitmask controlling which categories of session output a subscriber receives.
/// Lifecycle messages (<see cref="TurnCompleted"/>, <see cref="ErrorOutput"/>,
/// <see cref="SessionTitleOutput"/>) are always delivered regardless of filter.
/// </summary>
[Flags]
public enum OutputFilter
{
    /// <summary>No content — lifecycle messages only.</summary>
    None = 0,

    /// <summary><see cref="TextOutput"/> — user-facing assistant replies.</summary>
    Text = 1 << 0,

    /// <summary><see cref="ThinkingOutput"/> — reasoning/thinking tokens.</summary>
    Thinking = 1 << 1,

    /// <summary><see cref="ToolCallOutput"/> and <see cref="ToolResultOutput"/> — tool interactions.</summary>
    ToolCalls = 1 << 2,

    /// <summary><see cref="UsageOutput"/> — token counts and context window consumption.</summary>
    Usage = 1 << 3,

    /// <summary><see cref="FileOutput"/> — file attachments produced by the LLM or tools.</summary>
    Files = 1 << 4,

    // ── Convenience presets ──

    /// <summary>Text replies only — suitable for end-user adapters (Slack, TUI chat).</summary>
    TextOnly = Text,

    /// <summary>Text + token usage — for adapters that show context window indicators.</summary>
    TextAndUsage = Text | Usage,

    /// <summary>Everything — suitable for ops consoles, debugging, and observability.</summary>
    Full = Text | Thinking | ToolCalls | Usage | Files,
}

/// <summary>
/// Join a session to receive output. Works for new sessions (adapter generates ID),
/// resuming old sessions (reuse known ID), or tapping into active sessions.
/// Routes through <see cref="IWithSessionId"/> to reach the correct session actor.
/// </summary>
public sealed record JoinSession : IWithSessionId
{
    public SessionId SessionId { get; init; }

    /// <summary>
    /// The actor that will receive session output.
    /// </summary>
    public required IActorRef Subscriber { get; init; }

    /// <summary>
    /// Bitmask of output categories the subscriber wants to receive.
    /// Lifecycle messages are always delivered regardless of this value.
    /// </summary>
    public OutputFilter Filter { get; init; } = OutputFilter.TextOnly;
}

/// <summary>
/// Explicitly leave a session. Also handled automatically via DeathWatch
/// when the subscriber terminates.
/// </summary>
public sealed record LeaveSession : IWithSessionId
{
    public SessionId SessionId { get; init; }

    public required IActorRef Subscriber { get; init; }
}

/// <summary>
/// Acknowledgement sent to the subscriber after successfully joining a session.
/// Provides current session state for catch-up.
/// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
/// </summary>
public sealed record SessionJoined : SessionOutput
{
    /// <summary>
    /// Human-readable session title, if one has been generated.
    /// May be null for brand-new sessions.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Number of turns completed so far.
    /// </summary>
    public int TurnCount { get; init; }
}

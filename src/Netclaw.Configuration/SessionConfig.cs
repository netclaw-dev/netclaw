using Microsoft.Extensions.Configuration;

namespace Netclaw.Configuration;

/// <summary>
/// Operator-facing session configuration. Bound from the <c>Session</c> section
/// in <c>netclaw.json</c> at startup.
///
/// Model-derived properties (ModelId, ContextWindowTokens, modalities) live in
/// <see cref="ModelCapabilities"/>. Internal tuning constants live in
/// <see cref="SessionTuning"/> (accessible via <see cref="Tuning"/>).
/// </summary>
public sealed record SessionConfig
{
    /// <summary>
    /// Maximum number of individual tool calls allowed per turn.
    /// Each tool call in a batch counts separately (e.g., 3 parallel calls = 3).
    /// At ~75% of this limit, a budget-awareness nudge is injected so the model
    /// can start wrapping up. At 100%, tools are stripped and the model is asked
    /// to summarize its work.
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 30;

    /// <summary>
    /// Idle seconds before the session memory observer triggers distillation.
    /// The observer watches the conversation stream and distills memories
    /// when the session goes quiet for this duration.
    /// </summary>
    public int MemoryObserverIdleSeconds { get; init; } = 90;

    /// <summary>
    /// How long a session can be idle before passivating.
    /// The actor saves a snapshot and stops itself; re-creation by
    /// <c>GenericChildPerEntityParent</c> on next message recovers state from journal.
    /// Default 30 minutes. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Timeout for the primary per-turn LLM streaming call.
    /// Prevents sessions from remaining stuck in Processing forever when a
    /// provider stream stalls under network/backpressure failure modes.
    /// </summary>
    public TimeSpan TurnLlmTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Timeout for one tool-execution batch (all tool calls emitted
    /// by a single assistant response). Prevents indefinite hangs when one or
    /// more tools block forever.
    /// </summary>
    public TimeSpan ToolExecutionTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Timeout for sidecar LLM calls (title generation, observer
    /// summaries, and memory extraction). Increase this when running slower
    /// models to reduce false timeout failures during background tasks.
    /// </summary>
    public TimeSpan SidecarLlmTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Maximum inactivity timeout for LLM streaming calls. Used both as the
    /// initial wait for the first token (prefill phase) and as the silence
    /// threshold between consecutive deltas — the timer resets on every delta.
    /// Falls back to <see cref="TurnLlmTimeout"/> if not explicitly configured.
    /// </summary>
    public TimeSpan FirstTokenTimeout { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>
    /// Internal tuning constants. Bindable from config for development/testing
    /// but not part of the documented operator surface.
    /// </summary>
    public SessionTuning Tuning { get; init; } = new();

    /// <summary>
    /// Bind from an <see cref="IConfigurationSection"/> with backward-compatible
    /// int-seconds keys for timeouts. Enforces a minimum of 1 second per timeout.
    /// </summary>
    public static SessionConfig BindFromConfiguration(IConfigurationSection section)
    {
        var raw = section.Get<RawSessionConfig>() ?? new RawSessionConfig();
        var tuning = BindTuning(section);

        var turnLlmTimeout = TimeSpan.FromSeconds(Math.Max(1, raw.TurnLlmTimeoutSeconds));

        return new SessionConfig
        {
            MaxToolCallsPerTurn = raw.MaxToolCallsPerTurn,
            MemoryObserverIdleSeconds = raw.MemoryObserverIdleSeconds,
            IdleTimeout = raw.IdleTimeout,
            TurnLlmTimeout = turnLlmTimeout,
            ToolExecutionTimeout = TimeSpan.FromSeconds(Math.Max(1, raw.ToolExecutionTimeoutSeconds)),
            SidecarLlmTimeout = TimeSpan.FromSeconds(Math.Max(1, raw.SidecarLlmTimeoutSeconds)),
            // Explicit value → TurnLlmTimeout fallback (if customized) → default
            FirstTokenTimeout = raw.FirstTokenTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(raw.FirstTokenTimeoutSeconds)
                : raw.TurnLlmTimeoutSeconds != 180 ? turnLlmTimeout : TimeSpan.FromSeconds(600),
            Tuning = tuning,
        };
    }

    private static SessionTuning BindTuning(IConfigurationSection section)
    {
        var tuningSection = section.GetSection("Tuning");
        var nested = tuningSection.Get<SessionTuning>() ?? new SessionTuning();

        return nested with
        {
            CompactionThreshold = ResolveValue(tuningSection, section, nameof(SessionTuning.CompactionThreshold), nested.CompactionThreshold),
            SnapshotInterval = ResolveValue(tuningSection, section, nameof(SessionTuning.SnapshotInterval), nested.SnapshotInterval),
            KeepRecentToolResults = ResolveValue(tuningSection, section, nameof(SessionTuning.KeepRecentToolResults), nested.KeepRecentToolResults),
            MaxInlineToolResultChars = ResolveValue(tuningSection, section, nameof(SessionTuning.MaxInlineToolResultChars), nested.MaxInlineToolResultChars),
            DiscoveredToolRetentionTurns = ResolveValue(tuningSection, section, nameof(SessionTuning.DiscoveredToolRetentionTurns), nested.DiscoveredToolRetentionTurns),
            DiscoveredToolMaxCount = ResolveValue(tuningSection, section, nameof(SessionTuning.DiscoveredToolMaxCount), nested.DiscoveredToolMaxCount),
            KeepRecentMessages = ResolveValue(tuningSection, section, nameof(SessionTuning.KeepRecentMessages), nested.KeepRecentMessages),
            TitleGenerationInterval = ResolveValue(tuningSection, section, nameof(SessionTuning.TitleGenerationInterval), nested.TitleGenerationInterval),
            DeterministicRetrievalEnabled = ResolveValue(tuningSection, section, nameof(SessionTuning.DeterministicRetrievalEnabled), nested.DeterministicRetrievalEnabled),
            MemoryDistillationTurnInterval = ResolveValue(tuningSection, section, nameof(SessionTuning.MemoryDistillationTurnInterval), nested.MemoryDistillationTurnInterval),
            MaxToolDescriptionChars = ResolveValue(tuningSection, section, nameof(SessionTuning.MaxToolDescriptionChars), nested.MaxToolDescriptionChars),
            MaxToolSchemaWarnChars = ResolveValue(tuningSection, section, nameof(SessionTuning.MaxToolSchemaWarnChars), nested.MaxToolSchemaWarnChars),
            MinimumRecallCompositeScore = ResolveValue(tuningSection, section, nameof(SessionTuning.MinimumRecallCompositeScore), nested.MinimumRecallCompositeScore),
        };
    }

    private static T ResolveValue<T>(IConfigurationSection preferredSection, IConfigurationSection fallbackSection, string key, T fallback)
    {
        if (preferredSection[key] is not null)
            return preferredSection.GetValue<T>(key)!;

        if (fallbackSection[key] is not null)
            return fallbackSection.GetValue<T>(key)!;

        return fallback;
    }

    /// <summary>
    /// Raw config shape matching the JSON keys (int-seconds for timeouts).
    /// Used only by <see cref="BindFromConfiguration"/>.
    /// </summary>
    private sealed record RawSessionConfig
    {
        public int MaxToolCallsPerTurn { get; init; } = 30;
        public int MemoryObserverIdleSeconds { get; init; } = 90;
        public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
        public int TurnLlmTimeoutSeconds { get; init; } = 180;
        public int ToolExecutionTimeoutSeconds { get; init; } = 90;
        public int SidecarLlmTimeoutSeconds { get; init; } = 90;
        public int FirstTokenTimeoutSeconds { get; init; }
    }
}

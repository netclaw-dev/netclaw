// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.Outputs.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

public static partial class SessionProtocol
{
    // ===== Outputs (filtered pub/sub subscriber stream) =====

    /// <summary>
    /// Base type for all session output events delivered to subscribers.
    /// Forms a discriminated union — each concrete type represents one kind
    /// of output from the LLM session.
    ///
    /// Filtering is controlled by <see cref="OutputFilter"/> flags on subscription:
    /// - Lifecycle messages (<see cref="TurnCompleted"/>, <see cref="ErrorOutput"/>,
    ///   <see cref="SessionTitleOutput"/>) are always delivered.
    /// - Content messages require the matching flag in the subscriber's filter.
    /// </summary>
    public abstract record SessionOutput : IWithSessionId, INoSerializationVerificationNeeded
    {
        public required SessionId SessionId { get; init; }

        public long TimestampMs { get; init; } = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
    }

    /// <summary>
    /// User-facing text reply from the assistant.
    /// Requires <see cref="OutputFilter.Text"/>.
    /// </summary>
    public sealed record TextOutput(string Text) : SessionOutput;

    /// <summary>
    /// Incremental text delta from the assistant while a turn is streaming.
    /// Requires <see cref="OutputFilter.Text"/>.
    /// </summary>
    public sealed record TextDeltaOutput(string Delta) : SessionOutput;

    /// <summary>
    /// Thinking/reasoning tokens from the model (e.g., Claude extended thinking).
    /// Requires <see cref="OutputFilter.Thinking"/>.
    /// </summary>
    public sealed record ThinkingOutput(string Text) : SessionOutput;

    /// <summary>
    /// Incremental thinking/reasoning delta while a turn is streaming.
    /// Requires <see cref="OutputFilter.Thinking"/>.
    /// </summary>
    public sealed record ThinkingDeltaOutput(string Delta) : SessionOutput;

    /// <summary>
    /// The model has requested a tool/function call.
    /// Requires <see cref="OutputFilter.ToolCalls"/>.
    /// </summary>
    public sealed record ToolCallOutput : SessionOutput
    {
        public required ToolCallId CallId { get; init; }

        public required ToolName ToolName { get; init; }

        /// <summary>
        /// Tool arguments as a JSON string. Kept opaque at the protocol level —
        /// tool executors parse based on their schema.
        /// </summary>
        public string? ArgumentsJson { get; init; }
    }

    /// <summary>
    /// Result of a tool execution, fed back into the conversation.
    /// Requires <see cref="OutputFilter.ToolCalls"/>.
    /// </summary>
    public sealed record ToolResultOutput : SessionOutput
    {
        public required ToolCallId CallId { get; init; }

        public required ToolName ToolName { get; init; }

        public required string Result { get; init; }

        /// <summary>The stable preflight failure code, or null after execution.</summary>
        public string? FailureCode { get; init; }
    }

    /// <summary>
    /// Token usage report for the completed turn.
    /// Requires <see cref="OutputFilter.Usage"/>.
    /// Includes context window metadata so subscribers can display usage
    /// percentage without duplicating session config.
    /// </summary>
    public sealed record UsageOutput : SessionOutput
    {
        public long? InputTokens { get; init; }

        public long? OutputTokens { get; init; }

        public long? TotalTokens { get; init; }

        public long? CachedInputTokens { get; init; }

        public long? ReasoningTokens { get; init; }

        /// <summary>
        /// Total context window capacity from <see cref="SessionConfig.ContextWindowTokens"/>.
        /// </summary>
        public int ContextWindowTokens { get; init; }

        /// <summary>
        /// Percentage of context window consumed (0.0–1.0), computed as
        /// <c>InputTokens / ContextWindowTokens</c>. Null when input tokens
        /// are unavailable.
        /// </summary>
        public double? UsagePercent { get; init; }

        // ── Server-side timing (llama.cpp timings object) ──

        /// <summary>
        /// Server-side prompt processing (prefill) time in milliseconds.
        /// Sourced from llama.cpp <c>timings.prompt_ms</c>. Null when the
        /// provider does not report timing data.
        /// </summary>
        public double? PromptMs { get; init; }

        /// <summary>
        /// Server-side output generation throughput in tokens per second.
        /// Sourced from llama.cpp <c>timings.predicted_per_second</c>.
        /// </summary>
        public double? PredictedPerSecond { get; init; }
    }

    /// <summary>
    /// Classifies how a turn ended.
    /// </summary>
    public enum TurnOutcome
    {
        /// <summary>LLM produced a final text response.</summary>
        Completed = 0,

        /// <summary>Turn failed (timeout, provider error, tool failure).</summary>
        Failed = 1,

        /// <summary>Quick-exit path — LLM was never invoked (e.g. vision-only rejection, unknown slash command).</summary>
        Skipped = 2
    }

    /// <summary>
    /// Signals that a turn has completed (all content delivered).
    /// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
    /// </summary>
    public sealed record TurnCompleted : SessionOutput
    {
        public required TurnNumber TurnNumber { get; init; }

        /// <summary>
        /// How the turn ended. Defaults to <see cref="TurnOutcome.Completed"/> for backward compatibility.
        /// </summary>
        public TurnOutcome Outcome { get; init; } = TurnOutcome.Completed;

        /// <summary>
        /// Reminder delivery key (<c>{reminderId}:{fireTimestampMs}</c>) for reminder-sourced turns.
        /// Null for non-reminder turns.
        /// </summary>
        public ReminderId? SourceReminderId { get; init; }
    }

    /// <summary>
    /// Session title was generated or updated by the LLM.
    /// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
    /// </summary>
    public sealed record SessionTitleOutput(string Title) : SessionOutput;

    /// <summary>
    /// Classifies the source of an <see cref="ErrorOutput"/> for structured
    /// diagnostics and Slack fallback messages.
    /// </summary>
    public enum ErrorCategory
    {
        /// <summary>A tool execution failed or timed out.</summary>
        ToolFailure,

        /// <summary>The LLM provider returned an error or unexpected response.</summary>
        ProviderFailure,

        /// <summary>The LLM response stream broke mid-delivery.</summary>
        StreamFailure,

        /// <summary>An operation exceeded its configured timeout.</summary>
        Timeout,

        /// <summary>Error source is unclassified (e.g. compaction failures).</summary>
        Unknown,

        /// <summary>The active model cannot accept the complete session input.</summary>
        InputCompatibility
    }

    /// <summary>
    /// An error occurred during LLM processing.
    /// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
    /// </summary>
    public sealed record ErrorOutput : SessionOutput
    {
        public required string Message { get; init; }

        /// <summary>
        /// Unique identifier for this error instance. Included in the Slack
        /// fallback message and session log so operators can cross-reference.
        /// </summary>
        public Guid CorrelationId { get; init; } = Guid.NewGuid();

        /// <summary>
        /// Classifies the source of the error for structured diagnostics.
        /// </summary>
        public ErrorCategory Category { get; init; } = ErrorCategory.Unknown;

        /// <summary>
        /// The underlying exception, if available. Not user-facing — intended
        /// for diagnostic logging by subscribers and adapters.
        /// </summary>
        public Exception? Cause { get; init; }
    }

    /// <summary>
    /// A file produced by the LLM or a tool, ready for delivery to the user.
    /// Requires <see cref="OutputFilter.Files"/>.
    /// </summary>
    public sealed record FileOutput : SessionOutput
    {
        /// <summary>Absolute path to the file on disk.</summary>
        public required string FilePath { get; init; }

        /// <summary>User-facing filename.</summary>
        public required string FileName { get; init; }

        /// <summary>MIME type of the file.</summary>
        public required MimeType MimeType { get; init; }
    }

    /// <summary>
    /// A subagent started or completed execution within a tool call.
    /// Requires <see cref="OutputFilter.ToolCalls"/>.
    /// </summary>
    public sealed record SubAgentOutput : SessionOutput
    {
        public required SubAgents.AgentName AgentName { get; init; }
        public required SubAgents.SubAgentPhase Phase { get; init; }

        /// <summary>Number of tools available to the subagent (on Started).</summary>
        public int ToolCount { get; init; }

        /// <summary>Whether the subagent completed successfully (on Completed).</summary>
        public bool Success { get; init; }

        /// <summary>Terminal outcome of the subagent run (on Completed).</summary>
        public SubAgentRunOutcome Outcome { get; init; } = SubAgentRunOutcome.Completed;

        /// <summary>Machine-readable reason when <see cref="Outcome"/> is not completed.</summary>
        public SubAgentOutcomeReason? OutcomeReason { get; init; }

        /// <summary>Wall-clock duration (on Completed).</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Durable-memory decision made by the parent session for structured findings.
        /// Values: accepted, deferred, rejected.
        /// </summary>
        public string? MemoryDecision { get; init; }

        /// <summary>
        /// Optional reason explaining why a finding was deferred/rejected.
        /// </summary>
        public string? MemoryDecisionReason { get; init; }

        /// <summary>
        /// Number of structured findings included in this subagent completion.
        /// </summary>
        public int FindingsCount { get; init; }
    }

    /// <summary>
    /// Signals that subscribers should flush any buffered streamed text immediately.
    /// Emitted before tool execution begins when the model produces text alongside
    /// tool calls, so preamble text is visible to users before tools run.
    /// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
    /// </summary>
    public sealed record BufferFlush : SessionOutput;

    /// <summary>
    /// Signals that the session is actively processing or has returned to idle.
    /// Requires <see cref="OutputFilter.ProcessingState"/> so channel adapters can
    /// opt into native busy indicators without perturbing ordinary transcript
    /// subscribers.
    /// </summary>
    public sealed record ProcessingStateOutput(bool IsProcessing) : SessionOutput
    {
        /// <summary>
        /// True when this effect is required for correctness. Optional processing
        /// indicators can be ignored by channels that do not support them.
        /// </summary>
        public bool IsRequired { get; init; }
    }

    /// <summary>
    /// Session context was compacted to stay within the context window.
    /// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
    /// </summary>
    public sealed record CompactionOutput : SessionOutput
    {
        /// <summary>Number of messages before compaction.</summary>
        public required int MessagesBefore { get; init; }

        /// <summary>Number of messages after compaction.</summary>
        public required int MessagesAfter { get; init; }

        /// <summary>Whether tool results were cleared (Phase 1).</summary>
        public bool ToolResultsCleared { get; init; }

        /// <summary>Whether summarization was applied (Phase 2).</summary>
        public bool Summarized { get; init; }

        /// <summary>
        /// The configured context window budget in tokens.
        /// </summary>
        public int ContextWindowTokens { get; init; }

        /// <summary>
        /// The input token count that triggered compaction (<see cref="SessionConfig.CompactionTokenLimit"/>).
        /// </summary>
        public long PreCompactionInputTokens { get; init; }

        /// <summary>
        /// The effective keep count used after adaptive reduction.
        /// May be less than the configured <see cref="SessionConfig.KeepRecentMessages"/>
        /// if the post-compaction estimate still exceeded the budget.
        /// </summary>
        public int KeepCountUsed { get; init; }
    }

    /// <summary>
    /// A tool requires interactive user input before it can proceed.
    /// Lifecycle — always delivered regardless of <see cref="OutputFilter"/>.
    /// Channels MUST render this as a structured interaction (buttons, prompts, etc.)
    /// and route the user's response back as a <see cref="ToolInteractionResponse"/>.
    /// </summary>
    public sealed record ToolInteractionRequest : SessionOutput
    {
        /// <summary>The kind of interaction requested. "approval" for tool approval gates.</summary>
        public required string Kind { get; init; }

        /// <summary>The tool call ID that triggered this interaction.</summary>
        public required ToolCallId CallId { get; init; }

        /// <summary>The tool that requires interaction.</summary>
        public required ToolName ToolName { get; init; }

        /// <summary>Human-readable description of what the tool wants to do.</summary>
        public required string DisplayText { get; init; }

        /// <summary>
        /// Identity of the user who initiated the turn that triggered this request.
        /// Channels can use this to ensure responses are routed for the correct user.
        /// </summary>
        public SenderId? RequesterSenderId { get; init; }

        /// <summary>
        /// Principal classification of the requester. When <see cref="PrincipalClassification.VerifiedAutomation"/>
        /// (e.g. scheduled tasks, webhooks), any channel member may approve — the sender match is skipped.
        /// </summary>
        public PrincipalClassification? RequesterPrincipal { get; init; }

        /// <summary>
        /// Exact blocked approval units shown in the prompt. For shell these are
        /// the normalized command units that approve-once can retry.
        /// </summary>
        public IReadOnlyList<string> Patterns { get; init; } = [];

        /// <summary>
        /// Verb-only projection of <see cref="Candidates"/>. Renderers (Slack,
        /// Discord) bullet-list these in the prompt body. Persisted approval
        /// matching uses <see cref="Candidates"/> instead so the directory half
        /// of each <c>(verb, directory)</c> pair is preserved.
        /// </summary>
        public IReadOnlyList<string> CandidateVerbs { get; init; } = [];

        /// <summary>
        /// Per-clause <c>(verb, directory)</c> pairs extracted from this
        /// invocation. The directory half is the path argument extracted
        /// from each clause when present, else null (in which case the
        /// matcher falls back to <see cref="Cwd"/>). The session actor
        /// reads this on <c>ApprovedAlways</c> so "Always here" persists
        /// per-clause folder-scoped grants from the actual path arguments
        /// the agent passed.
        /// </summary>
        public IReadOnlyList<ApprovalCandidate> Candidates { get; init; } = [];

        /// <summary>
        /// Resolved working directory for this invocation, used by the approval
        /// gate to evaluate folder-scoped <c>ApprovalEntry</c> records. May be
        /// null for tools whose approvals are not directory-anchored.
        /// </summary>
        public string? Cwd { get; init; }

        /// <summary>
        /// True when the invocation cannot be cleanly split into verb-chain
        /// approval units — for shell, when the command contains bash control-flow
        /// keywords (<c>for</c>/<c>while</c>/<c>do</c>/<c>done</c>/<c>then</c>/
        /// <c>fi</c>/<c>case</c>/<c>esac</c>) or unbalanced quotes/brackets.
        /// Channel adapters render only the <c>Once</c>/<c>Deny</c> buttons and
        /// surface a "complex command" hint when this is true.
        /// </summary>
        public bool IsMessy { get; init; }

        /// <summary>Available response options (e.g., approve once, approve for this chat, approve always, deny).</summary>
        public required IReadOnlyList<ToolInteractionOption> Options { get; init; }

        /// <summary>
        /// True when the executable request was accompanied by quoted adopted context.
        /// </summary>
        public bool HasAdoptedContext { get; init; }

        /// <summary>
        /// Stable sender ids present in the adopted context window for the turn.
        /// </summary>
        public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = [];

        /// <summary>
        /// True when the adopted-context window includes at least one sender other than
        /// the current authorized sender.
        /// </summary>
        public bool HasThirdPartyAdoptedContext { get; init; }

        /// <summary>
        /// True when adopted-context provenance was preserved in stored approval state.
        /// </summary>
        public bool PersistedAdoptedContext { get; init; }
    }

    /// <summary>
    /// An option presented to the user in a <see cref="ToolInteractionRequest"/>.
    /// </summary>
    public sealed record ToolInteractionOption(ApprovalOptionKey Key, string Label);
}

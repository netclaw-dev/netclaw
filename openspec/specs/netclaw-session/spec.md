# netclaw-session Specification

## Purpose

Define session identity, turn lifecycle, persistence recovery, subscriber
model, context management, and compaction behavior.

Research: `docs/research/context-management-patterns.md`

## Requirements

### Requirement: Slack thread session identity

The system SHALL key each session by `{channelId}/{threadTs}`.

#### Scenario: Route repeated thread messages to same actor

- **GIVEN** a thread session key already exists
- **WHEN** a new message arrives in the same thread
- **THEN** the same session actor handles the turn

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit typed output events to
subscribers. Subscriber delivery SHALL use a direct subscription model with
`OutputFilter` bitmask so that subscribers control which output categories they
receive (Text, Thinking, ToolCalls, Usage). Lifecycle events (TurnCompleted,
ErrorOutput, SessionTitleOutput, ToolInteractionRequest) SHALL always be
delivered regardless of filter. `SubAgentOutput` events (Started/Completed
phases) SHALL be filtered under the `ToolCalls` category.

Multiple subscribers from different channels (e.g., Slack and TUI) SHALL
coexist on the same session actor. Each subscriber receives its own filtered
copy of output independently. Adding or removing a subscriber SHALL NOT affect
other active subscribers.

The session actor SHALL create an `IApprovalChannel` instance at session start
and pass it to the tool execution pipeline. During the Processing behavior
phase, the session actor SHALL handle `ToolInteractionResponse` messages by
completing the corresponding `TaskCompletionSource` in the approval channel.
The session actor SHALL also update the `CommandApprovalCache` based on the
approval decision (session-scoped for ApproveOnce, persistent via
`ToolApprovalStore` for ApproveAlways).

#### Scenario: Persist and emit assistant reply

- **WHEN** the assistant produces a response
- **THEN** a `TurnRecorded` event is persisted
- **AND** typed output events are emitted to subscribers based on their filter

#### Scenario: Multi-subscriber filtered delivery

- **GIVEN** multiple subscribers with different OutputFilter bitmasks
- **WHEN** a turn completes with text, thinking, and usage data
- **THEN** each subscriber receives only the output categories matching their filter
- **AND** all subscribers receive lifecycle events regardless of filter

#### Scenario: Cross-channel multi-subscriber

- **GIVEN** a session originally created by the Slack channel with an active
  Slack subscriber
- **WHEN** a TUI client joins the same session via `JoinSession`
- **THEN** both Slack and TUI subscribers receive output from subsequent turns
- **AND** either subscriber disconnecting does NOT affect the other
- **AND** the session continues processing input from any attached channel

#### Scenario: Approval response handled during Processing

- **GIVEN** the session is in Processing phase with a pending approval
- **WHEN** a `ToolInteractionResponse` message arrives
- **THEN** the session actor completes the corresponding TCS in the approval
  channel
- **AND** the blocked tool task unblocks and proceeds based on the decision

#### Scenario: ToolInteractionRequest delivered as lifecycle event

- **GIVEN** a tool requires approval
- **WHEN** the pipeline emits a `ToolInteractionRequest`
- **THEN** all subscribers receive it regardless of their `OutputFilter`

### Requirement: Context window usage transparency

The system SHALL include context window metadata in `UsageOutput` events so
subscribers can display usage percentage without duplicating session config.

#### Scenario: UsageOutput includes context window metadata

- **WHEN** a turn completes with usage data
- **THEN** `UsageOutput` includes `ContextWindowTokens` (total capacity) and
  `UsagePercent` (input tokens / context window)

### Requirement: Decoupled immutable session state

The system SHALL maintain conversation state (history, turn count, title) in an
immutable `SessionState` record decoupled from the actor. State transitions
SHALL be pure functions (`Apply` methods) testable without an ActorSystem.

#### Scenario: State transitions are pure and testable

- **GIVEN** a `SessionState` instance
- **WHEN** an event is applied via `Apply()`
- **THEN** a new `SessionState` is returned with the event applied
- **AND** the original instance is not modified

### Requirement: Session recovery across restart

The system SHALL recover session state from journal and snapshots.

#### Scenario: Recover context after process restart

- **GIVEN** prior persisted turns exist
- **WHEN** the process restarts
- **THEN** the session recovers prior context before processing new input

#### Scenario: Recover state after actor kill

- **GIVEN** two completed turns are persisted
- **WHEN** the session actor is killed and a new message arrives for the same session
- **THEN** a new actor recovers from the journal with TurnCount == 2
- **AND** the next turn continues from the recovered state

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach informed
by cross-SDK research (OpenAI, LangChain, Semantic Kernel, Anthropic, Google
ADK). Before and after compaction boundaries, the session SHALL emit
high-priority memory checkpoints into the durable memory queue instead of
performing a synchronous one-off memory flush that depends on the turn path
completing all curation work inline.

#### Scenario: Compaction threshold reached

- **GIVEN** `UsageDetails.InputTokenCount` exceeds `SessionConfig.CompactionTokenLimit`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` behavior state
- **AND** incoming messages are buffered during compaction

#### Scenario: Compaction boundary emits memory checkpoint

- **GIVEN** compaction is about to run or has just completed a summary reduction
- **WHEN** the compaction boundary is reached
- **THEN** the session enqueues a high-priority memory checkpoint for durable
  curation
- **AND** the user-facing session does not wait for background curation to
  finish

#### Scenario: Tiered compaction — tool result clearing first

- **GIVEN** compaction is triggered
- **WHEN** phase 1 runs
- **THEN** old tool results are replaced with placeholders
- **AND** the N most recent tool interactions are preserved in full
- **AND** if threshold is now satisfied, no summarization LLM call is made

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) did not bring context under threshold
- **WHEN** phase 3 runs
- **THEN** a structured summarization LLM call is made with domain-specific
  section headings (task overview, current state, decisions, pending actions)
- **AND** a `SessionCompacted` event is persisted
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** compaction runs
- **THEN** tool call/result pairs are never orphaned
- **AND** older tool interactions remain representable for checkpoint extraction
  and summarization

### Requirement: Automatic pre-turn memory recall

The session system SHALL run automatic durable-memory recall before each
user-facing model turn. The recall pipeline SHALL use the incoming user
message, recent turn state, active project/session context, and policy scope to
assemble a bounded recall bundle. If recall exceeds its latency budget or the
memory substrate is unhealthy, the turn SHALL continue in degraded mode without
blocking on recall.

#### Scenario: User-facing turn receives automatic recall bundle

- **GIVEN** a session receives a new user message
- **WHEN** the turn pipeline prepares the model request
- **THEN** the session queries durable memory before the model call
- **AND** injects a bounded recall bundle when eligible memories are found

#### Scenario: Recall timeout degrades safely

- **GIVEN** the memory recall pipeline exceeds its configured time budget
- **WHEN** the session is preparing the next model call
- **THEN** the session continues without the recall bundle
- **AND** records degraded memory status for diagnostics and observability

### Requirement: Durable memory checkpoint scheduling

The session system SHALL emit durable memory checkpoints on eligible events
including explicit memory requests, stable user facts, verified tool findings,
compaction boundaries, and accepted subagent findings. Checkpoint enqueue SHALL
be durable before the turn reports a successful explicit save, and pending
checkpoints SHALL survive daemon restart.

#### Scenario: Explicit remember request is durably queued

- **GIVEN** the operator explicitly tells Netclaw to remember a fact
- **WHEN** the session handles that request
- **THEN** the session durably enqueues a high-priority checkpoint before
  reporting success
- **AND** background curation may complete after the user-facing turn finishes

#### Scenario: Pending checkpoints recover after restart

- **GIVEN** one or more memory checkpoints were queued before daemon shutdown
- **WHEN** the daemon restarts
- **THEN** the memory worker reloads the pending checkpoints
- **AND** resumes curation without losing the queued work

### Requirement: Tool context in session state

The system SHALL load available tools into session state based on the active
policy grants at session initialization. Tool definitions SHALL be refreshed
from the tool registry each time a session actor starts or recovers.

#### Scenario: Session loads granted tools at initialization

- **GIVEN** the ACL grants `shell`, `web_search`, and `mcp:memorizer` to the
  current channel and sender
- **WHEN** a session actor initializes
- **THEN** session state includes tool definitions for only the granted tool
  categories

#### Scenario: Denied tools excluded from session

- **GIVEN** the ACL does not grant `github` for the current channel
- **WHEN** a session actor initializes
- **THEN** GitHub tool definitions are not loaded into session state

### Requirement: Config hot-reload integration

The session system SHALL respond to config change notifications dispatched by
the `ConfigWatcherService`. Active sessions SHALL re-evaluate their tool grants
when ACL changes, rebuild provider connections when provider config changes,
and reconnect MCP servers when MCP profiles change.

#### Scenario: ACL change refreshes tool grants for active session

- **GIVEN** a session actor is active with tools loaded from the previous ACL
- **WHEN** the config watcher publishes an ACL change event
- **THEN** the session actor re-evaluates tool grants against the new ACL
- **AND** adds or removes tools from the session's available tool set

#### Scenario: Provider change triggers IChatClient rebuild

- **GIVEN** a session actor is using an `IChatClient` from the current provider
  configuration
- **WHEN** the config watcher publishes a provider change event
- **THEN** the session actor obtains a new `IChatClient` from the provider
  factory
- **AND** subsequent turns use the new provider configuration

#### Scenario: MCP profile change triggers server reconnection

- **GIVEN** a session actor has MCP tools loaded from connected servers
- **WHEN** the config watcher publishes an MCP profile change event
- **THEN** the session actor refreshes its MCP tool definitions
- **AND** newly added servers' tools become available
- **AND** removed servers' tools are no longer available

#### Scenario: Schedule change does not affect active sessions

- **GIVEN** a session actor is processing turns
- **WHEN** the config watcher publishes a schedule change event
- **THEN** the session actor does NOT take any action
- **AND** the `ScheduleManagerActor` handles timer reconfiguration independently

<!-- Delta from 2026-03-24 compressed-skill-index -->
# netclaw-session Delta Spec

## MODIFIED Requirements

### Requirement: Skill index context layer injection

The skill index context layer SHALL accept the session's effective trust
audience and available tool set when producing the skill index for system
prompt injection. The injected index SHALL be filtered per-audience rather
than identical for all sessions.

#### Scenario: Session prompt includes audience-filtered skill index

- **GIVEN** a session with `TrustAudience.Team` and tools `[web_search, web_fetch, file_read]`
- **WHEN** the system prompt is assembled
- **THEN** the skill index context layer injects the Team-audience compressed
  menu
- **AND** skills requiring `shell_execute` are not present in the injected index

#### Scenario: Session prompt uses pre-built menu

- **GIVEN** pre-built menus exist for each audience
- **WHEN** the system prompt is assembled for a new session
- **THEN** the context layer selects the pre-built menu matching the session's
  effective audience
- **AND** no per-turn menu generation occurs

<!-- Delta from 2026-03-24 skill-tools-and-slash-commands -->
# netclaw-session Delta Spec

## MODIFIED Requirements

### Requirement: Slash-command interception before LLM dispatch

The session actor SHALL intercept user messages starting with `/` and check
the slash-command registry before passing the message to the LLM. This
interception SHALL apply to all message sources (Slack, webhook, scheduled
jobs, reminders).

#### Scenario: Slash command intercepted before LLM

- **GIVEN** a user message starting with `/netclaw-operations`
- **WHEN** the session actor receives the message
- **THEN** the slash-command registry is checked BEFORE any LLM call
- **AND** if matched, the skill content is injected as a transient system message
- **AND** the remainder text becomes the user message for the LLM turn

<!-- Delta from 2026-03-25 refactor-llm-session-actor -->
## MODIFIED Requirements

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach informed
by cross-SDK research (OpenAI, LangChain, Semantic Kernel, Anthropic, Google
ADK). Before and after compaction boundaries, the session SHALL emit
high-priority memory checkpoints into the durable memory queue instead of
performing a synchronous one-off memory flush that depends on the turn path
completing all curation work inline.

Compaction logic SHALL be encapsulated in a `SessionCompactionPipeline` static
utility class, separate from the session actor. The pipeline accepts a
`SessionState` snapshot and `CompactionParameters` record and sends results
back to the actor via `self.Tell()`.

#### Scenario: Compaction threshold reached

- **GIVEN** `UsageDetails.InputTokenCount` exceeds the compaction token limit
  derived from `ModelCapabilities.CompactionTokenLimit(SessionTuning.CompactionThreshold)`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` phase via `TransitionTo(Compacting)`
- **AND** incoming messages are buffered during compaction

#### Scenario: Compaction boundary emits memory checkpoint

- **GIVEN** compaction is about to run or has just completed a summary reduction
- **WHEN** the compaction boundary is reached
- **THEN** the session enqueues a high-priority memory checkpoint for durable
  curation
- **AND** the user-facing session does not wait for background curation to
  finish

#### Scenario: Tiered compaction — tool result clearing first

- **GIVEN** compaction is triggered
- **WHEN** phase 1 runs
- **THEN** old tool results are replaced with placeholders
- **AND** the N most recent tool interactions are preserved in full
  (configurable via `SessionTuning.KeepRecentToolResults`)
- **AND** if threshold is now satisfied, no summarization LLM call is made

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) did not bring context under threshold
- **WHEN** phase 2 runs
- **THEN** a structured summarization LLM call is made
- **AND** a `SessionCompacted` event is persisted
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** compaction runs
- **THEN** tool call/result pairs are never orphaned
- **AND** older tool interactions remain representable for checkpoint extraction
  and summarization

### Requirement: Decoupled immutable session state

The system SHALL maintain conversation state (history, turn count, title) in an
immutable `SessionState` record decoupled from the actor. State transitions
SHALL be pure functions (`Apply` methods) testable without an ActorSystem.

Session actor internal concerns SHALL be decomposed into independently testable
modules:

- **SessionSubscriberManager**: Owns subscriber registration, deregistration,
  filtered output delivery, and watch lifecycle.
- **DeliveryRetryHandler**: Owns retry counting, eligibility tracking, and
  nudge message construction for channel delivery failures.
- **TurnStateTracker**: Owns per-turn transient counters (tool call count,
  budget nudge, duplicate detection). Provides `Reset()` for turn boundaries.
- **DiscoveredToolCache**: Owns MCP tool retention with lease countdown,
  eviction, and max count enforcement.
- **ProcessingWatchdog**: Owns operation ID tracking, timer management, and
  expiry validation for stuck-operation detection.

Each module SHALL be a plain `internal sealed` class instantiated by the actor,
not registered in DI.

#### Scenario: State transitions are pure and testable

- **GIVEN** a `SessionState` instance
- **WHEN** an event is applied via `Apply()`
- **THEN** a new `SessionState` is returned with the event applied
- **AND** the original instance is not modified

#### Scenario: Handler modules testable without ActorSystem

- **GIVEN** a `TurnStateTracker` instance
- **WHEN** `Reset()` is called
- **THEN** all per-turn counters are zeroed
- **AND** no Akka.NET types are required for the test

### Requirement: Automatic pre-turn memory recall

The session system SHALL run automatic durable-memory recall before each
user-facing model turn. Recall logic SHALL be encapsulated in a
`SessionRecallManager` class that owns the turn recall cache and progressive
exclusion set. The manager SHALL provide `ResolveForTurn()`,
`InjectIntoMessages()`, `ResetForNewTurn()`, and `ResetForCompaction()` methods.

The recall pipeline SHALL use the incoming user message, recent turn state,
active project/session context, and policy scope to assemble a bounded recall
bundle. If recall exceeds its latency budget or the memory substrate is
unhealthy, the turn SHALL continue in degraded mode without blocking on recall.

#### Scenario: User-facing turn receives automatic recall bundle

- **GIVEN** a session receives a new user message
- **WHEN** the turn pipeline prepares the model request
- **THEN** the `SessionRecallManager` resolves recall before the model call
- **AND** injects a bounded recall bundle when eligible memories are found

#### Scenario: Recall timeout degrades safely

- **GIVEN** the memory recall pipeline exceeds its configured time budget
- **WHEN** the session is preparing the next model call
- **THEN** the session continues without the recall bundle
- **AND** records degraded memory status for diagnostics and observability

### Requirement: Session title generation

Title generation SHALL be encapsulated in a `SessionTitleGenerator` static
utility class. The generator SHALL determine whether to generate a title based
on turn count and `SessionTuning.TitleGenerationInterval`, and fire a sidecar
LLM call that sends `TitleGenerationCompleted` back to the actor. Title
generation is best-effort — failures are silently logged and do not affect
session operation.

#### Scenario: Title generated on first turn

- **GIVEN** a session completes turn 1
- **WHEN** `SessionTitleGenerator.ShouldGenerate(1, interval)` is evaluated
- **THEN** the result is `true`
- **AND** a sidecar LLM call is fired to generate a title

#### Scenario: Title generation failure is silent

- **GIVEN** the sidecar LLM call for title generation fails
- **WHEN** the error is caught
- **THEN** a warning is logged
- **AND** the session continues without a title update

### Requirement: LLM invocation encapsulation

LLM call execution and streaming SHALL be encapsulated in a `SessionLlmInvoker`
static utility class. The invoker SHALL handle timeout wrapping, streaming delta
forwarding via `self.Tell(LlmResponseDeltaReceived)`, and error packaging as
`LlmCallFailed`. Dynamic context layer injection SHALL be a static method on
this class.

#### Scenario: Two-phase LLM call timeout

The system SHALL enforce two separate timeout phases for LLM streaming calls:

- **Phase 1 — First-Token Timeout**: The system SHALL wait up to
  `FirstTokenTimeout` (default 600s) for the first streaming delta. This
  covers the prefill phase where the model processes input context.
- **Phase 2 — Stream-Idle Timeout**: Once the first delta arrives, the
  system SHALL switch to `StreamIdleTimeout` (default 120s). This resets
  on every subsequent delta and detects dead streams.

- **GIVEN** an LLM streaming call is in progress and no deltas have arrived
- **WHEN** the `FirstTokenTimeout` elapses
- **THEN** the invoker sends `LlmCallFailed` with a `TimeoutException`
- **AND** the error message indicates the provider did not respond

- **GIVEN** an LLM streaming call has produced at least one delta
- **WHEN** no further deltas arrive within `StreamIdleTimeout`
- **THEN** the watchdog fires and the turn fails with `ErrorCategory.Timeout`
- **AND** the error message indicates the stream stopped unexpectedly

Backward compat: if `TurnLlmTimeoutSeconds` is configured but the new
properties are not, both phases use `TurnLlmTimeout`.

#### Scenario: Streaming deltas forwarded to actor

- **GIVEN** an LLM streaming call is in progress
- **WHEN** text content chunks arrive
- **THEN** each chunk after the first is forwarded as `LlmResponseDeltaReceived`
- **AND** the first chunk is held until the second arrives (single-chunk optimization)
- **AND** the watchdog refreshes with `StreamIdleTimeout` on each delta

### Requirement: Tool execution encapsulation

Tool execution SHALL be encapsulated in a `SessionToolExecutionPipeline` static
utility class. The pipeline SHALL execute tool calls in parallel, clamp oversized
results to `SessionTuning.MaxInlineToolResultChars`, track sub-agent activity,
and send `ToolExecutionCompleted` or `ToolExecutionFailed` back to the actor.

#### Scenario: Parallel tool execution

- **GIVEN** an LLM response contains 3 tool calls
- **WHEN** `SessionToolExecutionPipeline.ExecuteToolsAsync()` runs
- **THEN** all 3 tool calls execute in parallel
- **AND** results are collected and sent as a single `ToolExecutionCompleted`

#### Scenario: Tool execution timeout

- **GIVEN** tool execution is in progress
- **WHEN** the configured `ToolExecutionTimeout` elapses
- **THEN** the pipeline sends `ToolExecutionFailed` with a `TimeoutException`

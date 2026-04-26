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

The persisted `TurnRecorded` event SHALL carry an optional
`SourceReminderId` field (protobuf tag 5, additive). When a
`SendUserMessage` arrives with `MessageSource.ReminderId` set, the
resulting `TurnRecorded` event SHALL copy that value into
`SourceReminderId` so that reminder-originated turns are distinguishable
in the journal (for forensics) and survive recovery (so the in-memory
dedup set can be rebuilt via event replay).

The persisted `TurnRecorded` event SHALL carry an optional
`SourceBackgroundJobId` field (protobuf tag 6, additive). When a
`SendUserMessage` arrives with `MessageSource.BackgroundJobId` set, the
resulting `TurnRecorded` event SHALL copy that value into
`SourceBackgroundJobId` so that background-job-originated turns are
distinguishable in the journal and survive recovery (so the in-memory
dedup set can be rebuilt via event replay).

`SessionState` SHALL maintain an `ActiveBackgroundJobs` dictionary
(`ImmutableDictionary<string, ActiveJobInfo>`) persisted to the Akka journal.
`ActiveJobInfo` SHALL carry `JobId`, `Command`, `Rationale`, and `StartedAt`.
When a background job is started, the session SHALL persist an event adding
the job entry. When a background job result is delivered, the session SHALL
persist an event removing the job entry and adding the job ID to a dedup
set (mirroring `ProcessedReminderIds`). The working context SHALL surface
active background jobs with their rationales so the LLM knows what it is
waiting for after compaction or session resumption.

Background job completion delivered through `DeliverTrustedSessionTurn` SHALL
be treated as the trusted completion of the original tool execution, matching
the trust semantics of synchronous shell results. The session SHALL process the
delivery only within the originating session and the persisted originating
audience/boundary captured for that job.

#### Scenario: Persist and emit assistant reply

- **WHEN** the assistant produces a response
- **THEN** a `TurnRecorded` event is persisted
- **AND** typed output events are emitted to subscribers based on their filter

#### Scenario: Reminder-originated turn carries SourceReminderId

- **GIVEN** the session receives a `SendUserMessage` whose
  `MessageSource.ReminderId` equals `"daily-digest:1712000000000"`
- **WHEN** the turn completes and `TurnRecorded` is persisted
- **THEN** the persisted event has
  `SourceReminderId = "daily-digest:1712000000000"`
- **AND** the event is replayable as a normal turn on recovery

#### Scenario: Background job started persisted to session state

- **GIVEN** the pipeline routes a tool call to background execution
- **WHEN** `BackgroundJobStarted` is received by the session
- **THEN** an `ActiveJobInfo` entry is added to `ActiveBackgroundJobs`
- **AND** the addition is persisted to the journal

#### Scenario: Background job result delivery removes active job

- **GIVEN** a background job result arrives via `DeliverTrustedSessionTurn`
- **WHEN** the session processes the delivery
- **THEN** the job entry is removed from `ActiveBackgroundJobs`
- **AND** the job ID is added to the dedup set
- **AND** both changes are persisted to the journal

#### Scenario: Session applies trusted delivery with originating scope

- **GIVEN** a background job result arrives via `DeliverTrustedSessionTurn`
- **AND** the job has persisted originating audience/boundary metadata
- **WHEN** the session processes the delivery
- **THEN** the turn is treated with the same trust semantics as a synchronous
  shell result for that session
- **AND** processing remains scoped to the persisted originating
  audience/boundary

#### Scenario: Active jobs visible in working context

- **GIVEN** a session has active background jobs
- **WHEN** the working context is built for the LLM
- **THEN** the context includes a section listing pending jobs with their
  rationales and start times

#### Scenario: Active jobs survive session recovery

- **GIVEN** a session with active background jobs has been passivated
- **WHEN** the session rehydrates from the journal
- **THEN** `ActiveBackgroundJobs` is restored with all entries
- **AND** the background job dedup set is restored

#### Scenario: Non-reminder turn has null SourceReminderId

- **GIVEN** the session receives a regular user `SendUserMessage` with
  `MessageSource.ReminderId = null`
- **WHEN** the turn completes and `TurnRecorded` is persisted
- **THEN** the persisted event has `SourceReminderId = null`

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

The system SHALL compact long session history using a tiered approach that
produces a structured summary surviving successive compactions without
grounding decay, enforces tool call/result pair integrity at the compaction
boundary, and disambiguates the self session from any foreign session
identifiers referenced in the discarded window. Before and after compaction
boundaries, the session SHALL emit high-priority memory checkpoints into the
durable memory queue instead of performing a synchronous one-off memory flush
that depends on the turn path completing all curation work inline.

Compaction logic SHALL be encapsulated in a `SessionCompactionPipeline` static
utility class, separate from the session actor. The pipeline accepts a
`SessionState` snapshot and `CompactionParameters` record and sends results
back to the actor via `self.Tell()`.

The compaction observer LLM SHALL produce output in a fixed structured
format with nine sections: Primary Request and Intent, Key Technical
Concepts, Files and Code Sections, Problem Solving, Pending Tasks, Task
Evolution, Current Work, Next Step, and Required Files. The Task Evolution
section SHALL contain direct quotes from user messages that changed the
task, to prevent drift across successive compactions.

The compaction summary message SHALL be wrapped with a distinctive header
of the form `[session-summary session:{id}]` so that consumers (the
observer on successive compactions, the reducer, and the UI) can
recognize it as a prior-compaction artifact and preserve it across
successive compactions without relying on a separately-persisted index.

The compaction observer SHALL receive the self `SessionId` in its system
prompt and SHALL explicitly mark any foreign session identifiers in
observations as `session:{id}` rather than conflating them with the self
session.

The compaction observer system prompt SHALL include a rule instructing the
model to preserve any prior structured summary block verbatim and update
in place, rather than re-summarizing or rewriting it.

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
- **WHEN** the observer LLM call runs
- **THEN** the observer produces a summary containing the nine fixed sections
  (Primary Request and Intent, Key Technical Concepts, Files and Code Sections,
  Problem Solving, Pending Tasks, Task Evolution, Current Work, Next Step,
  Required Files)
- **AND** the Task Evolution section contains direct quotes from user
  messages that changed the task
- **AND** the summary is wrapped with a `[session-summary session:{id}]`
  header and stored in the compacted history
- **AND** a `SessionCompacted` event is persisted carrying the compacted
  messages
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Successive compactions do not re-summarize prior summary

- **GIVEN** a session that has been compacted, with a prior
  `[session-summary session:{id}]` message in history
- **WHEN** a subsequent compaction is triggered
- **THEN** the observer system prompt instructs the model to preserve the
  prior summary block verbatim and update its sections in place
- **AND** the reducer's user-message-boundary walk-back preserves the
  prior summary message in the kept window (the summary is a User-role
  message with a distinctive header)

#### Scenario: Self session disambiguation in observer

- **GIVEN** the discarded window contains a reference to a session identifier
  that is not the running session (e.g. the agent was investigating another
  session via a tool call)
- **WHEN** the observer LLM call runs
- **THEN** the observer system prompt includes the self session id
- **AND** the produced summary marks the foreign session as `session:{id}`
- **AND** the produced summary does not conflate the foreign session with the
  self session

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** the extractive reducer selects the kept window
- **THEN** the kept window starts on a `User`-role message (not a
  `Tool`-role message and not an `Assistant` message that contains
  `FunctionCallContent` without a matching preceding user turn)
- **AND** tool call/result pairs are never split across the compaction
  boundary
- **AND** older tool interactions remain representable in the journal for
  checkpoint extraction and summarization

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

#### Scenario: LLM streaming inactivity timeout

The system SHALL enforce a single reset-on-delta inactivity timeout for LLM
streaming calls using `FirstTokenTimeout` (default 600s). The timer starts
when the LLM call is fired and resets on every streaming delta received.

- **GIVEN** an LLM streaming call is in progress
- **WHEN** no deltas arrive within `FirstTokenTimeout` of the call start or
  the last received delta
- **THEN** the watchdog fires and the turn fails with `ErrorCategory.Timeout`
- **AND** the error message indicates the stream timed out due to inactivity

Backward compat: if `TurnLlmTimeoutSeconds` is configured but
`FirstTokenTimeoutSeconds` is not, `FirstTokenTimeout` uses `TurnLlmTimeout`.

#### Scenario: Streaming deltas forwarded to actor

- **GIVEN** an LLM streaming call is in progress
- **WHEN** text content chunks arrive
- **THEN** each chunk after the first is forwarded as `LlmResponseDeltaReceived`
- **AND** the first chunk is held until the second arrives (single-chunk optimization)
- **AND** the watchdog refreshes with `FirstTokenTimeout` on each delta

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

### Requirement: Reminder redelivery best-effort dedup

`SessionState` SHALL maintain an in-memory
`ImmutableHashSet<string> ProcessedReminderIds`, folded in the
`Apply(TurnRecorded)` handler from each recovered or live event's
`SourceReminderId` when non-null. `Apply(SessionCompacted)` SHALL preserve
the set across compaction (similar to how `WorkingContext` is preserved).
The set SHALL NOT be persisted to `SessionSnapshot`: on snapshot-based
recovery the set starts empty and rebuilds from post-snapshot event replay
via the normal `Apply(TurnRecorded)` path.

`LlmSessionActor` SHALL pre-check `cmd.Source?.ReminderId` against
`ProcessedReminderIds` at the top of both the `Ready`-phase
`HandleIncomingUserMessage` method and the `Processing`-phase
`Command<SendUserMessage>` buffer handler. On a dedup hit, the session
SHALL reply `CommandAck` to the sender without modifying state,
persisting events, or dispatching an LLM call.

The dedup check SHALL happen *before* any audience enforcement, ACL
evaluation, or prompt construction, so that a redelivery from
Akka.Reminders is handled entirely in memory and cannot trigger spurious
side effects.

**Best-effort semantics**: dedup is not guaranteed across snapshot
recovery boundaries. A reminder processed before a snapshot and
redelivered after a snapshot-based recovery will be processed as a fresh
turn. This is an explicitly accepted tradeoff — the LLM itself typically
recognizes a duplicate prompt in its recent context and responds
appropriately, and persisting the dedup ledger to snapshot adds
complexity without proportional value.

#### Scenario: Redelivered reminder hits dedup in Ready phase

- **GIVEN** the session is in `Ready` phase with
  `ProcessedReminderIds = { "check-pr:1712000000000" }` rebuilt from
  post-snapshot journal replay
- **WHEN** a `SendUserMessage` arrives with
  `MessageSource.ReminderId = "check-pr:1712000000000"`
- **THEN** the session replies `CommandAck` to the sender
- **AND** no `TurnRecorded` event is persisted
- **AND** the LLM is not invoked
- **AND** a `reminder_mode_b_dedup_hit` log entry is emitted

#### Scenario: Redelivered reminder hits dedup in Processing phase

- **GIVEN** the session is in `Processing` phase (LLM call in flight) with
  a dedup set containing `"nightly-report:1712005000000"`
- **WHEN** a `SendUserMessage` redelivery arrives with the same reminder ID
- **THEN** the session replies `CommandAck` without buffering the message
- **AND** the in-flight turn is unaffected

#### Scenario: Dedup set rebuilt from post-snapshot event replay

- **GIVEN** a session journal contains three `TurnRecorded` events after
  the most recent snapshot, two with non-null `SourceReminderId` and one
  regular user turn
- **WHEN** the session actor recovers from the snapshot and replays
  subsequent events
- **THEN** `ProcessedReminderIds` contains exactly the two reminder IDs
  from the post-snapshot events
- **AND** subsequent redeliveries of those reminders are deduped

#### Scenario: Dedup set starts empty on snapshot-only recovery

- **GIVEN** a session journal where all `TurnRecorded` events are
  older than the most recent snapshot
- **WHEN** the session actor recovers from that snapshot
- **THEN** `ProcessedReminderIds` starts empty (the snapshot does not
  carry the set)
- **AND** a subsequent redelivery of a pre-snapshot reminder (still
  within `MaxDeliveryWindow`) is NOT deduped and is processed as a fresh
  turn
- **AND** the outcome is logged but not treated as an error

#### Scenario: Non-reminder user messages are not deduped

- **GIVEN** a session with a populated `ProcessedReminderIds` set
- **WHEN** a regular `SendUserMessage` arrives with
  `MessageSource.ReminderId = null`
- **THEN** the message is processed normally regardless of the dedup set

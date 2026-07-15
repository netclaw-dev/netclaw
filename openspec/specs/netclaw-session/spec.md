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
The session actor SHALL also record approvals through `IToolApprovalService`
based on the approval decision (session-scoped for ApproveOnce, persistent for
ApproveAlways).

The persisted `TurnRecorded` event SHALL carry an optional
`SourceReminderId` field (protobuf tag 5, additive). When a
`SendUserMessage` arrives with `MessageSource.ReminderId` set, the
resulting `TurnRecorded` event SHALL copy that value into
`SourceReminderId` so that reminder-originated turns are distinguishable
in the journal (for forensics) and survive recovery (so the in-memory
dedup set can be rebuilt via event replay).

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

### Requirement: Persisted adopted-context metadata separates truthful provenance from third-party policy

When the session persists or reuses an adopted-context record, it SHALL preserve
the full adopted window truthfully and SHALL NOT collapse self-only adopted
history into "no adopted context."

For persisted session metadata:

- `HasAdoptedContext` SHALL mean the adopted window is non-empty.
- Adopted-speaker provenance SHALL include all sender ids present in that
  adopted window.
- `HasThirdPartyAdoptedContext` SHALL be tracked as a separate policy concept and
  SHALL be true only when any adopted sender id differs from the current
  authorized author of the executable message.

This metadata split SHALL coexist with the existing trust model that adopted
context is quoted, non-executable context and only the current authorized
message is executable.

#### Scenario: Persisted record keeps self-only adopted window truthful

- **GIVEN** an adopted-context record is written for an authorized turn
- **AND** every adopted sender id matches the current authorized sender
- **WHEN** the session persists the record
- **THEN** `HasAdoptedContext` is true
- **AND** adopted-speaker provenance includes that sender id
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Persisted record marks third-party policy separately

- **GIVEN** an adopted-context record is written for an authorized turn
- **AND** the adopted window includes a sender id different from the current
  authorized sender
- **WHEN** the session persists the record
- **THEN** adopted-speaker provenance includes all adopted sender ids
- **AND** `HasThirdPartyAdoptedContext` is true

#### Scenario: Adopted context remains non-executable after metadata split

- **GIVEN** a persisted record reports `HasAdoptedContext=true`
- **WHEN** the session later uses that record for audit, retry, or recovery
- **THEN** the adopted window remains quoted, non-executable context
- **AND** only the current authorized message remains executable

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

Tool execution SHALL be encapsulated in a composed `SessionToolExecutionPipeline`
whose unconditional production services are required constructor dependencies.
The session actor SHALL submit one cohesive batch command whose tool authority is
derived from the admitted `TurnContext`; callers SHALL NOT be able to supply a
second, conflicting authority source. Genuinely unavailable runtime capabilities,
including background-job dispatch, SHALL be represented explicitly while retaining
their existing behavior. The pipeline SHALL execute tool calls in parallel, track
sub-agent activity, and send completion or failure messages back to the actor.

The pipeline SHALL NOT itself bound or clamp tool-result size. Bounding to the
inline budget and spilling overflow remains centralized in
`DispatchingToolExecutor`, so the pipeline stores the result already bounded by
the dispatcher. `SessionTuning.MaxInlineToolResultChars` remains the session
content budget used for tools without a smaller per-tool override.

#### Scenario: Parallel tool execution

- **GIVEN** an admitted turn whose LLM response contains three tool calls
- **WHEN** the session submits its `SessionToolBatch`
- **THEN** all three tool calls execute in parallel with fresh call-local state
- **AND** results are collected and returned through the existing actor protocol

#### Scenario: Conflicting authority cannot be supplied

- **GIVEN** a session constructs a tool batch from an admitted `TurnContext`
- **WHEN** the batch derives its tool run scope
- **THEN** session, audience, boundary, channel, delivery, and interactive-approval authority come from that turn context
- **AND** the caller has no initializer or alternate constructor for replacing the derived authority

#### Scenario: Background manager is unavailable

- **GIVEN** a valid background-capable shell request and no registered background-job manager
- **WHEN** the batch executes
- **THEN** the request executes synchronously as it did before the composition refactor
- **AND** manager absence is not inferred from a nullable security dependency

#### Scenario: Tool execution timeout

- **GIVEN** tool execution is in progress
- **WHEN** the configured `ToolExecutionTimeout` elapses
- **THEN** the pipeline sends `ToolExecutionFailed` with a `TimeoutException`

#### Scenario: Oversized result already bounded by the dispatcher

- **GIVEN** a tool returns an oversized result
- **WHEN** it reaches the pipeline
- **THEN** the pipeline stores it as-is without re-clamping

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

### Requirement: Persist adopted-context audit records

When an authorized threaded turn adopts unsynced prior thread messages, the session system SHALL durably persist or reuse an adopted-context record for
audit before execution continues for that authorized turn.

The persisted record SHALL include at minimum:

- session or thread identity
- authorizer identity for the current authorized message
- sync lower bound and upper bound
- included message ids
- included message timestamps
- included message sender ids
- authority-at-inclusion for each included message
- the exact canonical attribution projection presented to the model
- enough linkage to correlate retries or recovery for the same authorized
  message id

The idempotency basis for this record SHALL be the current authorized message
identity within the session or thread. If the same authorized message is
retried or replayed after adopted-context persistence has already succeeded, the
session SHALL reuse the existing adopted-context record and exact persisted
projection rather than persist a duplicate or re-derive a new projection from
raw thread history.

If the authorized message has no unsynced adopted gap, the session SHALL NOT
persist an adopted-context record and SHALL treat the turn as an ordinary
authorized turn.

If adopted-context persistence fails, the system SHALL NOT enqueue the
authorized turn and SHALL NOT advance the authorized-sync watermark.

If durable turn completion is not observed after the adopted-context record has
been persisted, the durable authorized-sync watermark SHALL remain unchanged and
the persisted record SHALL remain a fail-closed audit artifact that retries or
recovery can reuse rather than proof that the turn ran.

#### Scenario: Adopted-context record persisted for authorized turn

- **GIVEN** an authorized threaded message adopts three unsynced prior messages
- **WHEN** the turn is prepared
- **THEN** the session persists one adopted-context audit record
- **AND** the record contains the authorizer, sync bounds, included messages,
  authority-at-inclusion, and the exact canonical projection

#### Scenario: Persistence failure blocks enqueue

- **GIVEN** an authorized threaded message would adopt unsynced prior messages
- **WHEN** adopted-context persistence fails
- **THEN** the authorized turn is not enqueued
- **AND** the authorized-sync watermark does not advance

#### Scenario: Missing durable completion leaves audit without watermark advance

- **GIVEN** the adopted-context record has been persisted
- **WHEN** durable turn completion is not observed for that authorized message
- **THEN** the durable authorized-sync watermark remains unchanged
- **AND** the persisted adopted-context record is treated as a non-executed
  audit artifact that retries or recovery may reuse

#### Scenario: Same authorized message retry reuses persisted record

- **GIVEN** an adopted-context record already exists for a specific current
  authorized message identity
- **AND** a prior enqueue attempt for that message did not complete
- **WHEN** the system retries that same authorized message
- **THEN** the existing adopted-context record is reused
- **AND** the execution linkage is updated without persisting a duplicate

### Requirement: Adopted context is non-executable quoted context

The session SHALL treat adopted-context material as quoted context rather than
ordinary authoritative turn history unless a later explicit change says
otherwise. Only the current authorized message in that turn SHALL be executable.

Adopted or pending unauthorized content SHALL NOT directly:

- dispatch a model turn on its own;
- enter slash-command dispatch;
- originate tool approvals;
- originate tool calls, reminders, or jobs; or
- originate direct durable memory writes.

#### Scenario: Adopted context cannot execute without current authorized message

- **GIVEN** a thread contains only unauthorized pending messages after the last
  watermark
- **WHEN** no authorized message arrives
- **THEN** the session does not execute a turn from that pending material

#### Scenario: Authorized turn executes only current message

- **GIVEN** an authorized turn includes adopted context plus the current
  authorized message
- **WHEN** the session executes the turn
- **THEN** only the current authorized message is treated as executable
- **AND** the adopted context remains quoted supporting material

### Requirement: Canonical projection is derived from persisted record

The threaded adapter MAY construct the model-visible multi-speaker projection before session handoff. When adopted context exists, the session SHALL persist
that exact projection together with the adopted-message metadata before
execution continues.

Retries, replay, or crash recovery for the same authorized message id SHALL
reuse the persisted adopted-context record keyed by that authorized message id
rather than reconstruct a different projection from raw thread history.

If no adopted-context record exists because the turn had no unsynced gap, the
model SHALL receive only the current authorized message and no empty
adopted-context projection.

#### Scenario: Audit replay matches model-visible projection

- **GIVEN** an adopted-context record exists for a turn
- **WHEN** an operator reviews that turn later
- **THEN** the stored canonical projection matches the attribution framing that
  was shown to the model

### Requirement: Approval-paused turn lifecycle preserves original context

The persisted session turn lifecycle SHALL preserve the original turn context across approval pause, approval response, tool redrive, follow-up LLM calls, continuation tool calls, and turn completion. The context SHALL remain active until the resumed turn completes, fails, or is explicitly abandoned by a new user message.

#### Scenario: Context remains active through continuation LLM call

- **GIVEN** a recovered approval response redrives a parked tool batch
- **WHEN** the tool result is appended and the session invokes the continuation LLM call
- **THEN** the continuation call uses the restored turn context
- **AND** the exposed tool list is filtered for the original turn audience and boundary

#### Scenario: Context remains active through continuation tool call

- **GIVEN** a recovered approval redrive has resumed a turn
- **WHEN** the continuation LLM response requests another tool call
- **THEN** that tool call is dispatched with the same restored turn context
- **AND** approval capability and channel type match the original turn

#### Scenario: Context clears when resumed turn completes

- **GIVEN** a resumed approval-paused turn completes successfully
- **WHEN** `TurnCompleted` is emitted
- **THEN** the session clears the active approval turn context
- **AND** the next user message starts with a new turn context

### Requirement: Approval recovery tests cover context directly

Session approval recovery tests SHALL include direct coverage for turn-context construction, persistence, restoration, and projection into tool and memory contexts. End-to-end cold-recovery tests SHALL remain for user-visible recovery behavior, but field-by-field context propagation SHOULD be covered through focused tests where possible.

#### Scenario: Direct projection test replaces field-only integration coverage

- **GIVEN** a persisted turn context has audience, boundary, channel type, requester, approval capability, and adopted-context state
- **WHEN** the session projects that context into tool execution and memory-policy inputs
- **THEN** the projected values match the persisted context
- **AND** the assertion does not require a full actor cold-recovery scenario for each individual field

#### Scenario: End-to-end recovery test remains for user-visible behavior

- **GIVEN** a user approves a pending tool prompt after session recovery
- **WHEN** the session redrives the parked tool batch
- **THEN** the user observes the tool result and final assistant response
- **AND** no duplicate approval prompt is emitted for the approved call

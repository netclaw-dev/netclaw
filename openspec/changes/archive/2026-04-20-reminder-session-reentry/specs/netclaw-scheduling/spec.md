# netclaw-scheduling Delta Spec

## MODIFIED Requirements

### Requirement: Isolated task execution

Each scheduled task execution SHALL run in either a **fresh isolated session**
(Mode A — external notification) or **re-enter the originating session**
(Mode B — session check-back), determined at set time by whether the
reminder carries an explicit `ReportToChannel`. Mode A sessions SHALL load the
agent personality and project context overlays and SHALL NOT share state with
interactive sessions. Mode B executions SHALL reuse the persisted state of
the originating session actor (via Akka.Persistence rehydration if the
session has passivated) and SHALL NOT create a new session.

Execution MAY trust the stored reminder audience because reminder minting and
import paths SHALL validate the persisted audience before the definition is
saved. In both modes, the effective audience at execution time SHALL be the
stored reminder audience, not the live audience of the originating session.

#### Scenario: Fresh session per Mode A execution

- **GIVEN** a reminder persisted with `ReportToChannel` set and `SessionId = null`
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** agent personality is loaded from soul files

#### Scenario: Session re-entry per Mode B execution

- **GIVEN** a reminder persisted with `SessionId` set and `ReportToChannel = null`
- **WHEN** the timer tick triggers execution
- **THEN** the existing session actor for the persisted `SessionId` is
  addressed (rehydrating from Akka.Persistence if currently passivated)
- **AND** NO new session actor is created with a `schedule/...` entity key
- **AND** the reminder turn is delivered as a `SendUserMessage` whose
  `MessageSource.ChannelType` matches the stored `OriginChannelType`

#### Scenario: Scheduled session isolated from interactive sessions

- **GIVEN** an interactive Slack session exists for the same user
- **WHEN** a Mode A scheduled task executes
- **THEN** the scheduled session does not read or modify interactive session
  state
- **AND** the interactive session does not see scheduled session turns

#### Scenario: Task tool grants applied to session

- **GIVEN** a scheduled task specifies `tool_grants: ["web_search", "web_fetch"]`
- **WHEN** the task session starts
- **THEN** only the granted tools are available to the session
- **AND** ungrantable tools are not offered to the LLM

#### Scenario: Execution uses validated stored audience

- **GIVEN** a reminder definition was accepted with stored audience `Public`
- **WHEN** the reminder later executes on schedule
- **THEN** the execution session uses stored audience `Public`
- **AND** it does not recompute audience from the deployment posture default

### Requirement: Result reporting

Task execution results SHALL be delivered according to the execution mode.

- **Mode A** (external notification): results SHALL be posted to the
  notification target stored on the reminder definition. Notification targets
  SHALL always be canonical identifiers produced by
  `IReminderTargetResolver` (never raw LLM-supplied strings).
- **Mode B** (session check-back): the reminder turn is routed through the
  originating channel's existing inbound handling path. The daemon hosts
  two server-side gateways, both of which implement a
  `Receive<DeliverTrustedSessionTurn>` handler that reuses the gateway's
  existing routing code. The reminder dispatcher SHALL tell the appropriate
  gateway based on `OriginChannelType`: `ChannelType.Slack` →
  `SlackGatewayActor`; `ChannelType.Tui` or `ChannelType.SignalR` →
  `SignalRGatewayActor`. The channel-level inbound ACL SHALL be bypassed
  because the reminder's audience is already validated at minting time.
  Any other `OriginChannelType` SHALL be rejected at `set_reminder` time.

Both modes SHALL support a silent-unless-notable mode where routine results
are suppressed and only notable findings are posted (Mode A) or delivered as
a new turn (Mode B).

#### Scenario: Mode A results posted to configured channel

- **GIVEN** a scheduled task has `report_to.channel` configured with a
  canonical channel ID
- **WHEN** the task execution completes with results
- **THEN** the results are posted to the configured Slack channel via the
  reminder's isolated execution session

#### Scenario: Mode B Slack delivery routes through existing gateway chain

- **GIVEN** a Mode B reminder created from a Slack thread session with
  `OriginChannelType = Slack`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s `SlackGatewayActor`
  with a `DeliverTrustedSessionTurn` message carrying the originating
  `SessionId`, reminder prompt, and trusted `MessageSource`
- **AND** the gateway's handler parses the `SessionId` into
  `(channelId, threadTs)` and uses its existing
  `Context.Child(name).GetOrElse(...)` lookup to reach the conversation
  actor
- **AND** `conversation.Forward(msg)` preserves `Sender`
- **AND** `SlackConversationActor`'s handler uses the same lookup pattern
  to reach the thread binding actor
- **AND** `binding.Forward(msg)` preserves `Sender`
- **AND** `SlackThreadBindingActor`'s handler reads `Sender`, builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender`, and offers it to
  the pipeline queue
- **AND** the reminder turn is delivered through the normal `ChannelInput`
  → `ChannelPipeline` → `SendUserMessage` → session pipeline
- **AND** the session's streaming response is posted back to the original
  Slack thread via the binding's existing output sink
- **AND** `SlackAclPolicy.EvaluateInbound` is NOT called

#### Scenario: Mode B SignalR delivery routes through existing gateway chain

- **GIVEN** a Mode B reminder created from a SignalR session (including
  TUI) with `OriginChannelType` = `Tui` or `SignalR` and `SessionId` =
  `signalr/{guid}`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s `SignalRGatewayActor`
  with a `DeliverTrustedSessionTurn` message
- **AND** `SignalRMessageExtractor.EntityId` matches the message via its
  `IWithSessionId` fallback and extracts the session GUID
- **AND** `GenericChildPerEntityParent` routes the message to the existing
  `SignalRSessionActor` child for that session (creating one if needed)
- **AND** `SignalRSessionActor`'s handler reads `Sender`, builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender`, and offers it to
  the pipeline queue
- **AND** if a SignalR client is currently connected, the streaming
  response reaches the client in real time via the existing bridge
- **AND** if no client is currently connected, the session still processes
  the turn and persists `TurnRecorded`; streaming output is dropped per
  the existing `OverflowStrategy.DropHead` behavior and is visible on
  next `ResumeSessionAsync`

#### Scenario: Silent-unless-notable suppresses routine results

- **GIVEN** a scheduled task is configured with silent-unless-notable mode
- **WHEN** the task execution completes with no notable findings
- **THEN** no message is posted (Mode A) or delivered as a turn (Mode B)
- **AND** the execution is logged as completed with no notable output

#### Scenario: Notable results always posted

- **GIVEN** a scheduled task is configured with silent-unless-notable mode
- **WHEN** the task execution produces notable findings
- **THEN** the results are delivered via the mode-appropriate path
- **AND** the findings are clearly presented

### Requirement: Reminder notification target validation

The `set_reminder` tool SHALL validate any LLM-supplied `reportToChannel`
value through a transport-agnostic `IReminderTargetResolver` abstraction
before persisting the reminder definition. Validation SHALL accept
human-readable Slack handles (`#channel-name`, `@username`) and raw Slack
identifiers, and SHALL persist the resolver's canonical identifier — never
the raw LLM input. Unresolvable targets SHALL cause the tool invocation to
fail immediately with an error message the LLM can act on. When no
notification channel transport is registered in the host and the LLM
supplies a `reportToChannel`, the tool SHALL fail loudly with a
"no notification channel transport configured" error rather than silently
deferring the failure to reminder execution time.

When the LLM does **not** supply `reportToChannel`, the tool SHALL NOT
synthesize one by splitting the calling `context.SessionId`. Instead, if
`context.SessionId` is present, the tool SHALL persist the reminder in
**Mode B** with `SessionId = context.SessionId`, `OriginChannelType =
context.ChannelType`, and `ReportToChannel = null`. The tool SHALL
reject Mode B at set time if `context.ChannelType` is not `Slack`, `Tui`,
or `SignalR` — these are the only channel types with gateways that
support `DeliverTrustedSessionTurn`. If neither an explicit
`reportToChannel` nor an addressable `context.SessionId` is available,
the reminder SHALL be persisted with both fields null (headless execution).

#### Scenario: Hash-prefixed channel name resolved to canonical ID

- **GIVEN** a host with a registered `IReminderTargetResolver` that maps
  `#general` to channel ID `C0123ABC`
- **WHEN** the LLM calls `set_reminder` with `reportToChannel: "#general"`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `C0123ABC`
- **AND** `SessionId` is null (Mode A)
- **AND** the tool response reports success with the resolved schedule

#### Scenario: User handle resolved to canonical user ID

- **GIVEN** a host with a registered resolver that maps `@aaronontheweb` to
  user ID `U0456XYZ`
- **WHEN** the LLM calls `set_reminder` with `reportToChannel: "@aaronontheweb"`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `U0456XYZ`
- **AND** default notify instructions direct the agent to send a direct
  message to that resolved user ID

#### Scenario: Raw channel ID passes through without an API call

- **GIVEN** a host with a registered resolver
- **WHEN** the LLM calls `set_reminder` with `reportToChannel: "C0123ABC"`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `C0123ABC`
- **AND** no directory lookup against the channel transport is performed

#### Scenario: Unresolvable target returns actionable tool error

- **GIVEN** a host with a registered resolver that cannot resolve
  `#nonexistent-channel`
- **WHEN** the LLM calls `set_reminder` with
  `reportToChannel: "#nonexistent-channel"`
- **THEN** the tool returns an error string beginning with
  `Error: Could not resolve reportToChannel`
- **AND** no `ReminderDefinition` is persisted
- **AND** no `SaveReminderCommand` is sent to the reminder manager actor

#### Scenario: No channel transport configured rejects supplied target

- **GIVEN** a host with no `IReminderTargetResolver` registered in DI
- **WHEN** the LLM calls `set_reminder` with any non-empty `reportToChannel`
- **THEN** the tool returns an error string containing
  `No notification channel transport is configured`
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Mode B — session check-back without explicit channel

- **GIVEN** a tool execution context with `SessionId = "C0123ABC/1234567890.123456"`
  and `ChannelType = Slack`
- **WHEN** the LLM calls `set_reminder` without supplying `reportToChannel`
- **THEN** the persisted `ReminderDefinition.SessionId` equals
  `C0123ABC/1234567890.123456`
- **AND** `ReminderDefinition.OriginChannelType` equals `Slack`
- **AND** `ReminderDefinition.ReportToChannel` is null
- **AND** `ReminderDefinition.ReportToThreadTs` is null
- **AND** the resolver is NOT invoked
- **AND** default notify instructions direct the agent to reply in the
  originating session

#### Scenario: Mode B rejected for unsupported origin channel types

- **GIVEN** a tool execution context with `ChannelType = Headless` (or
  `Webhook`, `Reminder`) and a non-null `SessionId`
- **WHEN** the LLM calls `set_reminder` without supplying `reportToChannel`
- **THEN** the tool returns an error string explaining that Mode B is
  only supported for channels with a `DeliverTrustedSessionTurn` gateway
  (Slack, Tui, SignalR)
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Headless configuration with no supplied target continues to work

- **GIVEN** a host with no `IReminderTargetResolver` registered
- **WHEN** the LLM calls `set_reminder` without supplying `reportToChannel`
  and without an addressable `context.SessionId`
- **THEN** the reminder is persisted with `ReportToChannel = null` and
  `SessionId = null`
- **AND** the tool returns success

### Requirement: Failure handling and guardrails

Netclaw's reminder manager SHALL track consecutive execution failures per
reminder via `_failureCounts` and SHALL auto-pause a reminder when the
count reaches an internal `FailurePauseThreshold` constant. A successful
execution SHALL reset the failure count to zero. Paused reminders SHALL
remain persisted with `status: "paused"` and SHALL be visible via
`netclaw reminders list`.

`FailurePauseThreshold` is not operator-configurable — it lives as an
`internal const` on `ReminderManagerActor`. `Akka.Reminders` applies its
own separate retry budget (`MaxDeliveryAttempts`, library default) to
envelope delivery; Netclaw's auto-pause threshold is set strictly below
the library's default so the Netclaw-side pause fires first in practice
and operators see a `paused` reminder in `netclaw reminders list` before
the library would mark an occurrence terminally failed. If either
default changes in a way that breaks this ordering, add back a single
operator knob.

The reminder manager SHALL enforce a maximum concurrent execution limit
(`MaxConcurrentExecutions`, internal const) and SHALL enforce a
per-execution timeout (`ExecutionTimeoutSeconds`, internal const on
`ReminderExecutionActor`).

#### Scenario: Consecutive failures auto-pause task

- **GIVEN** a scheduled task has failed N times in a row where N equals
  `FailurePauseThreshold`
- **WHEN** the Nth failure is reported to `ReminderManagerActor`
- **THEN** the task status is set to `paused`
- **AND** the Akka timer for the task is cancelled
- **AND** a log event is emitted naming the reminder and the failure count
- **AND** the reminder remains in `tasks.json` with `status: "paused"`

#### Scenario: Successful execution resets failure counter

- **GIVEN** a scheduled task has failed twice
- **WHEN** the next execution succeeds
- **THEN** the internal failure count for that reminder is reset to zero
- **AND** subsequent failures start counting from zero again

#### Scenario: Max concurrent execution limit enforced

- **GIVEN** `MaxConcurrentExecutions` is reached and that many reminders are currently executing
- **WHEN** another reminder fires
- **THEN** the new reminder is deferred to an internal queue
- **AND** the Akka.Reminders envelope is still acked (both Mode A and
  Mode B deferred paths — a reminder that can't be dispatched yet is
  acked and the library's retry/auto-pause machinery covers starvation)

#### Scenario: Execution timeout enforced

- **GIVEN** a reminder execution exceeds the per-execution timeout
- **WHEN** the timeout fires
- **THEN** the execution is cancelled and reported as a failure
- **AND** the failure is counted toward `FailurePauseThreshold`

## ADDED Requirements

### Requirement: Envelope-ack-gated at-least-once delivery for Mode B

For Mode B reminders, the `ReminderManagerActor` SHALL NOT call
`_client.AckAsync(envelope)` eagerly. It SHALL spawn
`ReminderExecutionActor` and pass the `ReminderEnvelope` to the child
explicitly. The execution actor SHALL acquire `IReminderClient` via
`ReminderClientExtension.Get(Context.System)` at startup and SHALL call
`_client.AckAsync(envelope)` itself once the target session has
confirmed receipt.

The execution actor SHALL dispatch
`DeliverTrustedSessionTurn(SessionId, Content, MessageSource)` to the
target channel gateway using `Ask<CommandAck>` (Slack via
`SlackGatewayActor`, SignalR/TUI via `SignalRGatewayActor`, selected by
`OriginChannelType`) with a timeout of
`ReminderSettings.DefaultAckTimeout` (Akka.Reminders' shipped default,
currently 10 seconds — referencing the library constant directly so
Netclaw tracks any future library change automatically). The gateway's
handler SHALL propagate the message down its existing routing hierarchy
via `Forward` (preserving `Sender`), until it reaches the leaf binding/
session actor, which reads `Sender` and places it on the outgoing
`ChannelInput` as `MessageSource.AckTarget`.
`ChannelPipeline.MapToCommand`'s stream sink SHALL use
`cmd.Source?.AckTarget ?? ActorRefs.NoSender` as the `Tell` sender when
delivering to the session manager. `LlmSessionActor`'s existing
`TryReplyAck()` fires `CommandAck` to that sender, completing the
dispatcher's `Ask`.

On `CommandAck`, the execution actor SHALL call
`await _client.AckAsync(envelope)`, inspect the
`ReminderAckResponse.ResponseCode`, log on non-`Success`, and tell
`Context.Parent` a `ReminderExecutionCompleted(success=true)` for
bookkeeping. On Ask-timeout, `CommandNack`, or any gateway/transport
exception, the execution actor SHALL NOT call `AckAsync`; it SHALL
tell the parent a `ReminderExecutionCompleted(success=false)` with an
error message. The un-acked envelope SHALL be redelivered by
`Aaron.Akka.Reminders` per its built-in `AckTimeout` and
`MaxDeliveryAttempts` defaults.

For Mode A reminders, the manager SHALL continue to call
`_client.AckAsync(envelope)` eagerly after spawning the execution actor
as today.

Redelivery SHALL be best-effort deduped: the target session dedup
pre-checks the reminder's `(reminderId, fireTimestampMs)` pair against
its in-memory `ProcessedReminderIds` set (rebuilt from persisted
`TurnRecorded.SourceReminderId` events on recovery, not serialized to
snapshot) and SHALL reply `CommandAck` without processing a duplicate
when the dedup check hits. Dedup misses (across snapshot recovery
boundaries or after long passivation) result in the reminder being
processed a second time, which is an explicitly accepted tradeoff.

#### Scenario: Mode B envelope acked by execution child via IReminderClient

- **GIVEN** a Mode B reminder fires
- **WHEN** the `ReminderManagerActor` receives the envelope
- **THEN** the manager spawns a `ReminderExecutionActor` child and
  passes the envelope to it
- **AND** the manager does NOT call `_client.AckAsync(envelope)` itself
- **WHEN** the execution child `Ask<CommandAck>`s the target channel
  gateway with a `DeliverTrustedSessionTurn`
- **AND** the gateway forwards the message down its routing hierarchy,
  each level preserving `Sender` via `Forward`
- **AND** the leaf binding/session actor reads `Sender` and builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender`
- **AND** the pipeline stream stage maps the `ChannelInput` to
  `SendUserMessage` and tells the session manager using the
  `AckTarget` as the `Tell` sender
- **AND** the session's `HandleIncomingUserMessage` fires
  `TryReplyAck()`, which replies `CommandAck` to the Ask temp actor
- **THEN** the execution child's `Ask` completes with `CommandAck`
- **AND** the execution child calls `await _client.AckAsync(envelope)`
  exactly once
- **AND** the execution child tells `Context.Parent` a
  `ReminderExecutionCompleted(success=true)`

#### Scenario: Session Ask-timeout triggers Akka.Reminders redelivery

- **GIVEN** a Mode B reminder fires and the target channel gateway has
  been dispatched a `DeliverTrustedSessionTurn`
- **AND** the pipeline or session fails to reply `CommandAck` within
  `ReminderSettings.DefaultAckTimeout`
- **WHEN** the execution actor's `Ask<CommandAck>` times out
- **THEN** the execution actor does NOT call `_client.AckAsync(envelope)`
- **AND** the execution actor tells `Context.Parent` a
  `ReminderExecutionCompleted(success=false)` with a timeout error
- **AND** `Aaron.Akka.Reminders` marks the envelope as ack-timed-out
  and redelivers it per its built-in `MaxDeliveryAttempts` default

#### Scenario: Redelivered reminder is deduped on the target session

- **GIVEN** a Mode B reminder was previously processed by the session
  (evidenced by a `TurnRecorded` event whose `SourceReminderId` matches
  the reminder's `{reminderId}:{fireTimestampMs}` and is present in
  `ProcessedReminderIds`)
- **WHEN** Akka.Reminders redelivers the same envelope after a
  transient failure
- **THEN** the session dedup pre-check fires in
  `HandleIncomingUserMessage` and `TryReplyAck()` returns `CommandAck`
  without re-processing the turn
- **AND** the execution actor calls `_client.AckAsync(envelope)` once,
  closing out the redelivery loop

#### Scenario: Gateway rejects the ChannelInput on backpressure

- **GIVEN** a Mode B reminder fires and the execution actor has
  dispatched `DeliverTrustedSessionTurn` to the channel gateway
- **WHEN** the leaf binding actor's `inputQueue.OfferAsync(channelInput)`
  returns a non-`Enqueued` result
- **THEN** the binding Tells `Sender` (the Ask temp actor via `AckTarget`
  it would have set) a `CommandNack` directly
- **AND** the execution actor's `Ask<CommandAck>` completes with
  `CommandNack`
- **AND** the execution actor does NOT call `AckAsync`
- **AND** the envelope is redelivered by Akka.Reminders

### Requirement: Reminder delivery guarantees

The Mode B reminder delivery pipeline SHALL provide at-least-once
guarantees from the Akka.Reminders envelope down to the target session's
in-memory `CommandAck` boundary, with an explicitly accepted gap between
session-ack and turn-persist that is subsumed by future work.

**Guaranteed windows** (at-least-once, dedup-safe or redelivery-safe):

1. Crash before the channel gateway receives
   `DeliverTrustedSessionTurn`: envelope un-acked, Akka.Reminders
   redelivers on next fire.
2. Crash between the gateway's `OfferAsync` and the pipeline stream
   stage processing the `ChannelInput`: the Ask temp actor never
   receives a reply, execution actor's `Ask` times out without calling
   `AckAsync`, envelope un-acked, Akka.Reminders redelivers.
3. Crash after session received the message (in-memory state updated)
   but before execution actor calls `_client.AckAsync(envelope)`: the
   envelope is still un-acked, Akka.Reminders redelivers. On
   redelivery, if `TurnRecorded` already persisted, the session's
   `ProcessedReminderIds` dedup catches it (best-effort); if not, the
   redelivery is processed as a fresh turn (desired retry).
4. Ack message lost in flight between execution actor and the
   Akka.Reminders scheduler proxy: Akka.Reminders redelivers on
   `AckTimeout`, session dedup likely catches the duplicate.

**Explicitly NOT guaranteed (accepted tradeoffs)**:

- **Crash after `_client.AckAsync(envelope)` succeeds but before the
  session's LLM turn completes and `TurnRecorded` is persisted.** In
  this window the envelope has been acknowledged from Akka.Reminders'
  perspective (the scheduler will not redeliver it) but the session
  only reached in-memory state and did not write a durable record. On
  restart, the reminder is lost. This window spans the entire LLM turn
  execution, potentially minutes for tool-heavy reasoning. **This is
  the identical failure mode every regular `SendUserMessage` has today**
  — Mode B reminders do not introduce a new failure class. Closing
  this gap requires a durable ingress queue on `LlmSessionActor`, which
  is session-wide work deferred to the drain-on-shutdown follow-up
  (issues #403, #419).

- **Duplicate reminder processing across snapshot recovery boundaries.**
  If `LlmSessionActor` is recovered from a snapshot rather than
  replaying the full journal, the `ProcessedReminderIds` dedup set
  starts empty. A redelivery of a pre-snapshot reminder would then be
  processed as a fresh turn. In practice this requires the reminder to
  still be within Akka.Reminders' `MaxDeliveryWindow` after a snapshot
  has been taken — a narrow timing window. **Accepted tradeoff**: the
  LLM itself typically recognizes a duplicate prompt in its recent
  context and responds appropriately. Persisting the dedup set to
  snapshot was not worth the complexity.

Operators who need stronger guarantees should track the
drain-on-shutdown follow-up.

#### Scenario: Crash before gateway offer is safe

- **GIVEN** a Mode B reminder fires
- **WHEN** the daemon crashes before the channel gateway's
  `DeliverTrustedSessionTurn` handler completes its `OfferAsync`
- **THEN** the envelope is un-acked
- **AND** on daemon restart, Akka.Reminders redelivers the envelope
- **AND** the reminder is processed normally

#### Scenario: Crash between gateway offer and stream stage is safe

- **GIVEN** a Mode B reminder fires and the channel gateway has
  successfully offered a `ChannelInput` to the pipeline queue
- **WHEN** the daemon crashes before the pipeline stream stage processes
  the `ChannelInput` and reaches the session actor
- **THEN** the execution actor's `Ask<CommandAck>` times out
- **AND** `_client.AckAsync(envelope)` is not called
- **AND** the envelope is un-acked
- **AND** on daemon restart, Akka.Reminders redelivers and the reminder
  is processed normally

#### Scenario: Crash between session in-memory receipt and AckAsync is safe

- **GIVEN** the session's `HandleIncomingUserMessage` has updated
  in-memory state and fired `TryReplyAck()`, but the `CommandAck` has
  not yet been processed by the execution actor's Ask
- **WHEN** the daemon crashes before `_client.AckAsync(envelope)` is
  called
- **THEN** the envelope is un-acked
- **AND** on daemon restart, Akka.Reminders redelivers
- **AND** if `TurnRecorded` was already persisted by the session before
  the crash, the dedup pre-check catches the redelivery (best-effort)
- **AND** if `TurnRecorded` was NOT yet persisted, the redelivered
  reminder is processed as a fresh turn (desired retry)

#### Scenario: Crash after AckAsync but before TurnRecorded loses the reminder (accepted gap)

- **GIVEN** the execution actor has called
  `_client.AckAsync(envelope)` successfully and received a
  `ReminderAckResponse(Success)`
- **AND** the session has begun processing the turn but has not yet
  persisted `TurnRecorded`
- **WHEN** the daemon crashes
- **THEN** the envelope is acked from Akka.Reminders' perspective and
  is NOT redelivered on restart
- **AND** the session recovery replays its journal but finds no
  `TurnRecorded` for this reminder
- **AND** the reminder turn is lost
- **AND** this outcome is documented as an explicit accepted tradeoff,
  identical to the failure mode every regular `SendUserMessage` has
  today, subsumed by the drain-on-shutdown follow-up (issues #403, #419)

#### Scenario: Duplicate across snapshot recovery is accepted

- **GIVEN** a Mode B reminder was processed and `TurnRecorded`
  persisted
- **AND** a subsequent `SessionSnapshot` was taken
- **AND** the session later recovers from that snapshot (journal
  replay skips events before the snapshot)
- **AND** a redelivery of the original reminder arrives via
  Akka.Reminders (the envelope was within `MaxDeliveryWindow`)
- **WHEN** the dedup pre-check runs
- **THEN** the set is empty (not populated from the snapshot) and the
  redelivery is processed as a fresh turn
- **AND** the LLM may observe the duplicate in its transcript context
  and respond appropriately
- **AND** this outcome is documented as an explicit accepted tradeoff

#### Scenario: Delivery guarantees documented in reminder-set confirmation

- **GIVEN** a Mode B reminder is successfully set
- **WHEN** the tool returns its success message
- **THEN** the message conveys that the reminder will fire and deliver
  a new turn to the originating session

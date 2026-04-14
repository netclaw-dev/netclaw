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
  two channels with server-side gateways, both of which SHALL implement a
  `Receive<DeliverTrustedSessionTurn>` handler that runs the same
  lookup-or-create chain used for real inbound events. The reminder
  dispatcher SHALL tell the appropriate gateway based on
  `OriginChannelType`: `ChannelType.Slack` → `SlackGatewayActor`;
  `ChannelType.Tui` or `ChannelType.SignalR` → `SignalRGatewayActor`
  (both TUI and SignalR map to the same gateway because TUI is a SignalR
  client, not a separate transport). The channel-level inbound ACL SHALL
  be bypassed because the reminder's audience is already validated at
  minting time. Any other `OriginChannelType` SHALL be rejected at
  `set_reminder` time — there is no gateway to route through.

Both modes SHALL support a silent-unless-notable mode where routine results
are suppressed and only notable findings are posted (Mode A) or delivered as
a new turn (Mode B).

#### Scenario: Mode A results posted to configured channel

- **GIVEN** a scheduled task has `report_to.channel` configured with a
  canonical channel ID
- **WHEN** the task execution completes with results
- **THEN** the results are posted to the configured Slack channel via the
  reminder's isolated execution session

#### Scenario: Mode B Slack delivery reuses the Slack inbound path

- **GIVEN** a Mode B reminder created from a Slack thread session with
  `OriginChannelType = Slack` whose thread binding may or may not be
  currently materialized
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher tells `SlackGatewayActor` a
  `DeliverTrustedSessionTurn` message carrying the originating `SessionId`,
  the reminder prompt, and a trusted `MessageSource`
- **AND** the gateway parses the `SessionId` into `(channelId, threadTs)`
  and runs the same conversation/thread lookup-or-create chain used for
  inbound Slack events
- **AND** the resulting `SlackThreadBindingActor` is live and subscribed
  to the session's output stream
- **AND** the reminder turn is delivered through the normal `ChannelInput`
  → `ChannelPipeline` → `SendUserMessage` → session pipeline
- **AND** the session's streaming response is posted back to the original
  Slack thread via the binding's existing output sink

#### Scenario: Mode B SignalR delivery reuses the SignalR inbound path

- **GIVEN** a Mode B reminder created from a SignalR session (including
  TUI) with `OriginChannelType` = `Tui` or `SignalR` and `SessionId` =
  `signalr/{guid}`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher tells `SignalRGatewayActor` a
  `DeliverTrustedSessionTurn` message carrying the originating `SessionId`,
  the reminder prompt, and a trusted `MessageSource`
- **AND** the gateway runs the same lookup-or-create chain used when
  `SessionRegistry.StartSignalRSession` is invoked for an inbound event
- **AND** the reminder turn is delivered through the normal `ChannelInput`
  → `ChannelPipeline` → `SendUserMessage` → session pipeline
- **AND** if a SignalR client is currently connected for this session,
  the streaming response reaches the client in real time via the existing
  `SignalRSessionActor` bridge
- **AND** if no client is currently connected, the session still processes
  the turn and persists `TurnRecorded`; the streaming output is dropped
  by the existing `Source.ActorRef` `OverflowStrategy.DropHead` behavior,
  and the completed turn is visible on next `ResumeSessionAsync`

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
context.ChannelType`, and `ReportToChannel = null`. If neither an explicit
`reportToChannel` nor an addressable `context.SessionId` is available, the
reminder SHALL be persisted with both fields null (headless execution).

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

#### Scenario: Headless configuration with no supplied target continues to work

- **GIVEN** a host with no `IReminderTargetResolver` registered
- **WHEN** the LLM calls `set_reminder` without supplying `reportToChannel`
  and without an addressable `context.SessionId`
- **THEN** the reminder is persisted with `ReportToChannel = null` and
  `SessionId = null`
- **AND** the tool returns success

## ADDED Requirements

### Requirement: Envelope-ack-gated at-least-once delivery for Mode B

For Mode B reminders, the `ReminderManagerActor` SHALL NOT call
`_client.AckAsync(envelope)` eagerly. It SHALL spawn
`ReminderExecutionActor` and pass the `ReminderEnvelope` to the child
explicitly (e.g., as a constructor arg or initial message). The
execution actor SHALL acquire `IReminderClient` via
`ReminderClientExtension.Get(Context.System)` at startup and SHALL call
`_client.AckAsync(envelope)` itself once the target session has
confirmed receipt.

The execution actor SHALL dispatch
`DeliverTrustedSessionTurn(SessionId, Content, MessageSource)` to the
target channel gateway using `Ask<CommandAck>` (Slack via
`SlackGatewayActor`, SignalR via `SignalRGatewayActor`, selected by
`OriginChannelType`). The gateway handler SHALL read `Sender` (the Ask
temp actor), create a `ChannelInput` with `AckTarget = Sender`, and
offer it to the existing pipeline queue via `inputQueue.OfferAsync(...)`.
**The gateway handler SHALL NOT reply to the Ask directly in the happy
path** — the reply flows from the target session's `TryReplyAck()`
through the pipeline's sender-propagation chain back to the temp actor.
The gateway SHALL reply `CommandNack` directly only when `OfferAsync`
returns a non-`Enqueued` result, signaling that the channel refused the
message.

On `CommandAck` (meaning the target session has accepted the message
into its in-memory state), the execution actor SHALL call
`await _client.AckAsync(envelope)`, inspect the
`ReminderAckResponse.ResponseCode`, log on non-success, and tell
`Context.Parent` a `ReminderExecutionCompleted(success=true)` for
bookkeeping. On Ask-timeout, `CommandNack`, or any gateway/transport
exception, the execution actor SHALL NOT call `AckAsync`; it SHALL
tell the parent a `ReminderExecutionCompleted(success=false)` with an
error message. The un-acked envelope SHALL be redelivered by
`Aaron.Akka.Reminders` per its configured `AckTimeout`,
`MaxDeliveryAttempts`, and `MaxDeliveryWindow`.

For Mode A reminders, the manager SHALL continue to call
`_client.AckAsync(envelope)` eagerly after spawning the execution actor
as today.

Redelivery SHALL be idempotent: the target session dedup checks the
reminder's `(reminderId, fireTimestampMs)` pair against its
`ProcessedReminderIds` set (rebuilt from persisted
`TurnRecorded.SourceReminderId` events on recovery) and SHALL reply
`CommandAck` without processing a duplicate.

#### Scenario: Mode B envelope acked by execution child via IReminderClient

- **GIVEN** a Mode B reminder fires
- **WHEN** the `ReminderManagerActor` receives the envelope
- **THEN** the manager spawns a `ReminderExecutionActor` child and
  passes the envelope to it (as a constructor arg or initial message)
- **AND** the manager does NOT call `_client.AckAsync(envelope)` itself
- **WHEN** the execution child `Ask<CommandAck>`s the target channel
  gateway with a `DeliverTrustedSessionTurn`
- **AND** the gateway handler reads `Sender`, creates a `ChannelInput`
  with `AckTarget = Sender`, and offers it to the pipeline queue
  without replying
- **AND** the pipeline stream stage maps the `ChannelInput` to
  `SendUserMessage` and tells the session manager using the
  `AckTarget` as the `Tell` sender
- **AND** the session's `HandleIncomingUserMessage` fires
  `TryReplyAck()`, which replies `CommandAck` to the temp actor
- **THEN** the execution child's `Ask` completes with `CommandAck`
- **AND** the execution child calls
  `await _client.AckAsync(envelope)` exactly once
- **AND** the execution child tells `Context.Parent` a
  `ReminderExecutionCompleted(success=true)`

#### Scenario: Session Ask-timeout triggers Akka.Reminders redelivery

- **GIVEN** a Mode B reminder fires and the target channel gateway has
  been dispatched a `DeliverTrustedSessionTurn`
- **AND** the pipeline or session fails to reply `CommandAck` within
  the configured Ask timeout (queue backpressure, session mid-rehydrate,
  stream stage stall, etc.)
- **WHEN** the execution actor's `Ask<CommandAck>` times out
- **THEN** the execution actor does NOT call
  `_client.AckAsync(envelope)`
- **AND** the execution actor tells `Context.Parent` a
  `ReminderExecutionCompleted(success=false)` with a timeout error
- **AND** `Aaron.Akka.Reminders` marks the envelope as ack-timed-out
  and redelivers it per the configured `MaxDeliveryAttempts`

#### Scenario: Redelivered reminder is deduped on the target session

- **GIVEN** a Mode B reminder was previously processed by the session
  (evidenced by a `TurnRecorded` event whose `SourceReminderId` matches
  the reminder's `{reminderId}:{fireTimestampMs}`)
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
- **WHEN** the gateway's `inputQueue.OfferAsync(channelInput)` returns
  a non-`Enqueued` result (e.g., `Dropped`, `Failure`, `QueueClosed`)
- **THEN** the gateway handler replies `CommandNack` directly to the
  Ask temp actor (its `Sender`)
- **AND** the execution actor's `Ask<CommandAck>` completes with
  `CommandNack` (not `CommandAck`)
- **AND** the execution actor does NOT call `AckAsync`
- **AND** the envelope is redelivered by Akka.Reminders

### Requirement: Reminder delivery guarantees

The Mode B reminder delivery pipeline SHALL provide at-least-once
guarantees from the Akka.Reminders envelope down to the target session's
in-memory `CommandAck` boundary, with an explicitly accepted gap between
session-ack and turn-persist that is subsumed by future work.

**Guaranteed windows** (at-least-once, dedup-safe):

1. Crash before the channel gateway receives `DeliverTrustedSessionTurn`:
   envelope un-acked, Akka.Reminders redelivers on next fire.
2. Crash between the gateway's `OfferAsync` and the pipeline stream stage
   processing the `ChannelInput`: the Ask temp actor never receives a
   reply, execution actor's `Ask` times out without calling `AckAsync`,
   envelope un-acked, Akka.Reminders redelivers. (This is the window
   the gateway-never-replies pattern explicitly closes.)
3. Crash after session received the message (in-memory state updated)
   but before execution actor calls `_client.AckAsync(envelope)`: the
   envelope is still un-acked, Akka.Reminders redelivers. On redelivery,
   if `TurnRecorded` already persisted, the session's
   `ProcessedReminderIds` dedup catches it; if not, the redelivery is
   processed as a fresh turn (desired retry).
4. Ack message lost in flight between execution actor and the
   Akka.Reminders scheduler proxy: Akka.Reminders redelivers on
   `AckTimeout`, session dedup catches the duplicate.

**Explicitly NOT guaranteed (accepted tradeoff)**:

Crash after the execution actor has successfully called
`_client.AckAsync(envelope)` but before the session's LLM turn
completes and `TurnRecorded` is persisted. In this window the envelope
has been acknowledged from Akka.Reminders' perspective (the scheduler
will not redeliver it) but the session only reached in-memory state and
did not write a durable record of the reminder turn. On restart, the
reminder is lost.

This window spans the entire LLM turn execution, which can be minutes
for tool-heavy reasoning turns. **This is the identical failure mode
every regular `SendUserMessage` has today**: the session's
`TryReplyAck()` fires after in-memory state update but before the LLM
call starts, and any crash during the turn loses the message. Mode B
reminders do not introduce a new failure class — they inherit the
existing user-message semantic.

Closing this gap requires a durable ingress queue on `LlmSessionActor`
(persist user messages on receipt, mark them processed when
`TurnRecorded` is written) — a session-wide change that affects every
`SendUserMessage` code path, not reminders specifically. That work is
deferred to the drain-on-shutdown follow-up (issues #403, #419) where it
can be designed holistically across all ingress.

Operators and downstream specs MAY treat this window as an accepted
tradeoff for this change. Future changes that implement durable ingress
SHALL supersede this guarantee text.

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
  in-memory state and fired `TryReplyAck()`, but the
  `CommandAck` has not yet been processed by the execution actor's Ask
- **WHEN** the daemon crashes before `_client.AckAsync(envelope)` is
  called
- **THEN** the envelope is un-acked
- **AND** on daemon restart, Akka.Reminders redelivers
- **AND** if `TurnRecorded` was already persisted by the session before
  the crash, the dedup pre-check catches the redelivery
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
  today, and is subsumed by the drain-on-shutdown follow-up (issues
  #403, #419)

#### Scenario: Delivery guarantees documented in reminder-set confirmation

- **GIVEN** a Mode B reminder is successfully set
- **WHEN** the tool returns its success message
- **THEN** the message conveys that the reminder will fire and deliver
  a new turn to the originating session

### Requirement: Configurable Akka.Reminders delivery tunables

`ReminderConfig` SHALL expose `AckTimeout`, `MaxDeliveryAttempts`, and
`MaxDeliveryWindow` properties and wire them into the underlying
`Aaron.Akka.Reminders` client construction. Defaults SHALL match the
package defaults. Values SHALL be schema-validated via
`netclaw-config.v1.schema.json` per the Configuration Schema Sync Rule.

#### Scenario: AckTimeout configured via netclaw.json

- **GIVEN** `netclaw.json` sets `reminders.ackTimeout` to `00:01:00`
- **WHEN** the daemon starts
- **THEN** the constructed `ReminderClient` uses a 60-second ack timeout
- **AND** schema validation passes

#### Scenario: Invalid MaxDeliveryAttempts rejected at startup

- **GIVEN** `netclaw.json` sets `reminders.maxDeliveryAttempts` to a
  non-positive integer
- **WHEN** the daemon starts
- **THEN** `ConfigSchemaDoctorCheck` rejects the configuration with a clear
  error message
- **AND** the daemon fails to start (fail-loud)

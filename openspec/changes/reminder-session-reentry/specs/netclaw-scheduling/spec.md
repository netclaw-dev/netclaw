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
- **Mode B** (session check-back): the reminder turn is processed inside the
  originating session; output SHALL flow back through the session's original
  transport binding, reactivated on demand via an
  `ISessionTransportReanimator` keyed by `OriginChannelType`. No separate
  "post to a channel" step is performed.

Both modes SHALL support a silent-unless-notable mode where routine results
are suppressed and only notable findings are posted (Mode A) or delivered as
a new turn (Mode B).

#### Scenario: Mode A results posted to configured channel

- **GIVEN** a scheduled task has `report_to.channel` configured with a
  canonical channel ID
- **WHEN** the task execution completes with results
- **THEN** the results are posted to the configured Slack channel via the
  reminder's isolated execution session

#### Scenario: Mode B results flow back through originating transport

- **GIVEN** a Mode B reminder created from a Slack thread session whose
  thread binding has since passivated
- **WHEN** the reminder fires
- **THEN** the `SlackSessionTransportReanimator` is invoked to re-materialize
  the thread binding for the originating `{channelId}/{threadTs}` pair
- **AND** the reminder turn's streaming output is delivered to the
  reactivated Slack thread binding via the existing `JoinSession` subscriber
  mechanism
- **AND** the user sees the reminder response appear in the original Slack
  thread

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

For Mode B reminders, the `ReminderManagerActor` SHALL NOT acknowledge the
Akka.Reminders envelope until the target session has replied `CommandAck` to
the dispatched `SendUserMessage`. On session `CommandNack`, reanimator
failure, or Ask-timeout, the execution actor SHALL report failure to the
manager without requesting an envelope ack; the un-acked envelope SHALL be
redelivered by `Aaron.Akka.Reminders` per its configured `AckTimeout`,
`MaxDeliveryAttempts`, and `MaxDeliveryWindow`. For Mode A reminders, the
manager SHALL continue to ack the envelope eagerly after spawning the
execution actor.

Redelivery SHALL be idempotent: the target session dedup checks the
reminder's `(reminderId, fireTimestampMs)` pair against its persisted
`ProcessedReminderIds` ledger (fed by `TurnRecorded.SourceReminderId`) and
SHALL reply `CommandAck` without processing a duplicate.

#### Scenario: Mode B envelope held open until session acks

- **GIVEN** a Mode B reminder fires
- **WHEN** the `ReminderManagerActor` dispatches execution
- **THEN** the envelope is NOT acked immediately
- **AND** the execution actor `Ask`s the session manager with a
  `SendUserMessage` carrying `MessageSource.ReminderId`
- **WHEN** the session replies `CommandAck`
- **THEN** the execution actor tells the manager to ack the envelope
- **AND** `_client.AckAsync(envelope)` is called exactly once

#### Scenario: Session Ask-timeout triggers Akka.Reminders redelivery

- **GIVEN** a Mode B reminder fires
- **AND** the target session is mid-rehydrate and does not respond within
  the configured Ask timeout
- **WHEN** the execution actor's Ask times out
- **THEN** the execution actor reports failure to the manager WITHOUT
  requesting an envelope ack
- **AND** `Aaron.Akka.Reminders` marks the envelope as ack-timed-out and
  redelivers it per the configured `MaxDeliveryAttempts`

#### Scenario: Redelivered reminder is deduped on the target session

- **GIVEN** a Mode B reminder was previously processed by the session
  (evidenced by a `TurnRecorded` event whose `SourceReminderId` matches the
  reminder's `{reminderId}:{fireTimestampMs}`)
- **WHEN** Akka.Reminders redelivers the same envelope after a transient
  failure
- **THEN** the session replies `CommandAck` without processing the reminder
  again
- **AND** the execution actor tells the manager to ack the envelope

### Requirement: Transport reanimation contract for Mode B

The host SHALL register an `ISessionTransportReanimator` implementation for
every channel type that supports Mode B. The reanimator contract SHALL be
idempotent: multiple concurrent `EnsureBindingAsync` calls for the same
`SessionId` SHALL produce exactly one live binding actor. Mode B execution
SHALL invoke the reanimator (looked up via `SessionTransportRegistry` keyed
by `OriginChannelType`) before dispatching the `SendUserMessage`.

Reanimators for channels without durable outbound state (e.g., TUI) MAY
succeed as no-ops; reminder turns still persist into session state and
appear when the user reconnects.

#### Scenario: Slack reanimator re-materializes thread binding for passivated session

- **GIVEN** a Mode B reminder with `OriginChannelType = Slack` and
  `SessionId = "C0123ABC/1234567890.123456"`
- **AND** the `SlackThreadBindingActor` for that thread is not currently
  materialized
- **WHEN** the execution actor calls
  `reanimator.EnsureBindingAsync(sessionId)`
- **THEN** the `SlackGatewayActor` receives an `EnsureThreadBinding` message
  and creates the conversation actor (if needed) and the thread binding
  actor (if needed)
- **AND** the call returns successfully after the binding is live and
  subscribed to the session's output stream

#### Scenario: Idempotent reanimation under concurrent calls

- **GIVEN** two parallel `EnsureBindingAsync` calls for the same
  `SessionId`
- **WHEN** both calls reach the `SlackGatewayActor`
- **THEN** exactly one `SlackThreadBindingActor` exists for the thread
- **AND** both calls complete successfully

#### Scenario: TUI reanimator is a no-op

- **GIVEN** a Mode B reminder with `OriginChannelType = Tui`
- **WHEN** the execution actor calls `reanimator.EnsureBindingAsync(sessionId)`
- **THEN** the call completes successfully without materializing any
  durable outbound binding
- **AND** the reminder turn is still delivered to the session actor and
  persisted normally

### Requirement: Reminder delivery guarantees

The reminder delivery pipeline SHALL provide at-least-once guarantees from
the Akka.Reminders envelope down to the target session's in-memory `CommandAck`
boundary. Specifically:

- Envelope held open until session ack: guaranteed.
- Session dedup on redelivery via `TurnRecorded.SourceReminderId`:
  guaranteed for turns that completed normally.
- Durability against mid-turn daemon crash (crash between `CommandAck` and
  `TurnRecorded` persistence): **NOT guaranteed** in this change. This
  matches the existing semantic for every `SendUserMessage` today and is
  subsumed by the drain-on-shutdown follow-up.

#### Scenario: Delivery guarantees documented in reminder-set confirmation

- **GIVEN** a Mode B reminder is successfully set
- **WHEN** the tool returns its success message
- **THEN** the message conveys that the reminder will fire and deliver a
  new turn to the originating session

#### Scenario: Mid-turn crash loses the reminder turn the same way a user message would

- **GIVEN** a Mode B reminder is accepted by the session (`CommandAck` sent,
  envelope acked) and the LLM turn begins
- **WHEN** the daemon crashes before `TurnRecorded` is persisted
- **THEN** the reminder turn is lost (same semantic as a regular user
  message in the same state)
- **AND** recovery does NOT attempt to re-issue the reminder

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

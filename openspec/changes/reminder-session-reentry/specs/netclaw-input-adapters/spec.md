# netclaw-input-adapters Delta Spec

## MODIFIED Requirements

### Requirement: Internal timer adapter

The timer adapter SHALL fire on Akka timer ticks for scheduled tasks and
create `SendUserMessage` commands with the task instruction as content. The
entity key and session lifecycle SHALL depend on the reminder's mode:

- **Mode A** (external notification — `ReportToChannel` set,
  `SessionId = null`): the entity key SHALL be `schedule/{taskId}/{runTs}`
  and each timer fire SHALL create a fresh isolated session.
- **Mode B** (session check-back — `SessionId` set, `ReportToChannel = null`):
  the entity key SHALL be the persisted `SessionId` and the timer fire SHALL
  re-enter the existing session actor (rehydrating from Akka.Persistence if
  currently passivated), NOT create a new session.

In both modes, the `SendUserMessage` SHALL carry a `MessageSource` whose
`ChannelType` reflects the reminder's `OriginChannelType` (Mode B) or
`ChannelType.Reminder` (Mode A), and whose `ReminderId` field is populated
with `{reminderId}:{fireTimestampMs}` for idempotent redelivery.

#### Scenario: Timer fires for active Mode A scheduled task

- **GIVEN** an active Mode A scheduled task has a timer registered
- **WHEN** the Akka timer fires
- **THEN** the timer adapter creates a `SendUserMessage` command
- **AND** the message content is the task's instruction prompt
- **AND** the entity key is `schedule/{taskId}/{runTs}`
- **AND** `MessageSource.ReminderId` equals `{taskId}:{runTsMs}`

#### Scenario: Fresh session created per Mode A timer execution

- **GIVEN** a Mode A timer fires for task `daily-report`
- **WHEN** the timer adapter dispatches the command
- **THEN** a new session actor is created for the unique
  `schedule/daily-report/{runTs}` entity key
- **AND** the session loads the agent personality from soul files
- **AND** the session does not reuse any previous execution's state

#### Scenario: Session re-entry per Mode B timer execution

- **GIVEN** a Mode B reminder persists `SessionId = "C0123ABC/1712000000.000000"`
  and `OriginChannelType = Slack`
- **WHEN** the Akka timer fires
- **THEN** the timer adapter dispatches a `SendUserMessage` with
  `SessionId = "C0123ABC/1712000000.000000"` and
  `MessageSource.ChannelType = Slack`
- **AND** the session manager routes the message to the existing
  `LlmSessionActor` for that `SessionId`
- **AND** NO new session actor with a `schedule/...` entity key is created

#### Scenario: Timer adapter does not fire for paused tasks

- **GIVEN** a scheduled task is in `paused` status
- **WHEN** the system checks for timer scheduling
- **THEN** no timer is registered for the paused task
- **AND** no `SendUserMessage` command is produced

## ADDED Requirements

### Requirement: Session transport reanimation contract

Every channel input adapter that supports Mode B reminder re-entry SHALL
register an `ISessionTransportReanimator` implementation in DI. The
reanimator contract requires:

- `ChannelType ChannelType { get; }` — the channel type this reanimator
  services.
- `Task EnsureBindingAsync(SessionId sessionId, CancellationToken ct)` —
  idempotent reactivation of the session's output transport binding so
  reminder-triggered turns are delivered back to the originating UI.

`EnsureBindingAsync` SHALL complete successfully when the binding is live
and subscribed to the session's output stream, OR when the channel does not
support durable outbound and the turn will still be persisted to session
state. It SHALL fail loudly on transport errors (e.g., Slack API rejection)
rather than silently degrading.

A `SessionTransportRegistry` singleton SHALL map `ChannelType → reanimator`
and SHALL be queried by `ReminderExecutionActor` before dispatching a Mode B
`SendUserMessage`. Lookup for a channel type with no registered reanimator
SHALL fail the reminder execution loudly — no silent fallback.

#### Scenario: Slack adapter registers transport reanimator

- **GIVEN** `Netclaw.Channels.Slack` is enabled in the host
- **WHEN** the daemon builds the Akka actor system
- **THEN** `SessionTransportRegistry` contains a reanimator keyed by
  `ChannelType.Slack`
- **AND** that reanimator's implementation delegates to
  `SlackGatewayActor` via an `EnsureThreadBinding` message

#### Scenario: TUI adapter registers a no-op reanimator

- **GIVEN** `Netclaw.Channels.Tui` is enabled in the host
- **WHEN** the daemon builds the Akka actor system
- **THEN** `SessionTransportRegistry` contains a reanimator keyed by
  `ChannelType.Tui`
- **AND** that reanimator's `EnsureBindingAsync` completes immediately as a
  no-op

#### Scenario: SignalR adapter registers a best-effort reanimator

- **GIVEN** `Netclaw.Channels.SignalR` is enabled in the host
- **WHEN** a Mode B reminder fires with `OriginChannelType = SignalR` and
  a client is currently connected for the session
- **THEN** the reanimator wires the connected client's binding as a
  subscriber to the session's output stream
- **WHEN** no client is currently connected
- **THEN** the reanimator completes as a no-op
- **AND** the reminder turn still persists into session state and becomes
  visible when a client reconnects

#### Scenario: Missing reanimator for declared OriginChannelType fails loudly

- **GIVEN** a Mode B reminder persists `OriginChannelType = Slack`
- **AND** the host has no reanimator registered for `ChannelType.Slack`
  (e.g., `Netclaw.Channels.Slack` not loaded)
- **WHEN** the reminder fires
- **THEN** `ReminderExecutionActor` reports failure with a clear message
  naming the missing reanimator
- **AND** the reminder envelope is NOT acked
- **AND** no `SendUserMessage` is dispatched to the session manager

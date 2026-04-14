# netclaw-input-adapters Delta Spec

## MODIFIED Requirements

### Requirement: Internal timer adapter

The timer adapter SHALL fire on Akka timer ticks for scheduled tasks and
deliver the task instruction to a session. The entity key and delivery path
SHALL depend on the reminder's mode:

- **Mode A** (external notification — `ReportToChannel` set,
  `SessionId = null`): the entity key SHALL be `schedule/{taskId}/{runTs}`
  and each timer fire SHALL create a fresh isolated session via the existing
  `ISessionPipeline.CreateAsync` path.
- **Mode B** (session check-back — `SessionId` set, `ReportToChannel = null`):
  the entity key SHALL be the persisted `SessionId` and the timer fire SHALL
  re-enter the existing session actor (rehydrating from Akka.Persistence if
  currently passivated), NOT create a new session. Mode B delivery SHALL
  route through the originating channel's existing inbound path. The
  reminder dispatcher tells the appropriate gateway a
  `DeliverTrustedSessionTurn` message based on `OriginChannelType`:
  `ChannelType.Slack` → `SlackGatewayActor`; `ChannelType.Tui` or
  `ChannelType.SignalR` → `SignalRGatewayActor` (both TUI and SignalR
  route through the SignalR gateway because TUI is a SignalR client, not
  a separate transport). The gateway runs the same lookup-or-create chain
  used for inbound events. Any other `OriginChannelType` is rejected at
  `set_reminder` time — Mode B requires an addressable server-side
  gateway.
- In both Mode A and Mode B, the dispatched `SendUserMessage` SHALL carry a
  `MessageSource` whose `ReminderId` field is populated with
  `{reminderId}:{fireTimestampMs}` for idempotent redelivery dedup at the
  session.

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

#### Scenario: Mode B Slack reminder routes through gateway inbound path

- **GIVEN** a Mode B reminder persists `SessionId = "C0123ABC/1712000000.000000"`
  and `OriginChannelType = Slack`
- **WHEN** the Akka timer fires
- **THEN** the reminder dispatcher tells `SlackGatewayActor` a
  `DeliverTrustedSessionTurn` message
- **AND** the gateway reuses the same lookup-or-create chain used for
  inbound Slack events to reach the thread binding actor
- **AND** the `ChannelInput` is queued into the existing pipeline for the
  thread, which delivers a `SendUserMessage` to the existing
  `LlmSessionActor`
- **AND** NO new session actor with a `schedule/...` entity key is created

#### Scenario: Mode B SignalR reminder routes through SignalR gateway

- **GIVEN** a Mode B reminder persists `SessionId = "signalr/abc123"` and
  `OriginChannelType = Tui` (or `SignalR`)
- **WHEN** the Akka timer fires
- **THEN** the reminder dispatcher tells `SignalRGatewayActor` a
  `DeliverTrustedSessionTurn` message (the same gateway handles both
  `Tui` and `SignalR` because TUI is a SignalR client subtype)
- **AND** the gateway runs the same lookup-or-create chain used by
  `SessionRegistry.StartSignalRSession`
- **AND** the `ChannelInput` is queued into the existing pipeline for the
  session, which delivers a `SendUserMessage` to the existing
  `LlmSessionActor`
- **AND** NO new session actor with a `schedule/...` entity key is created
- **AND** if a TUI client is connected, it receives the streaming response
  in real time; otherwise the turn persists and is visible on next
  `netclaw chat --resume`

#### Scenario: Timer adapter does not fire for paused tasks

- **GIVEN** a scheduled task is in `paused` status
- **WHEN** the system checks for timer scheduling
- **THEN** no timer is registered for the paused task
- **AND** no `SendUserMessage` command is produced

## ADDED Requirements

### Requirement: ChannelInput ack target propagation

`ChannelInput` SHALL expose an optional `AckTarget` field of type
`IActorRef?`. `ChannelPipeline.MapToCommand` SHALL propagate this value as
the `Tell` sender when dispatching the resulting `SendUserMessage` to the
session manager. When `AckTarget` is null, the pipeline SHALL use
`ActorRefs.NoSender` exactly as today, preserving fire-and-forget semantics
for regular user-message ingress.

This extension exists so that trusted deliveries (e.g., Mode B reminders)
can receive the session's existing `CommandAck` reply without the session
actor or the pipeline needing to special-case reminder messages. The
session's existing `TryReplyAck()` helper replies to `Sender`, which is the
`AckTarget` actor for trusted deliveries and `NoSender` for regular user
messages.

#### Scenario: Regular inbound message preserves fire-and-forget semantics

- **GIVEN** a Slack user sends a message in an active thread
- **WHEN** the pipeline maps the `ChannelInput` (with `AckTarget = null`)
  to `SendUserMessage`
- **THEN** the session manager is told with `ActorRefs.NoSender`
- **AND** the session's `TryReplyAck()` call goes to `DeadLetters` (the
  existing no-op behavior — the helper checks for `IsNobody()`)

#### Scenario: Trusted delivery receives CommandAck via AckTarget

- **GIVEN** a reminder dispatcher constructs a `ChannelInput` with
  `AckTarget = Self` and injects it into the pipeline for a session
- **WHEN** the pipeline maps the input to `SendUserMessage` and tells the
  session manager
- **THEN** the sender on the `Tell` is the dispatcher actor
- **AND** the session's `TryReplyAck()` replies `CommandAck` to the
  dispatcher actor
- **AND** the dispatcher's `Ask<CommandAck>` completes successfully

### Requirement: Trusted session turn delivery protocol

The shared protocol message `DeliverTrustedSessionTurn` SHALL be defined in
`Netclaw.Actors.Protocol` with the following shape:

```
DeliverTrustedSessionTurn(
    SessionId SessionId,
    string Content,
    MessageSource Source) : IWithSessionId
```

Every server-side channel in the daemon SHALL register a handler for
this message on its gateway actor. The handler SHALL:

1. Parse `SessionId` into channel-specific addressing.
2. Run the same lookup-or-create chain used for real inbound events to
   reach the session's pipeline queue (factored into a shared helper
   with the inbound-event handler).
3. Read `Sender` (the caller's `Ask` temp actor) and create a
   `ChannelInput` with `AckTarget = Sender`, carrying the supplied
   `MessageSource` as provenance.
4. Offer the `ChannelInput` into the pipeline queue via
   `inputQueue.OfferAsync(...)`.
5. **NOT reply to the `Ask` directly in the happy path.** The
   `CommandAck` reply to the caller flows from the downstream
   `LlmSessionActor`'s `TryReplyAck()` through `ChannelPipeline`'s
   sender propagation (via `AckTarget`) back to the temp actor.
6. **Reply `CommandNack` directly to `Sender` only** when `OfferAsync`
   returns a non-`Enqueued` result (queue closed, dropped, failure),
   signaling that the channel refused the message without routing it.

The channel-level inbound ACL check (e.g.,
`SlackAclPolicy.EvaluateInbound`) SHALL be bypassed because the supplied
`MessageSource.Principal` is `VerifiedAutomation` and the stored
reminder audience is already validated at minting time by
`reminder-audience-authorization`.

**Ack semantic**: the caller's `Ask<CommandAck>` completes when the
downstream `LlmSessionActor` has updated its in-memory state and fired
`TryReplyAck()` — meaning "the session has accepted this turn for
processing." This is stronger than "the channel has received the
message" and closes the in-process gap between gateway-offer and
stream-stage-run that a gateway-level ack would leave open.

The daemon currently hosts two gateways that MUST implement this handler:

- **`SlackGatewayActor`** — parses `{channelId}/{threadTs}` SessionIds,
  runs the conversation → thread binding lookup-or-create chain.
- **`SignalRGatewayActor`** (in `src/Netclaw.Daemon/Gateway/`) — parses
  `signalr/{guid}` SessionIds, runs the lookup-or-create chain used by
  `SessionRegistry.StartSignalRSession`. When no SignalR client is
  currently connected for the target session, the gateway SHALL still
  route the turn to the underlying `LlmSessionActor` via the pipeline;
  streaming output SHALL be dropped per the existing
  `OverflowStrategy.DropHead` behavior and the completed turn SHALL be
  visible to the user on next `ResumeSessionAsync`. This mirrors the
  current semantic when a TUI client disconnects mid-tool-call.

#### Scenario: Slack gateway handles DeliverTrustedSessionTurn

- **GIVEN** a Mode B reminder fires for
  `SessionId = "C0123ABC/1712000000.000000"`
- **WHEN** `SlackGatewayActor` receives a `DeliverTrustedSessionTurn` with
  that `SessionId`, the reminder prompt, and a `MessageSource` whose
  `Principal = VerifiedAutomation` and `Provenance.SourceKind = "reminder"`
- **THEN** the gateway runs its existing conversation → thread binding
  lookup-or-create chain for `(C0123ABC, 1712000000.000000)`
- **AND** a `ChannelInput` with the reminder content and
  `AckTarget = Sender` is queued into the thread binding's pipeline
- **AND** `SlackAclPolicy.EvaluateInbound` is NOT invoked (bypassed because
  the provenance indicates a trusted delivery)

#### Scenario: SignalR gateway handles DeliverTrustedSessionTurn with connected client

- **GIVEN** a Mode B reminder fires for `SessionId = "signalr/abc123"`
- **AND** a SignalR client is currently connected for that session
- **WHEN** `SignalRGatewayActor` receives a `DeliverTrustedSessionTurn`
  with that `SessionId` and a trusted `MessageSource`
- **THEN** the gateway runs the same lookup-or-create chain used by
  `SessionRegistry.StartSignalRSession` to reach the existing
  `SignalRSessionActor` for the session
- **AND** the `ChannelInput` with the reminder content and
  `AckTarget = Sender` is queued into the session pipeline
- **AND** the streaming response reaches the connected client in real
  time via the existing `SignalRSessionActor` → `SessionHub` bridge

#### Scenario: SignalR gateway handles DeliverTrustedSessionTurn with no client connected

- **GIVEN** a Mode B reminder fires for `SessionId = "signalr/abc123"`
- **AND** no SignalR client is currently connected for that session
  (e.g., TUI client exited between reminder set and fire)
- **WHEN** `SignalRGatewayActor` receives a `DeliverTrustedSessionTurn`
- **THEN** the gateway still routes the turn to the underlying
  `LlmSessionActor` via the session pipeline
- **AND** the session processes the turn and persists `TurnRecorded`
- **AND** streaming output is dropped per the existing
  `OverflowStrategy.DropHead` behavior of the output subscriber
- **AND** the session replies `CommandAck` to the dispatcher via
  `AckTarget`, allowing the reminder envelope to be acked exactly once
- **AND** the completed turn is visible when the user next calls
  `ResumeSessionAsync`

#### Scenario: Concurrent inbound and trusted delivery produce a single binding

- **GIVEN** a real inbound event and a Mode B reminder
  `DeliverTrustedSessionTurn` arrive at the same gateway in parallel,
  both targeting the same session addressing
- **WHEN** both handlers run concurrently
- **THEN** exactly one conversation/session actor chain exists for the
  session
- **AND** both messages are successfully queued into the same pipeline
  in arrival order

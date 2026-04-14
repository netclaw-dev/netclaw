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
  route through the originating channel's existing inbound path when the
  channel has a durable server-side transport binding (Slack): the reminder
  dispatcher tells the channel gateway a `DeliverTrustedSessionTurn`
  message, and the gateway runs the same lookup-or-create chain used for
  inbound events. For channels without durable server-side bindings (TUI,
  SignalR), the reminder dispatcher tells the session manager directly.
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

#### Scenario: Mode B non-Slack reminder bypasses channel gateway

- **GIVEN** a Mode B reminder persists `SessionId = "tui/abc123"` and
  `OriginChannelType = Tui`
- **WHEN** the Akka timer fires
- **THEN** the reminder dispatcher tells the session manager a
  `SendUserMessage` directly (no channel gateway involvement)
- **AND** the session actor for `tui/abc123` rehydrates if idle and
  processes the turn
- **AND** the `TurnRecorded` event persists into the session journal
- **AND** a connected TUI client receives streaming output if attached, or
  sees the persisted turn on next `netclaw chat --resume`

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

Channels that host a durable server-side transport binding (currently
Slack) MAY register a handler for this message on their gateway actor. The
handler SHALL parse `SessionId` into channel-specific addressing (e.g.,
`{channelId}/{threadTs}` for Slack), SHALL run the same lookup-or-create
chain used for real inbound events to reach the session's transport
binding, and SHALL queue the content into the existing pipeline with the
supplied `MessageSource` as provenance. The channel-level inbound ACL check
(e.g., `SlackAclPolicy.EvaluateInbound`) SHALL be bypassed because the
supplied `MessageSource.Principal` is `VerifiedAutomation` and the stored
reminder audience is already validated at minting time by
`reminder-audience-authorization`.

Channels without durable server-side bindings SHALL NOT register a handler.
Callers MUST route through the session manager directly for such channels.

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

#### Scenario: Concurrent inbound and trusted delivery produce a single binding

- **GIVEN** a real inbound Slack event and a Mode B reminder
  `DeliverTrustedSessionTurn` arrive at `SlackGatewayActor` in parallel,
  both targeting the same `(channelId, threadTs)` pair
- **WHEN** both handlers run concurrently
- **THEN** exactly one `SlackConversationActor` exists for the channel
- **AND** exactly one `SlackThreadBindingActor` exists for the thread
- **AND** both messages are successfully queued into the single thread
  binding's pipeline in arrival order

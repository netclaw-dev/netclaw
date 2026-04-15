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
- **Mode B** (session check-back — `SessionId` set,
  `ReportToChannel = null`): the entity key SHALL be the persisted
  `SessionId` and the timer fire SHALL re-enter the existing session
  actor (rehydrating from Akka.Persistence if currently passivated), NOT
  create a new session. Mode B delivery SHALL route through the
  originating channel's existing inbound actor hierarchy by telling the
  appropriate gateway a `DeliverTrustedSessionTurn` message:
  `ChannelType.Slack` → `SlackGatewayActor`; `ChannelType.Tui` or
  `ChannelType.SignalR` → `SignalRGatewayActor`. Each gateway routes the
  message down its existing hierarchy using the same lookup-or-create
  logic it uses for inbound events; `Forward` preserves `Sender`
  (the reminder dispatcher's `Ask<CommandAck>` temp actor) down the chain.
  Any other `OriginChannelType` is rejected at `set_reminder` time — Mode
  B requires a gateway that implements the `DeliverTrustedSessionTurn`
  handler.

In both Mode A and Mode B, the dispatched `SendUserMessage` SHALL carry
a `MessageSource` whose `ReminderId` field is populated with
`{reminderId}:{fireTimestampMs}` for idempotent best-effort redelivery
dedup at the session.

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

#### Scenario: Mode B Slack reminder routes through existing gateway chain

- **GIVEN** a Mode B reminder persists `SessionId = "C0123ABC/1712000000.000000"`
  and `OriginChannelType = Slack`
- **WHEN** the Akka timer fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s `SlackGatewayActor`
  a `DeliverTrustedSessionTurn` message
- **AND** `SlackGatewayActor`'s handler parses the `SessionId` into
  `(channelId, threadTs)` and uses
  `Context.Child(channelId).GetOrElse(...)` — the same pattern its
  existing `SlackInboundMessage` handler uses — to reach or create the
  conversation actor
- **AND** `conversation.Forward(msg)` preserves `Sender`
- **AND** `SlackConversationActor`'s handler uses the same lookup
  pattern to reach or create the thread binding actor
- **AND** `binding.Forward(msg)` preserves `Sender`
- **AND** `SlackThreadBindingActor`'s handler reads `Sender` and offers
  a `ChannelInput` (with `MessageSource.AckTarget = Sender`) into the
  pipeline queue
- **AND** the pipeline delivers a `SendUserMessage` to the existing
  `LlmSessionActor` for that session
- **AND** NO new session actor with a `schedule/...` entity key is created

#### Scenario: Mode B SignalR reminder routes through SignalR gateway

- **GIVEN** a Mode B reminder persists `SessionId = "signalr/abc123"` and
  `OriginChannelType = Tui` (or `SignalR`)
- **WHEN** the Akka timer fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s `SignalRGatewayActor`
  a `DeliverTrustedSessionTurn` message
- **AND** `SignalRMessageExtractor.EntityId` matches the message via its
  `IWithSessionId` fallback and extracts the session ID
- **AND** `GenericChildPerEntityParent` routes the message to the
  existing (or newly-created) `SignalRSessionActor` for that session
- **AND** the session actor's handler reads `Sender` and offers a
  `ChannelInput` (with `MessageSource.AckTarget = Sender`) into the
  pipeline queue
- **AND** NO new session actor with a `schedule/...` entity key is created
- **AND** if a SignalR client is connected, it receives the streaming
  response in real time; otherwise the turn persists and is visible on
  next `ResumeSessionAsync`

#### Scenario: Timer adapter does not fire for paused tasks

- **GIVEN** a scheduled task is in `paused` status
- **WHEN** the system checks for timer scheduling
- **THEN** no timer is registered for the paused task
- **AND** no `SendUserMessage` command is produced

## ADDED Requirements

### Requirement: ChannelInput / MessageSource ack target propagation

`MessageSource` SHALL expose an optional `AckTarget` field of type
`IActorRef?`. `MessageSource` is explicitly non-persisted (marked
`[ProtoIgnore]` on `SendUserMessage.Source`), so adding a runtime
`IActorRef` is safe. `ChannelPipeline.MapToCommand`'s stream sink SHALL
propagate this value as the `Tell` sender when dispatching the resulting
`SendUserMessage` to the session manager. When `AckTarget` is null, the
sink SHALL use `ActorRefs.NoSender` exactly as today, preserving
fire-and-forget semantics for regular user-message ingress.

This extension exists so that trusted deliveries (e.g., Mode B reminders)
can receive the session's existing `CommandAck` reply without the session
actor or the pipeline needing to special-case reminder messages. The
session's existing `TryReplyAck()` helper replies to `Sender`, which is
the `AckTarget` actor for trusted deliveries and `NoSender` for regular
user messages.

#### Scenario: Regular inbound message preserves fire-and-forget semantics

- **GIVEN** a Slack user sends a message in an active thread
- **WHEN** the pipeline maps the `ChannelInput` (whose
  `Source.AckTarget = null`) to `SendUserMessage`
- **THEN** the session manager is told with `ActorRefs.NoSender`
- **AND** the session's `TryReplyAck()` call goes to `DeadLetters`
  (the existing no-op behavior — the helper checks for `IsNobody()`)

#### Scenario: Trusted delivery receives CommandAck via AckTarget

- **GIVEN** a reminder dispatcher's `Ask<CommandAck>` reaches a channel
  gateway's `DeliverTrustedSessionTurn` handler
- **AND** the handler forwards the message down to the leaf binding/
  session actor, which builds a `ChannelInput` with
  `MessageSource.AckTarget = Sender` (the dispatcher's Ask temp actor)
- **AND** the pipeline stream sink Tells the session manager using
  `cmd.Source.AckTarget` as the sender
- **WHEN** the session's `HandleIncomingUserMessage` runs and fires
  `TryReplyAck()`
- **THEN** the session Tells `Sender` (the Ask temp actor) a
  `CommandAck`
- **AND** the dispatcher's `Ask<CommandAck>` completes successfully

### Requirement: Trusted session turn delivery protocol

The shared protocol message `DeliverTrustedSessionTurn` SHALL be defined
in `Netclaw.Actors.Protocol` with the following shape:

```
DeliverTrustedSessionTurn(
    SessionId SessionId,
    string Content,
    MessageSource Source) : IWithSessionId
```

Every server-side channel gateway in the daemon that supports Mode B
reminder re-entry SHALL register a `Receive<DeliverTrustedSessionTurn>`
handler that mirrors the gateway's existing inbound-routing logic — the
same lookup-or-create pattern used to route real inbound events down to
the leaf binding/session actor. The handler SHALL parse `SessionId`
into channel-specific addressing, SHALL use `Context.Child(name)
.GetOrElse(...)` (or the equivalent `GenericChildPerEntityParent`
routing path) to reach the next actor in the hierarchy, and SHALL
`Forward(msg)` the message down to preserve the original `Sender`. The
channel-level inbound ACL check (e.g.,
`SlackAclPolicy.EvaluateInbound`) SHALL NOT be called from this handler
— the two message types (inbound event and trusted delivery) have
separate handlers with separate logic, so no shared code path exists
where a flag could accidentally leak the bypass.

At the leaf actor (`SlackThreadBindingActor` for Slack,
`SignalRSessionActor` for SignalR), the handler SHALL read `Sender`
(the Ask temp actor, preserved via the `Forward` chain) and build a
`ChannelInput` carrying the reminder `Content`, the supplied
`MessageSource` (with `ReminderId`, trusted provenance, and stored
audience), and `MessageSource.AckTarget = Sender`. It SHALL offer the
`ChannelInput` to the pipeline queue via `inputQueue.OfferAsync(...)`.
On non-`Enqueued` offer result, the leaf actor SHALL Tell `Sender` a
`CommandNack` directly so the reminder dispatcher's Ask can complete
with failure.

The daemon currently hosts two gateways that MUST implement this
handler chain:

- **`SlackGatewayActor`** (three-level hierarchy: gateway →
  `SlackConversationActor` → `SlackThreadBindingActor`). Each level
  gets its own `Receive<DeliverTrustedSessionTurn>` handler. The
  gateway-level handler parses `{channelId}/{threadTs}` from the
  `SessionId`, looks up or creates the conversation by channel ID, and
  forwards. The conversation-level handler looks up or creates the
  thread binding by thread TS, and forwards. The binding-level handler
  offers the `ChannelInput` to the pipeline.

- **`SignalRGatewayActor`** (flat hierarchy via
  `GenericChildPerEntityParent` + `SignalRMessageExtractor`).
  `SignalRMessageExtractor.EntityId` SHALL be extended with an
  `IWithSessionId` fallback so the shared `DeliverTrustedSessionTurn`
  message (which implements `IWithSessionId`) is routable by the
  extractor without needing to implement the channel-internal
  `ISignalRSessionMessage` interface. `ISignalRSessionMessage` remains
  `internal` — no upstream dependency leak. `SignalRSessionActor` gets
  one new `Receive<DeliverTrustedSessionTurn>` handler that offers the
  `ChannelInput` to its pipeline.

#### Scenario: Slack gateway handles DeliverTrustedSessionTurn

- **GIVEN** a Mode B reminder fires for
  `SessionId = "C0123ABC/1712000000.000000"`
- **WHEN** `SlackGatewayActor` receives a `DeliverTrustedSessionTurn`
  with that `SessionId`, the reminder prompt, and a `MessageSource`
  whose `Principal = VerifiedAutomation` and
  `Provenance.SourceKind = "reminder"`
- **THEN** the gateway parses the `SessionId` into
  `(channelId, threadTs)` and looks up or creates the conversation
  actor using `Context.Child(channelId).GetOrElse(...)`
- **AND** the gateway Forwards the message to the conversation
- **AND** the conversation looks up or creates the thread binding
  using the same pattern and Forwards
- **AND** the thread binding reads `Sender`, constructs a
  `ChannelInput` with `MessageSource.AckTarget = Sender`, and offers
  it to the pipeline queue
- **AND** `SlackAclPolicy.EvaluateInbound` is NOT invoked

#### Scenario: SignalR gateway handles DeliverTrustedSessionTurn

- **GIVEN** a Mode B reminder fires for `SessionId = "signalr/abc123"`
- **WHEN** `SignalRGatewayActor` receives a
  `DeliverTrustedSessionTurn` with that `SessionId`
- **THEN** `SignalRMessageExtractor.EntityId` returns `"signalr/abc123"`
  via its `IWithSessionId` fallback
- **AND** `GenericChildPerEntityParent` routes the message to the
  `SignalRSessionActor` for that session (creating one if needed)
- **AND** the session actor reads `Sender`, constructs a `ChannelInput`
  with `MessageSource.AckTarget = Sender`, and offers it to the
  pipeline queue
- **AND** if a SignalR client is currently connected, the streaming
  response reaches the client in real time via the existing
  `SignalRSessionActor` → `SessionHub` bridge
- **AND** if no client is currently connected, the session still
  processes the turn and persists `TurnRecorded`; streaming output is
  dropped per the existing `OverflowStrategy.DropHead` behavior; the
  execution actor still receives `CommandAck` because `TryReplyAck`
  fires regardless of subscribers

#### Scenario: Concurrent inbound and trusted delivery produce a single binding

- **GIVEN** a real inbound event and a Mode B reminder
  `DeliverTrustedSessionTurn` arrive at the same gateway in parallel,
  both targeting the same session addressing
- **WHEN** both handlers run concurrently
- **THEN** exactly one conversation/session actor chain exists for the
  session (the existing `Context.Child(name).GetOrElse(...)` lookup is
  idempotent under actor supervision)
- **AND** both messages are successfully queued into the same pipeline
  in arrival order

#### Scenario: Gateway rejects on pipeline queue backpressure

- **GIVEN** a Mode B `DeliverTrustedSessionTurn` reaches the leaf
  binding/session actor
- **WHEN** `inputQueue.OfferAsync(channelInput)` returns a
  non-`Enqueued` result (e.g., `Dropped`, `Failure`, `QueueClosed`)
- **THEN** the leaf actor Tells `Sender` (the Ask temp actor) a
  `CommandNack` directly
- **AND** the reminder dispatcher's `Ask<CommandAck>` completes with
  `CommandNack`
- **AND** the reminder execution actor does NOT call
  `_client.AckAsync(envelope)`
- **AND** Akka.Reminders redelivers the envelope per its policy

### Requirement: SignalR message extractor IWithSessionId fallback

`SignalRMessageExtractor` SHALL extend its `EntityId` implementation to
fall through to `IWithSessionId.SessionId.Value` when a message does not
implement the channel-internal `ISignalRSessionMessage` interface. This
allows upstream protocol messages (such as `DeliverTrustedSessionTurn`)
that implement `IWithSessionId` to be routed through the SignalR
gateway's `GenericChildPerEntityParent` without needing to leak
`ISignalRSessionMessage` as a public interface.

```csharp
public override string? EntityId(object message) => message switch
{
    ISignalRSessionMessage msg => msg.SessionId.Value,
    IWithSessionId wid         => wid.SessionId.Value,
    _ => null
};
```

The existing `ISignalRSessionMessage` match SHALL continue to fire
first so that channel-internal routing messages are unchanged.
`ISignalRSessionMessage` SHALL remain `internal`.

#### Scenario: Internal SignalR message routes via ISignalRSessionMessage

- **GIVEN** a `StartSignalRSession` message (implements
  `ISignalRSessionMessage`) arrives at `SignalRGatewayActor`
- **WHEN** `SignalRMessageExtractor.EntityId` inspects the message
- **THEN** the first pattern matches and returns `msg.SessionId.Value`
- **AND** routing proceeds as today

#### Scenario: Upstream protocol message routes via IWithSessionId fallback

- **GIVEN** a `DeliverTrustedSessionTurn` message (implements
  `IWithSessionId` but not `ISignalRSessionMessage`) arrives at
  `SignalRGatewayActor`
- **WHEN** `SignalRMessageExtractor.EntityId` inspects the message
- **THEN** the second pattern matches and returns `wid.SessionId.Value`
- **AND** `GenericChildPerEntityParent` routes the message to the
  matching `SignalRSessionActor` child

#### Scenario: Unroutable message returns null

- **GIVEN** a message that implements neither `ISignalRSessionMessage`
  nor `IWithSessionId`
- **WHEN** `SignalRMessageExtractor.EntityId` inspects it
- **THEN** `EntityId` returns `null`
- **AND** `GenericChildPerEntityParent` does not route the message

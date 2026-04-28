# netclaw-input-adapters Specification

## Purpose

Define the unified input adapter architecture that treats all message sources
identically. All inputs produce a `SendUserMessage` command routed to the
session parent actor. This capability covers transport-agnostic session
commands, source metadata, entity key routing, broadcast subscription for
reply delivery, the Slack Socket Mode adapter, and the internal timer adapter.

## Requirements

### Requirement: Transport-agnostic session commands

All input adapters SHALL produce `SendUserMessage` as the universal command
contract for delivering input to session actors. Session actors SHALL never
reference adapter-specific types. The `SendUserMessage` command and broadcast
events SHALL be the only contract between adapters and session actors.

#### Scenario: Slack adapter produces SendUserMessage

- **GIVEN** a Slack `app_mention` event is received
- **WHEN** the Slack adapter processes the event
- **THEN** the adapter produces a `SendUserMessage` command
- **AND** the command contains the message content, entity key, and source
  metadata

#### Scenario: Timer adapter produces SendUserMessage

- **GIVEN** an Akka timer fires for a scheduled task
- **WHEN** the timer adapter processes the tick
- **THEN** the adapter produces a `SendUserMessage` command
- **AND** the command contains the task instruction as message content

#### Scenario: Session actor is adapter-agnostic

- **GIVEN** a session actor receives a `SendUserMessage` command
- **WHEN** the session processes the turn
- **THEN** the session actor does not import or reference any adapter-specific
  types
- **AND** the session behavior is identical regardless of the originating
  adapter

### Requirement: Source metadata on all commands

All inbound `SendUserMessage` commands SHALL carry source metadata sufficient
for ACL evaluation and audit logging. For threaded authorized turns that adopt
prior context, source metadata SHALL identify the current authorized sender as
the executable-turn source, while adopted prior messages are represented only in
the adopted-context audit record and canonical projection. That projection SHALL
continue to name adopted speakers by stable sender id even though they are not
treated as executable-turn sources.

#### Scenario: Slack source metadata populated

- **GIVEN** a Slack message event is received
- **WHEN** the Slack adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `slack`
- **AND** includes the Slack user ID as sender identity
- **AND** includes the Slack channel ID
- **AND** includes the event timestamp

#### Scenario: Timer source metadata populated

- **GIVEN** an Akka timer fires for a scheduled task
- **WHEN** the timer adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `timer`
- **AND** includes the task creator as sender identity
- **AND** includes the task ID as the channel equivalent
- **AND** includes the timer fire timestamp

#### Scenario: ACL uses source metadata for evaluation

- **GIVEN** a `SendUserMessage` command arrives with source metadata
- **WHEN** the ACL gate evaluates the command
- **THEN** the evaluation uses the sender identity from source metadata
- **AND** the evaluation uses the channel identifier from source metadata

#### Scenario: Authorized threaded turn source metadata points at authorizer

- **GIVEN** a thread where unauthorized messages were adopted
- **WHEN** the authorized turn is created
- **THEN** the command source metadata identifies the authorized current sender
- **AND** adopted prior senders are not treated as independent live turn sources

### Requirement: Authorized threaded turns adopt unsynced context

When a threaded adapter receives an authorized inbound message, it SHALL hydrate
the unsynced thread gap before that message and construct a single authorized
turn envelope containing:

- a canonical adopted-context projection for the adopted window; and
- the current authorized executable message.

The adopted-context portion SHALL be quoted context only. The current
authorized message SHALL be the only executable user instruction in that turn.

When adopted context is present, the threaded adapter MAY construct the
canonical adopted-context projection before handoff. The session SHALL durably
persist that exact projection together with the adopted-message metadata before
execution continues. Retries or recovery for the same authorized message id
SHALL reuse the persisted adopted-context record rather than re-derive a
different projection from raw thread history.

If the unsynced gap is empty, the adapter SHALL omit adopted-context
persistence and adopted-context framing and SHALL send only the current
authorized message as an ordinary authorized turn.

#### Scenario: Authorized message carries adopted window plus executable message

- **GIVEN** a thread has unsynced prior messages
- **AND** an authorized user sends the next inbound message
- **WHEN** the adapter constructs the session input
- **THEN** exactly one `SendUserMessage` is created
- **AND** it contains the adopted-context projection first
- **AND** it contains the current authorized message second
- **AND** only the current authorized message is executable

#### Scenario: Zero-gap authorized message omits adopted-context framing

- **GIVEN** the watermark already covers all prior thread messages before the
  current authorized inbound
- **WHEN** the adapter constructs the session input
- **THEN** no adopted-context projection is prepended
- **AND** the session receives only the current authorized message text

### Requirement: Unauthorized live threaded messages stay off the turn path

Threaded adapters SHALL NOT map unauthorized live inbound messages to
`SendUserMessage` commands. Those messages SHALL remain pending source-thread
context until a later authorized message adopts them.

#### Scenario: Unauthorized live message does not become a turn

- **GIVEN** a threaded Slack message from a non-allowed user
- **WHEN** no authorized user is speaking on that inbound event
- **THEN** no `SendUserMessage` command is created
- **AND** the message does not enter slash-command dispatch or model execution

### Requirement: Canonical framing and reserved-marker escaping

The channel pipeline SHALL use the following canonical framing for authorized
threaded turns:

```text
[adopted-context]
[adopted-message id={messageId} author={senderId} authority-at-inclusion={authorized|pending} ts={timestamp}]
{escaped adopted text}
[/adopted-message]
[/adopted-context]
[current-authorized-message author={senderId} ts={timestamp}]
{escaped current text}
[/current-authorized-message]
```

Any user-originated line beginning with a reserved marker prefix SHALL be
escaped by prefixing that line with `\` before inclusion in the canonical
projection.

The adapter owns source-thread gap fetch and watermark bookkeeping. After the
authorized turn is accepted for enqueue, it SHALL persist a pending cursor for
that authorized message. The adapter SHALL advance the durable
authorized-sync watermark only after `TurnCompleted` or other durable turn
completion confirms that the turn was durably recorded. This sequencing SHALL
remain fail-closed for crash recovery.

#### Scenario: Adopted message text with reserved marker is escaped

- **GIVEN** an adopted source message begins with `[adopted-context]`
- **WHEN** the projection is built
- **THEN** the emitted line begins with `\[adopted-context]`
- **AND** the model-visible framing remains unambiguous

#### Scenario: Current authorized message with reserved marker is escaped

- **GIVEN** the authorized sender's text begins with `[/adopted-message]`
- **WHEN** the projection is built
- **THEN** the line is escaped before inclusion under
  `[current-authorized-message ...]`

### Requirement: Entity key routing

The session parent actor SHALL extract an entity key from each
`SendUserMessage` command and route to the correct child session actor. Slack
messages SHALL use entity key pattern `{channelId}/{threadTs}`. Timer
messages SHALL use entity key pattern `schedule/{taskId}/{runTs}`. TUI
messages SHALL use entity key pattern `tui/{sessionId}`.

#### Scenario: Slack message routed by thread identity

- **GIVEN** a Slack message arrives from channel `C0123` in thread `T456`
- **WHEN** the session parent extracts the entity key
- **THEN** the entity key is `C0123/T456`
- **AND** the command is routed to the session actor for that key

#### Scenario: Timer message routed by task and run identity

- **GIVEN** a timer fires for task `ebay-check` at timestamp `1708531200`
- **WHEN** the session parent extracts the entity key
- **THEN** the entity key is `schedule/ebay-check/1708531200`
- **AND** a new session actor is created for that entity key

#### Scenario: TUI message routed by session identity

- **GIVEN** a TUI message arrives with session ID `a1b2c3`
- **WHEN** the session parent extracts the entity key
- **THEN** the entity key is `tui/a1b2c3`
- **AND** the command is routed to the session actor for that key

#### Scenario: Repeated messages in same thread route to same actor

- **GIVEN** a session actor exists for entity key `C0123/T456`
- **WHEN** another message arrives in the same Slack thread
- **THEN** the message is routed to the existing session actor
- **AND** no new session actor is created

#### Scenario: Repeated TUI messages route to same actor

- **GIVEN** a session actor exists for entity key `tui/a1b2c3`
- **WHEN** the operator sends another message in the same chat session
- **THEN** the message is routed to the existing session actor

### Requirement: Broadcast subscription for reply delivery

Input adapters SHALL subscribe to session broadcast events to deliver replies
back through the originating channel. Adapters SHALL consume broadcast events
through pub/sub without direct transport coupling to session actors.

#### Scenario: Slack adapter receives reply broadcast

- **GIVEN** the Slack adapter is subscribed to session broadcasts
- **WHEN** a session actor emits a turn broadcast with a reply
- **THEN** the Slack adapter receives the broadcast
- **AND** delivers the reply to the originating Slack thread

#### Scenario: Timer result broadcast consumed by Slack adapter

- **GIVEN** a scheduled task session completes with results
- **WHEN** the session emits a result broadcast
- **THEN** the Slack adapter receives the broadcast
- **AND** posts the results to the task's configured reporting channel

#### Scenario: Multiple adapters can subscribe to same session

- **GIVEN** both a Slack adapter and a future UI adapter are running
- **WHEN** a session emits a broadcast
- **THEN** both adapters receive the broadcast independently
- **AND** each adapter delivers through its own channel

### Requirement: Slack Socket Mode adapter

The Slack adapter SHALL connect via Slack Socket Mode, handle `app_mention`
events, dispatch `SendUserMessage` commands to the session parent, and
deliver reply broadcasts back to the originating Slack thread.

#### Scenario: Socket Mode connection established at startup

- **GIVEN** valid Slack app and bot tokens are configured
- **WHEN** Netclaw starts
- **THEN** the Slack adapter opens a Socket Mode connection
- **AND** reports connection health in operator diagnostics

#### Scenario: App mention event dispatched as session command

- **GIVEN** the Slack adapter is connected
- **WHEN** an `app_mention` event is received from an allowed channel
- **THEN** the adapter extracts entity key `{channelId}/{threadTs}`
- **AND** creates a `SendUserMessage` with the message text, entity key, and
  Slack source metadata
- **AND** routes the command to the session parent actor

#### Scenario: Reply delivered to originating thread

- **GIVEN** a session processes a turn from a Slack message
- **WHEN** the session emits a reply broadcast
- **THEN** the Slack adapter posts the reply in the same thread
- **AND** uses the Slack bot token for the API call

#### Scenario: Socket Mode reconnects on disconnect

- **GIVEN** the Slack Socket Mode connection drops
- **WHEN** the adapter detects the disconnection
- **THEN** the adapter attempts to reconnect
- **AND** logs the disconnection and reconnection events

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

### Requirement: TUI input adapter

The TUI adapter SHALL receive keyboard input via Termina TextInputNode, produce
`SendUserMessage` commands with entity key `tui/{sessionId}`, subscribe to
session broadcasts, and render responses as streaming text. The TUI adapter
SHALL be a Phase 1 input source.

#### Scenario: TUI adapter produces SendUserMessage

- **GIVEN** the operator is in a `netclaw chat` session
- **WHEN** the operator types a message and presses Enter
- **THEN** the TUI adapter produces a `SendUserMessage` command
- **AND** the command contains the message content, entity key `tui/{sessionId}`,
  and source metadata with adapter type `tui`

#### Scenario: TUI adapter renders streaming response

- **GIVEN** a session actor is processing a turn from the TUI adapter
- **WHEN** the session emits token-level broadcast events
- **THEN** the TUI adapter renders tokens in real-time via StreamingTextNode
- **AND** the response appears incrementally in the chat history

#### Scenario: TUI adapter displays tool invocation status

- **GIVEN** a session is executing tool calls
- **WHEN** a tool invocation starts
- **THEN** the TUI adapter displays an inline tool activity panel
- **AND** shows the tool name with a spinner indicator
- **WHEN** the tool invocation completes
- **THEN** the spinner is replaced with a checkmark and duration

#### Scenario: TUI adapter subscribes to session broadcasts

- **GIVEN** the TUI adapter has sent a `SendUserMessage` command
- **WHEN** the session actor emits a `TurnBroadcast` event
- **THEN** the TUI adapter receives the broadcast
- **AND** renders the response content in the chat history

#### Scenario: TUI source metadata populated

- **GIVEN** the operator sends a message via `netclaw chat`
- **WHEN** the TUI adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `tui`
- **AND** includes `local-operator` as sender identity
- **AND** includes the session ID as channel identifier
- **AND** includes the current timestamp

### Requirement: Channel-agnostic thread history fetcher contract

The channel abstraction layer SHALL define an `IThreadHistoryFetcher` interface
that returns an ordered `IReadOnlyList<ChannelInput>` for a given `SessionId`.
Each channel adapter that supports threaded conversations MAY implement this
interface as an optional capability. Adapters that do not support threads
(e.g., timer, TUI) SHALL NOT implement it. The `ChannelInput` contract SHALL
NOT carry a backfill-related flag — hydration is an adapter-internal concern
and the session layer SHALL be unaware of whether history was merged into an
inbound message.

#### Scenario: Fetcher returns chronologically ordered channel inputs

- **GIVEN** a threaded channel adapter implements `IThreadHistoryFetcher`
- **WHEN** `FetchThreadHistoryAsync(sessionId, ct)` is invoked
- **THEN** the returned list contains `ChannelInput` items in chronological
  order (oldest first)
- **AND** the return type contains no channel-specific types

#### Scenario: Non-threaded adapters do not implement history fetch

- **GIVEN** a timer adapter or TUI adapter
- **WHEN** the adapter is registered in DI
- **THEN** no `IThreadHistoryFetcher` implementation is registered for that
  adapter
- **AND** no hydration logic runs for messages it emits

#### Scenario: Session layer is unaware of hydration

- **GIVEN** a `ChannelInput` produced by a threaded adapter after hydration
- **WHEN** the channel pipeline transforms it into a `SendUserMessage`
- **THEN** the resulting command carries no backfill flag
- **AND** the session actor processes it as a normal user turn

### Requirement: Channel interactive approval capability

Each channel implementation SHALL declare whether it supports interactive
approval via a capability flag (`SupportsInteractiveApproval`). The capability
SHALL be queryable from `ToolExecutionContext` or `MessageSource` at tool
invocation time. Channels that support interactive approval MUST be able to
render `ToolInteractionRequest` outputs and route `ToolInteractionResponse`
messages back to the session actor.

#### Scenario: Slack channel declares approval support

- **GIVEN** the Slack channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `true`

#### Scenario: Headless channel declares no approval support

- **GIVEN** the headless (single-prompt CLI) channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `false`

#### Scenario: Capability flows to tool execution context

- **GIVEN** a session on the Slack channel
- **WHEN** a tool execution context is created
- **THEN** the context includes the channel's `SupportsInteractiveApproval`
  value
- **AND** `ToolAccessPolicy` can use it to determine approval behavior

### Requirement: Fallback text rendering for basic channels

Channels that support interactive approval but lack rich UI SHALL render
approval prompts as numbered text option lists and parse user responses by
option number or keyword matching. This covers future SMS or plain-text
adapters.

#### Scenario: Text-only channel renders ABC options

- **GIVEN** a channel with interactive approval support but no rich UI
- **WHEN** a `ToolInteractionRequest` is received
- **THEN** the channel posts a text-based approval prompt with labeled options
- **AND** user replies "A", "a", or "approve once" are accepted

#### Scenario: Text-only channel routes parsed response

- **GIVEN** the user replies "B" to an approval prompt
- **WHEN** the channel parses the reply
- **THEN** it sends a `ToolInteractionResponse` with `ApprovedAlways`

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

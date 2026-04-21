# netclaw-scheduling spec delta: reminder-delivery-contract

## MODIFIED Requirements

### Requirement: Isolated task execution

Each scheduled task execution SHALL run in a mode selected explicitly at
set time by `ReminderDefinition.Delivery.Kind`:

- `DeliveryKind.CurrentSession` → **re-enter the originating session**
  (no new session actor created; rehydrates from Akka.Persistence if
  passivated).
- `DeliveryKind.Channel` → **spawn a fresh isolated session** whose LLM
  uses the transport's canonical notification tool (e.g.,
  `send_slack_message` for `Transport = "slack"`) to post to
  `Delivery.Address`.
- `DeliveryKind.None` → **spawn a fresh isolated session** that runs
  the task and records history but emits no external output.

Isolated sessions (Channel / None) SHALL load the agent personality and
project context overlays and SHALL NOT share state with interactive
sessions. Session-reentry executions (CurrentSession) SHALL reuse the
persisted state of the originating session actor and SHALL NOT create a
new session.

Execution mode SHALL NOT be inferred from the presence or absence of
any other field — only `Delivery.Kind` determines the branch.

Execution MAY trust the stored reminder audience because reminder
minting and import paths SHALL validate the persisted audience before
the definition is saved. In all modes, the effective audience at
execution time SHALL be the stored reminder audience, not the live
audience of the originating session.

#### Scenario: Fresh session for Channel delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = Channel`,
  `Delivery.Transport = "slack"`, and `Delivery.Address = "C0123ABC"`
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** agent personality is loaded from soul files
- **AND** the LLM's available tools include `send_slack_message`

#### Scenario: Fresh session for None delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = None`
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** no notification tool (`send_slack_message`, etc.) is present
  in the LLM's tool set
- **AND** `ReminderExecutionCompleted(success=true)` is reported on
  natural turn completion

#### Scenario: Session re-entry for CurrentSession delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = CurrentSession`,
  `Delivery.SessionId` set, and `Delivery.OriginChannelType` set
- **WHEN** the timer tick triggers execution
- **THEN** the existing session actor for the persisted `SessionId` is
  addressed (rehydrating from Akka.Persistence if currently passivated)
- **AND** NO new session actor is created with a `schedule/...` entity key
- **AND** the reminder turn is delivered as a `SendUserMessage` whose
  `MessageSource.ChannelType` matches `Delivery.OriginChannelType`

#### Scenario: Scheduled session isolated from interactive sessions

- **GIVEN** an interactive Slack session exists for the same user
- **WHEN** a Channel-kind scheduled task executes
- **THEN** the scheduled session does not read or modify interactive
  session state
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

Task execution results SHALL be delivered according to
`ReminderDefinition.Delivery.Kind`:

- `Channel`: results SHALL be posted to `Delivery.Address` via
  `Delivery.Transport`'s canonical notification tool, called by the
  isolated session's LLM. `Delivery.Address` SHALL always be a
  canonical identifier produced by the transport's
  `IReminderTargetResolver` (never a raw LLM-supplied string).
- `CurrentSession`: the reminder turn SHALL be routed through the
  originating channel's existing inbound handling path. The daemon
  hosts two server-side gateways; both implement a
  `Receive<DeliverTrustedSessionTurn>` handler that reuses the
  gateway's existing routing code. The reminder dispatcher SHALL tell
  the appropriate gateway based on `Delivery.OriginChannelType`:
  `ChannelType.Slack` → `SlackGatewayActor`;
  `ChannelType.Tui` or `ChannelType.SignalR` → `SignalRGatewayActor`.
  The channel-level inbound ACL SHALL be bypassed because the
  reminder's audience was validated at minting time. Any other
  `OriginChannelType` SHALL be rejected at `set_reminder` time.
- `None`: no external delivery SHALL be performed. Execution history
  SHALL still be recorded.

Optional `ReminderDefinition.DeliveryInstructions` SHALL guide the content
the LLM produces for `Channel` and `CurrentSession` deliveries but
SHALL NOT affect routing.

#### Scenario: Channel results posted via transport notification tool

- **GIVEN** a reminder with `Delivery.Kind = Channel`,
  `Delivery.Transport = "slack"`, `Delivery.Address = "C0123ABC"`
- **WHEN** the task execution completes with results
- **THEN** the LLM calls `send_slack_message` with the address and the
  result content
- **AND** the results are posted to the Slack channel via the
  reminder's isolated execution session

#### Scenario: CurrentSession Slack delivery routes through existing gateway chain

- **GIVEN** a `CurrentSession` reminder created from a Slack thread
  session with `Delivery.OriginChannelType = Slack`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s
  `SlackGatewayActor` with a `DeliverTrustedSessionTurn` carrying the
  originating `SessionId`, reminder prompt, and trusted `MessageSource`
- **AND** the gateway's handler parses the `SessionId` into
  `(channelId, threadTs)` and uses its existing
  `Context.Child(name).GetOrElse(...)` lookup to reach the conversation
  actor
- **AND** `conversation.Forward(msg)` preserves `Sender`
- **AND** `SlackConversationActor`'s handler uses the same lookup
  pattern to reach the thread binding actor
- **AND** `binding.Forward(msg)` preserves `Sender`
- **AND** `SlackThreadBindingActor`'s handler reads `Sender`, builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender` and
  `MessageSource.ReminderId` populated, and offers it to the pipeline
  queue
- **AND** the reminder turn is delivered through the normal
  `ChannelInput` → `ChannelPipeline` → `SendUserMessage` → session
  pipeline
- **AND** the session's streaming response is posted back to the
  original Slack thread via the binding's existing output sink
- **AND** `SlackAclPolicy.EvaluateInbound` is NOT called

#### Scenario: CurrentSession SignalR delivery routes through existing gateway chain

- **GIVEN** a `CurrentSession` reminder created from a SignalR session
  (including TUI) with `Delivery.OriginChannelType` = `Tui` or
  `SignalR` and `Delivery.SessionId = "signalr/{guid}"`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s
  `SignalRGatewayActor` with a `DeliverTrustedSessionTurn`
- **AND** `SignalRMessageExtractor.EntityId` matches the message via
  its `IWithSessionId` fallback and extracts the session GUID
- **AND** `GenericChildPerEntityParent` routes the message to the
  existing `SignalRSessionActor` child for that session (creating one
  if needed)
- **AND** `SignalRSessionActor`'s handler reads `Sender`, builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender`, and offers
  it to the pipeline queue
- **AND** if a SignalR client is currently connected, the streaming
  response reaches the client in real time via the existing bridge
- **AND** if no client is currently connected, the session still
  processes the turn and persists `TurnRecorded`; streaming output is
  dropped per the existing `OverflowStrategy.DropHead` behavior and is
  visible on next `ResumeSessionAsync`

#### Scenario: None delivery records history and emits nothing

- **GIVEN** a reminder with `Delivery.Kind = None`
- **WHEN** the task execution completes
- **THEN** no message is posted and no session turn is delivered
- **AND** the execution is recorded in
  `~/.netclaw/reminders/{id}.history.jsonl` with `success=true`

### Requirement: Reminder delivery target validation

The `set_reminder` tool SHALL validate the structured
`delivery: { kind, transport?, address? }` parameter at invocation
time and SHALL persist only canonical, validated values.

For `delivery.kind = CurrentSession`:

- The tool SHALL require an addressable tool execution context
  (`context.SessionId` present and `context.ChannelType` parseable to
  `ChannelType.Slack`, `ChannelType.Tui`, or `ChannelType.SignalR`).
- The tool SHALL reject any non-null `delivery.transport` or
  `delivery.address` with an actionable error.
- The persisted `ReminderDefinition.Delivery.SessionId` SHALL equal
  `context.SessionId`; `Delivery.OriginChannelType` SHALL equal the
  parsed channel type; `Delivery.Transport` and `Delivery.Address`
  SHALL be null.

For `delivery.kind = Channel`:

- Both `delivery.transport` and `delivery.address` SHALL be non-empty.
- The tool SHALL look up an `IReminderTargetResolver` whose `Transport`
  property equals `delivery.transport` (case-insensitive). If no
  matching resolver is registered, the tool SHALL fail loud with an
  error identifying the unknown transport.
- The matching resolver SHALL be called to canonicalize
  `delivery.address`. The persisted `Delivery.Address` SHALL be the
  resolver's canonical identifier — never the raw LLM input. An
  unresolvable address SHALL cause the tool invocation to fail
  immediately with an actionable error.
- `Delivery.SessionId` and `Delivery.OriginChannelType` SHALL be null.
- Transports without a canonical notification tool
  (SignalR / TUI today) SHALL be rejected for
  `delivery.kind = Channel` with a clear error telling the LLM to use
  `CurrentSession` for those origins.

For `delivery.kind = None`:

- `delivery.transport` and `delivery.address` SHALL both be null.
- All `Delivery.*` fields except `Kind` SHALL be null in the persisted
  definition.
- `DeliveryRequired` SHALL be ignored for `None` deliveries since there
  is no delivery to fail. The field MAY be set to any value; it has no
  effect on execution outcome.

#### Scenario: Hash-prefixed Slack channel resolved to canonical ID

- **GIVEN** a host with a registered `IReminderTargetResolver` whose
  `Transport = "slack"` and that maps `#general` to `C0123ABC`
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"`, `delivery.transport = "slack"`,
  `delivery.address = "#general"`
- **THEN** the persisted `ReminderDefinition.Delivery.Address` equals
  `C0123ABC`
- **AND** `Delivery.Transport` equals `"slack"`
- **AND** `Delivery.Kind` equals `Channel`
- **AND** the tool response reports success with the resolved schedule

#### Scenario: Unknown transport fails loud

- **GIVEN** a host with only a `slack` resolver registered
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"`, `delivery.transport = "discord"`,
  `delivery.address = "#general"`
- **THEN** the tool returns an error string naming the unknown
  transport and listing available transports
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Unresolvable address returns actionable tool error

- **GIVEN** a host with a registered `slack` resolver that cannot
  resolve `#nonexistent-channel`
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"`, `delivery.transport = "slack"`,
  `delivery.address = "#nonexistent-channel"`
- **THEN** the tool returns an error string beginning with
  `Error: Could not resolve`
- **AND** no `ReminderDefinition` is persisted
- **AND** no `SaveReminderCommand` is sent to the reminder manager

#### Scenario: CurrentSession without addressable context rejected

- **GIVEN** a tool execution context with `ChannelType = Headless` (or
  `Webhook`, `Reminder`) and a non-null `SessionId`
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "current_session"`
- **THEN** the tool returns an error explaining that CurrentSession is
  only supported for channels with a `DeliverTrustedSessionTurn`
  gateway (Slack, Tui, SignalR)
- **AND** no `ReminderDefinition` is persisted

#### Scenario: CurrentSession rejects channel fields

- **GIVEN** an addressable Slack session context
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "current_session"` AND either
  `delivery.transport` or `delivery.address` non-null
- **THEN** the tool returns an error stating that transport/address
  are invalid for CurrentSession
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Channel without transport rejected

- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"` and `delivery.transport` or
  `delivery.address` missing
- **THEN** the tool returns an error naming which required field is
  missing
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Channel kind rejected for session-only transports

- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"` and `delivery.transport = "signalr"` (or
  `"tui"`)
- **THEN** the tool returns an error explaining that SignalR/TUI do
  not support channel-target delivery, and advising the LLM to use
  `delivery.kind = "current_session"`
- **AND** no `ReminderDefinition` is persisted

#### Scenario: None delivery rejects transport and address

- **WHEN** the LLM calls `set_reminder` with `delivery.kind = "none"`
  and either `delivery.transport` or `delivery.address` non-null
- **THEN** the tool returns an error stating that transport/address
  are invalid for None
- **AND** no `ReminderDefinition` is persisted

### Requirement: Envelope-ack-gated at-least-once delivery for Mode B

The `ReminderManagerActor` SHALL NOT eagerly ack the
`Aaron.Akka.Reminders` envelope for reminders with
`Delivery.Kind = CurrentSession` (the canonical term for what this
requirement historically called "Mode B"). It SHALL spawn `ReminderExecutionActor` and pass the
`ReminderEnvelope` to the child. The execution actor SHALL acquire
`IReminderClient` via `ReminderClientExtension.Get(Context.System)`
at startup.

The execution actor SHALL dispatch
`DeliverTrustedSessionTurn(SessionId, Content, MessageSource)` to the
target channel gateway using `Ask<CommandAck>` (Slack via
`SlackGatewayActor`, SignalR/TUI via `SignalRGatewayActor`, selected
by `Delivery.OriginChannelType`) with a timeout of
`ReminderSettings.DefaultAckTimeout`. The gateway's handler SHALL
propagate the message down its existing routing hierarchy via
`Forward` (preserving `Sender`) until it reaches the leaf binding /
session actor, which reads `Sender`, places it on the outgoing
`ChannelInput` as `MessageSource.AckTarget`, and populates
`MessageSource.ReminderId` with the reminder delivery key.
`ChannelPipeline.MapToCommand`'s stream sink SHALL use
`cmd.Source?.AckTarget ?? ActorRefs.NoSender` as the `Tell` sender.
`LlmSessionActor`'s `TryReplyAck()` fires `CommandAck` to that sender,
completing the dispatcher's `Ask`.

When `Delivery.DeliveryRequired = true`, the execution actor SHALL
also wait for a `ReminderDeliveryObserved(reminderId, channelType)`
signal emitted by `ChannelPipeline`'s outbound stage when the
session's assistant reply whose source turn carries a matching
`SourceReminderId` flows out through the channel's subscriber sink.
The execution actor SHALL NOT call `AckAsync(envelope)` until both
`CommandAck` and `ReminderDeliveryObserved` are received for the
reminder. The outbound wait SHALL use a dedicated timeout
(`DeliveryObservedTimeout`, internal const on
`ReminderExecutionActor`) strictly greater than
`DefaultAckTimeout`.

When `Delivery.DeliveryRequired = false`, `CommandAck` alone SHALL
satisfy the acknowledgment; the outbound signal wait SHALL be skipped.

On successful ack conditions, the execution actor SHALL call
`await _client.AckAsync(envelope)`, inspect the
`ReminderAckResponse.ResponseCode`, log on non-`Success`, and tell
`Context.Parent` a `ReminderExecutionCompleted(success=true)`. On
Ask-timeout, `CommandNack`, gateway/transport exception, OR
delivery-observed timeout with `DeliveryRequired = true`, the
execution actor SHALL NOT call `AckAsync`; it SHALL tell the parent a
`ReminderExecutionCompleted(success=false)` with a descriptive error
message. The un-acked envelope SHALL be redelivered by
`Aaron.Akka.Reminders` per its built-in `AckTimeout` and
`MaxDeliveryAttempts` defaults.

For `Delivery.Kind ∈ {Channel, None}`, the manager SHALL continue to
call `_client.AckAsync(envelope)` eagerly after spawning the execution
actor; delivery-success tracking for those kinds flows through
`ExecutionOutputAccumulator` / `ReminderExecutionCompleted` /
`FailurePauseThreshold` as today.

Redelivery SHALL be best-effort deduped: the target session dedup
pre-checks the reminder's `(reminderId, fireTimestampMs)` pair against
its in-memory `ProcessedReminderIds` set and SHALL reply `CommandAck`
without processing a duplicate when the dedup check hits.

#### Scenario: CurrentSession envelope held until outbound delivery observed

- **GIVEN** a `CurrentSession` reminder with `DeliveryRequired = true`
  fires
- **WHEN** the execution child `Ask<CommandAck>`s the target channel
  gateway with a `DeliverTrustedSessionTurn` and the session's
  `TryReplyAck()` replies `CommandAck`
- **THEN** the execution child does NOT yet call
  `_client.AckAsync(envelope)`
- **WHEN** the session completes its turn and the assistant reply
  carrying the matching `SourceReminderId` flows out through the
  channel pipeline's outbound stage
- **THEN** `ChannelPipeline` emits a
  `ReminderDeliveryObserved(reminderId, channelType)` signal addressed
  to the execution actor
- **AND** the execution actor calls `await _client.AckAsync(envelope)`
  exactly once
- **AND** the execution actor tells `Context.Parent` a
  `ReminderExecutionCompleted(success=true)`

#### Scenario: CurrentSession outbound delivery timeout fails loud

- **GIVEN** a `CurrentSession` reminder with `DeliveryRequired = true`
  fires and `CommandAck` was received from the session
- **WHEN** `DeliveryObservedTimeout` elapses without a
  `ReminderDeliveryObserved` signal
- **THEN** the execution actor does NOT call `_client.AckAsync(envelope)`
- **AND** the execution actor tells `Context.Parent` a
  `ReminderExecutionCompleted(success=false)` with a
  "delivery not observed" error
- **AND** `OperationalAlert.ReminderExecutionFailed` is emitted
- **AND** `Aaron.Akka.Reminders` redelivers the envelope on next fire

#### Scenario: CurrentSession with DeliveryRequired=false acks on CommandAck alone

- **GIVEN** a `CurrentSession` reminder with `DeliveryRequired = false`
  fires
- **WHEN** `CommandAck` is received from the session
- **THEN** the execution actor immediately calls
  `await _client.AckAsync(envelope)`
- **AND** tells `Context.Parent` a
  `ReminderExecutionCompleted(success=true)`
- **AND** no `ReminderDeliveryObserved` signal wait is attempted

#### Scenario: Session Ask-timeout triggers Akka.Reminders redelivery

- **GIVEN** a `CurrentSession` reminder fires and the target channel
  gateway has been dispatched a `DeliverTrustedSessionTurn`
- **AND** the pipeline or session fails to reply `CommandAck` within
  `ReminderSettings.DefaultAckTimeout`
- **WHEN** the execution actor's `Ask<CommandAck>` times out
- **THEN** the execution actor does NOT call `_client.AckAsync(envelope)`
- **AND** the execution actor tells `Context.Parent` a
  `ReminderExecutionCompleted(success=false)` with a timeout error
- **AND** `Aaron.Akka.Reminders` marks the envelope as ack-timed-out
  and redelivers it per its built-in `MaxDeliveryAttempts` default

#### Scenario: Channel kind keeps eager envelope ack

- **GIVEN** a reminder with `Delivery.Kind = Channel` fires
- **WHEN** `ReminderManagerActor.HandleReminderFiredAsync` runs
- **THEN** the manager calls `_client.AckAsync(envelope)` after
  spawning the execution actor
- **AND** execution-success/failure tracking flows through
  `ReminderExecutionCompleted` and
  `OperationalAlert.ReminderExecutionFailed`

#### Scenario: Redelivered CurrentSession reminder is deduped on the target session

- **GIVEN** a `CurrentSession` reminder was previously processed by
  the session (evidenced by a `TurnRecorded` event whose
  `SourceReminderId` matches the reminder's
  `{reminderId}:{fireTimestampMs}` and is present in
  `ProcessedReminderIds`)
- **WHEN** Akka.Reminders redelivers the same envelope after a
  transient failure
- **THEN** the session dedup pre-check fires in
  `HandleIncomingUserMessage` and `TryReplyAck()` returns `CommandAck`
  without re-processing the turn
- **AND** `ReminderDeliveryObserved` fires because the prior turn's
  outbound reply replay produces an observable delivery signal OR
  the execution actor treats the dedup-ack path as observed for
  acking purposes (implementation detail documented in design.md)
- **AND** the execution actor calls `_client.AckAsync(envelope)` once,
  closing out the redelivery loop

## ADDED Requirements

### Requirement: Stale reminder schema hard-delete on startup

`ReminderDefinitionStore` SHALL attempt to deserialize each persisted
reminder under the current `ReminderDefinition` schema at startup.
Rows that fail deserialization SHALL be deleted from disk (hard
delete) and SHALL NOT be restored as scheduled Akka.Reminders entries.
The store SHALL emit an `OperationalAlert` at `Warning` severity
listing the IDs of dropped reminders so the operator can re-create
them.

Netclaw is pre-production with a single operator; no in-place
migration is required. This requirement is authoritative for the
reminder-delivery-contract schema break.

#### Scenario: Stale reminder dropped at startup

- **GIVEN** `netclaw.db` contains a `netclaw_reminders` row with a
  pre-`reminder-delivery-contract` shape (populated
  `ReportToChannel` / `NotifyInstructions`, no `Delivery` struct)
- **WHEN** the daemon starts and `ReminderDefinitionStore` loads
- **THEN** the row is deleted from `netclaw_reminders`
- **AND** no Akka.Reminders schedule is created for it
- **AND** an `OperationalAlert` is emitted at `Warning` severity
  naming the dropped reminder ID and the reason
- **AND** the daemon startup completes successfully

### Requirement: Transport-keyed reminder target resolver registration

`IReminderTargetResolver` SHALL expose a `Transport` property (string)
identifying the channel transport the resolver owns (e.g., `"slack"`).
Hosts SHALL register resolver implementations via DI such that
`SetReminderTool` receives them as `IEnumerable<IReminderTargetResolver>`
and SHALL dispatch `delivery.address` resolution to the resolver whose
`Transport` matches `delivery.transport` (case-insensitive).

Hosts SHALL NOT register two resolvers with the same `Transport`
value; if this is detected at startup, host initialization SHALL fail
loud.

`SetReminderTool` SHALL reject `delivery.kind = Channel` with an
unknown or unregistered `delivery.transport` at tool-call time with an
error enumerating the registered transports.

#### Scenario: Slack resolver registered and keyed correctly

- **GIVEN** a host with `SlackReminderTargetResolver` registered
- **WHEN** `set_reminder` is called with
  `delivery.transport = "slack"`, `delivery.address = "#general"`
- **THEN** the Slack resolver's `ResolveAsync` is invoked with
  `"#general"`
- **AND** the returned canonical ID is persisted as
  `Delivery.Address`

#### Scenario: Duplicate-transport registration fails startup

- **GIVEN** a host that registers two `IReminderTargetResolver`
  instances both reporting `Transport = "slack"`
- **WHEN** the daemon starts
- **THEN** host initialization fails with an error naming the duplicate
  transport
- **AND** the daemon does not enter the ready state

#### Scenario: Unknown transport rejected with available-transport list

- **GIVEN** a host with only a `slack` resolver registered
- **WHEN** `set_reminder` is called with
  `delivery.transport = "discord"`
- **THEN** the tool returns an error naming `"discord"` as unknown and
  listing `["slack"]` as the registered transports
- **AND** no reminder is persisted

## REMOVED Requirements

### Requirement: Reminder notification target validation

**Reason**: Replaced by `Reminder delivery target validation` which
validates the full structured `delivery` object (not just a raw
`reportToChannel` string) and dispatches through transport-keyed
resolvers.

**Migration**: None. Pre-existing stored reminders are hard-deleted at
startup by the new `Stale reminder schema hard-delete on startup`
requirement. Operators re-create their reminders using the new tool
surface.

## MODIFIED Requirements

### Requirement: Result reporting

Task execution results SHALL be delivered according to
`ReminderDefinition.Delivery.Kind`:

- `Channel`: results SHALL be posted to `Delivery.Address` via
  `Delivery.Transport`'s canonical notification tool, called by the
  isolated session's LLM. `Delivery.Address` SHALL always be a
  canonical identifier produced by the transport's
  `IReminderTargetResolver` (never a raw LLM-supplied string).
- `CurrentSession`: the reminder turn SHALL be routed through the
  originating channel's existing session route. The reminder dispatcher
  SHALL select the gateway from the stored `Delivery.OriginChannelType`:
  `ChannelType.Slack` → `SlackGatewayActor`;
  `ChannelType.Discord` → `DiscordGatewayActor`;
  `ChannelType.Mattermost` → `MattermostGatewayActor`;
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

#### Scenario: Mattermost current-session reminder preserves the thread and authority

- **GIVEN** a Team-audience Mattermost session identified by `{channelId}/{rootPostId}`
- **WHEN** `set_reminder` accepts `delivery_kind = current_session`
- **THEN** the stored delivery contains that session ID and `OriginChannelType = Mattermost`
- **AND** the stored audience does not exceed the creator's source audience
- **WHEN** the reminder fires
- **THEN** the dispatcher uses the Mattermost gateway, conversation, and session binding
- **AND** the response reaches the original channel and thread
- **AND** the run succeeds when the session acknowledges the fire, whether or not `delivery_required` is set

#### Scenario: Unsupported current-session origin is rejected before persistence

- **GIVEN** a webhook session without a current-session gateway
- **WHEN** `set_reminder` receives `delivery_kind = current_session`
- **THEN** the tool rejects the reminder before it sends a save command

#### Scenario: Mattermost post failure does not count as delivery

- **GIVEN** a Mattermost current-session reminder with `delivery_required = true`
- **AND** the session acknowledged the fire
- **WHEN** the Mattermost post API later rejects the response
- **THEN** the execution still records a successful outcome
- **AND** the reminder system does not retry the fire
- **AND** the session's own delivery retry handles the failed post

## ADDED Requirements

### Requirement: Envelope-ack-gated delivery ends at admission

The reminder manager SHALL retain each Akka.Reminders envelope until the attempt
has a known outcome. This rule SHALL apply to every delivery kind, so `Channel`
and `None` reminders no longer get an eager acknowledgement.

The execution actor SHALL report its outcome to the manager. It SHALL wait for
`ReminderExecutionAccepted` before it stops.

The manager SHALL acknowledge only a successful execution with all required
delivery evidence. It SHALL negatively acknowledge a known failure.

A `CurrentSession` reminder SHALL use the origin gateway and
`Ask<CommandAck>`. The run SHALL end when the session acknowledges the fire
(admission). The run SHALL NOT wait for a post to the channel, whatever the
value of `DeliveryRequired`. `DeliveryRequired` SHALL have no effect for a
`CurrentSession` reminder. A `CommandNack` or an Ask timeout SHALL fail the run.

The target session SHALL decide alone what happens to an admitted fire. It
SHALL use the stable key of the occurrence, which is built from the occurrence
due time.

#### Scenario: CurrentSession succeeds at admission

- **GIVEN** a `CurrentSession` reminder has `DeliveryRequired = true`
- **WHEN** the target session returns `CommandAck`
- **THEN** the child reports success to the manager
- **AND** the manager acknowledges the occurrence
- **AND** no delivery result is awaited

#### Scenario: CurrentSession with DeliveryRequired=false acks on CommandAck alone

- **GIVEN** a `CurrentSession` reminder with `DeliveryRequired = false` fires
- **WHEN** `CommandAck` is received from the session
- **THEN** the child reports success to the manager

#### Scenario: CurrentSession refusal fails the run

- **GIVEN** a `CurrentSession` reminder fires
- **WHEN** the gateway or the session replies `CommandNack`, or the Ask times out
- **THEN** the child reports a descriptive failure
- **AND** the manager sends a negative acknowledgement

#### Scenario: Channel execution fails

- **GIVEN** a `Channel` reminder starts an isolated execution
- **WHEN** the execution or notification fails
- **THEN** the manager does not acknowledge success
- **AND** the manager sends a negative acknowledgement

#### Scenario: None delivery succeeds

- **GIVEN** a reminder uses `Delivery.Kind = None`
- **WHEN** its execution completes successfully
- **THEN** the manager acknowledges the occurrence

#### Scenario: The child reports success before it stops

- **GIVEN** an execution child reports success
- **WHEN** the manager saves local state and acknowledges the occurrence
- **THEN** the manager sends `ReminderExecutionAccepted`
- **AND** the child stops after that message

#### Scenario: Redelivered CurrentSession reminder is deduped on the target session

- **GIVEN** a `CurrentSession` fire was admitted by the session, so its journal
  holds the input with source key `{reminderId}:{fireTimestampMs}`
- **WHEN** Akka.Reminders redelivers the same envelope after a transient failure
- **THEN** the session acknowledges it without a second input
- **AND** the manager settles the occurrence once, closing the redelivery loop

### Requirement: Reminder delivery guarantees end at admission

The reminder pipeline SHALL provide at-least-once attempt delivery until the
manager confirms execution. For a `CurrentSession` reminder, execution ends at
admission by the session.

A crash before acknowledgement SHALL leave the occurrence eligible for retry. A
crash after acknowledgement SHALL not lose successful work.

The stable occurrence identity and the stored source key of the admitted input
SHALL reduce duplicate work. Netclaw SHALL not claim exactly-once delivery.

#### Scenario: The daemon stops during execution

- **GIVEN** a reminder attempt has not reached manager acknowledgement
- **WHEN** the daemon stops
- **THEN** the acknowledgement lease expires
- **AND** Akka.Reminders can retry the occurrence

#### Scenario: The daemon stops after acknowledgement

- **GIVEN** execution succeeded
- **AND** the manager acknowledged the occurrence
- **WHEN** the daemon stops before one-shot terminal state is saved
- **THEN** durable occurrence status remains `Delivered`
- **AND** reconciliation repairs the one-shot terminal state

#### Scenario: Duplicate across snapshot recovery is caught

- **GIVEN** a `CurrentSession` fire was admitted
- **AND** a `SessionSnapshot` was taken
- **AND** the session later recovers from that snapshot
- **WHEN** Akka.Reminders redelivers the original fire
- **THEN** the snapshot holds the source key of the fire
- **AND** the session acknowledges the redelivery without a second input

#### Scenario: Delivery guarantees documented in reminder-set confirmation

- **GIVEN** a `CurrentSession` reminder is successfully set
- **WHEN** the tool returns its success message
- **THEN** the message conveys that the reminder will fire and deliver a new turn
  to the originating session

### Requirement: A reminder has at most one waiting fire in a session

A session SHALL keep at most one waiting fire for each reminder. A fire waits
only while the session's in-memory input queue holds it. A turn that selects a
fire removes it from the queue. When a newer
fire of the same reminder arrives while an earlier fire waits, the session SHALL
acknowledge the newer fire and SHALL NOT store it. The session SHALL log
`reminder_fire_dropped`. The reminder system SHALL see success.

Recovery rebuilds the queue for inputs that wait behind an open approval, so
the rule holds after a restart or a passivation for those inputs. A stored
input that nothing will run (a crash, a restart with several requesters, an
expired restart reminder, or a restart message that found another turn running
left it pending) is not waiting. It SHALL NOT block a later fire. The session
SHALL NOT drop a fire while the earlier fire runs. The session SHALL NOT drop a fire because another
reminder has a waiting fire. A key that is not a reminder fire key
(`{reminderId}:{fireTimeMs}`) SHALL never be dropped and SHALL never block a fire.

The decision SHALL cause no side effect. A dropped or duplicate fire SHALL NOT
abandon an open tool batch.

#### Scenario: A newer fire is dropped while the earlier fire waits

- **GIVEN** a reminder fires every 12 minutes
- **AND** the 10:00 fire waits behind a running turn
- **WHEN** the 10:12 fire arrives
- **THEN** the session acknowledges it and stores no input
- **AND** the 10:00 fire runs after the turn
- **AND** the 10:24 fire waits again, and the session admits it

#### Scenario: A fire waits behind an approval, also after a restart

- **GIVEN** the 10:00 fire waits behind an open approval
- **AND** the daemon restarted, so recovery rebuilt the waiting fire from the journal into the queue
- **WHEN** the 10:12 fire arrives
- **THEN** the session acknowledges it and stores no input

#### Scenario: A fire that a crash left pending does not block the next fire

- **GIVEN** the 10:00 fire was stored and waited behind a running turn
- **AND** the daemon crashed, so the new session holds the 10:00 fire in its journal but not in its queue, and no restart reminder exists
- **WHEN** the 10:12 fire arrives
- **THEN** the session stores it as an input
- **AND** the 10:12 fire runs

#### Scenario: A key that is not a fire key is never dropped

- **GIVEN** a reminder input is stored with the key `weekly:abc` (the time is not digits)
- **WHEN** a reminder input with the key `weekly:def` arrives
- **THEN** the session stores it as an input
- **AND** it logs no `reminder_fire_dropped`
- **AND** the stored `weekly:abc` input does not block `weekly:1700000000000`

#### Scenario: A fire is admitted while the earlier fire runs

- **GIVEN** a running turn was started by the 10:00 fire
- **WHEN** the 10:12 fire arrives
- **THEN** the session stores it as a waiting input
- **AND** both fires run

#### Scenario: Different reminders never drop each other

- **GIVEN** the reminders `check-pr` and `standup` both fire during a busy turn
- **THEN** the session stores both fires
- **AND** a name prefix (`check` and `check-pr`) does not make two reminders equal

#### Scenario: A redelivered fire is a duplicate, not a drop

- **GIVEN** the session admitted the 10:00 fire
- **WHEN** Akka.Reminders redelivers the 10:00 fire
- **THEN** the session acknowledges it and stores no input
- **AND** a recovered session writes no `ToolBatchAbandoned` for it

## REMOVED Requirements

### Requirement: Envelope-ack-gated at-least-once delivery

**Reason**: A `CurrentSession` run no longer waits for delivery evidence. The requirement returns as "Envelope-ack-gated delivery ends at admission".
**Migration**: None. Every delivery kind keeps the envelope-ack rule.

### Requirement: Reminder delivery guarantees

**Reason**: Guarantees now end at admission by the session. The requirement returns as "Reminder delivery guarantees end at admission".
**Migration**: None.

### Requirement: Delivery observation timeout alignment

**Reason**: The reminder system no longer waits for a delivery result. A `CurrentSession` run ends at admission, so no delivery observation timeout exists.
**Migration**: None. The execution attempt timeout and the stall timeout stay.

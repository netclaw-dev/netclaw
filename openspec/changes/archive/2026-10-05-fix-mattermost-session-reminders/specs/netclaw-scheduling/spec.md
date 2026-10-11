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
- **AND** `delivery_required = true` succeeds only after a successful post

#### Scenario: Unsupported current-session origin is rejected before persistence

- **GIVEN** a webhook session without a current-session gateway
- **WHEN** `set_reminder` receives `delivery_kind = current_session`
- **THEN** the tool rejects the reminder before it sends a save command

#### Scenario: Mattermost post failure does not count as delivery

- **GIVEN** a Mattermost current-session reminder with `delivery_required = true`
- **WHEN** the Mattermost post API rejects its response
- **THEN** the execution records a failed outcome
- **AND** a session acknowledgement does not count as a successful post


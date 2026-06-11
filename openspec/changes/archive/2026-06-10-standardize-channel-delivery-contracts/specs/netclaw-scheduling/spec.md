## MODIFIED Requirements

### Requirement: Isolated task execution

Each scheduled task execution SHALL run in a mode selected explicitly at
set time by `ReminderDefinition.Delivery.Kind`:

- `DeliveryKind.CurrentSession` → **re-enter the originating session**
  (no new session actor created; rehydrates from Akka.Persistence if
  passivated).
- `DeliveryKind.Channel` → **spawn a fresh isolated session** whose LLM
  uses the generic `send_channel_message` notification tool (with
  `channel_key` set from `Transport`, e.g. `"slack"`) to post to
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
- **AND** the LLM's available tools include `send_channel_message`

#### Scenario: Fresh session for None delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = None`
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** no notification tool (`send_channel_message`, etc.) is present
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
- **THEN** the LLM calls `send_channel_message` with a destination resolved
  to the address and the result content
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

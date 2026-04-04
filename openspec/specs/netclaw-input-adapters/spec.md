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
for ACL evaluation and audit logging. Source metadata SHALL include adapter
type, sender identity, channel identifier, and timestamp.

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

The timer adapter SHALL fire on Akka timer ticks for scheduled tasks, create
`SendUserMessage` commands with the task instruction as content, and use entity
key pattern `schedule/{taskId}/{runTs}`. Each timer fire SHALL create a fresh
session for isolated execution.

#### Scenario: Timer fires for active scheduled task

- **GIVEN** an active scheduled task has a timer registered
- **WHEN** the Akka timer fires
- **THEN** the timer adapter creates a `SendUserMessage` command
- **AND** the message content is the task's instruction prompt
- **AND** the entity key is `schedule/{taskId}/{runTs}`

#### Scenario: Fresh session created per timer execution

- **GIVEN** a timer fires for task `daily-report`
- **WHEN** the timer adapter dispatches the command
- **THEN** a new session actor is created for the unique entity key
- **AND** the session loads the agent personality from soul files
- **AND** the session does not reuse any previous execution's state

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

Channels that support interactive approval but lack rich UI (e.g., future SMS
or plain-text adapters) SHALL render approval prompts as numbered text option
lists and parse user responses by option number or keyword matching.

#### Scenario: Text-only channel renders ABC options

- **GIVEN** a channel with interactive approval support but no rich UI
- **WHEN** a `ToolInteractionRequest` is received
- **THEN** the channel posts a text-based approval prompt with labeled options
- **AND** user replies "A", "a", or "approve once" are accepted

#### Scenario: Text-only channel routes parsed response

- **GIVEN** the user replies "B" to an approval prompt
- **WHEN** the channel parses the reply
- **THEN** it sends a `ToolInteractionResponse` with `ApprovedAlways`

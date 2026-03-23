## ADDED Requirements

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

## MODIFIED Requirements

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

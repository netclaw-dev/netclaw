## MODIFIED Requirements

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

#### Scenario: Discord source metadata populated

- **GIVEN** a Discord message event is received
- **WHEN** the Discord adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `discord`
- **AND** includes the Discord user ID as sender identity
- **AND** includes the Discord channel ID
- **AND** includes the event timestamp

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
messages SHALL use entity key pattern `tui/{sessionId}`. Discord messages SHALL
use entity key pattern `{channelId}/{threadIdOrMessageId}`, where
`threadIdOrMessageId` is the Discord thread ID when present, otherwise the root
message ID.

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

#### Scenario: Discord threaded message routed by thread identity

- **GIVEN** a Discord message arrives from channel `ch-7` in thread `th-42`
- **WHEN** the session parent extracts the entity key
- **THEN** the entity key is `ch-7/th-42`
- **AND** the command is routed to the session actor for that key

#### Scenario: Discord non-threaded message routed by root message identity

- **GIVEN** a Discord message arrives from channel `ch-7` without thread context
- **WHEN** the session parent extracts the entity key
- **THEN** the entity key is `ch-7/m-9001`
- **AND** repeated replies using that root context route to the same actor

#### Scenario: Repeated messages in same thread route to same actor

- **GIVEN** a session actor exists for entity key `C0123/T456`
- **WHEN** another message arrives in the same Slack thread
- **THEN** the message is routed to the existing session actor
- **AND** no new session actor is created

#### Scenario: Repeated TUI messages route to same actor

- **GIVEN** a session actor exists for entity key `tui/a1b2c3`
- **WHEN** the operator sends another message in the same chat session
- **THEN** the message is routed to the existing session actor

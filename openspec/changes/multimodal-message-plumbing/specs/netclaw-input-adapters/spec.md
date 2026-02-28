## MODIFIED Requirements

### Requirement: Transport-agnostic session commands

All input adapters SHALL produce `SendUserMessage` as the universal command
contract for delivering input to session actors. Session actors SHALL never
reference adapter-specific types. The `SendUserMessage` command SHALL carry
both text content and media file references so that non-text content from any
adapter can reach the session actor. Broadcast events SHALL be the only
contract between adapters and session actors.

#### Scenario: Slack adapter produces SendUserMessage

- **GIVEN** a Slack `app_mention` event is received
- **WHEN** the Slack adapter processes the event
- **THEN** the adapter produces a `SendUserMessage` command
- **AND** the command contains the message content, entity key, and source
  metadata

#### Scenario: Slack adapter produces SendUserMessage with image

- **GIVEN** a Slack `app_mention` event is received with file attachments
- **WHEN** the Slack adapter processes the event
- **THEN** the adapter downloads supported image attachments to the session
  media directory
- **AND** the `SendUserMessage` command contains both text content and media
  file references

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

### Requirement: Broadcast subscription for reply delivery

Input adapters SHALL subscribe to session broadcast events to deliver replies
back through the originating channel. Adapters SHALL consume broadcast events
through pub/sub without direct transport coupling to session actors. Adapters
SHALL handle `FileOutput` events according to their channel capabilities.

#### Scenario: Slack adapter receives reply broadcast

- **GIVEN** the Slack adapter is subscribed to session broadcasts
- **WHEN** a session actor emits a turn broadcast with a reply
- **THEN** the Slack adapter receives the broadcast
- **AND** delivers the reply to the originating Slack thread

#### Scenario: Slack adapter receives FileOutput broadcast

- **GIVEN** the Slack adapter is subscribed to session broadcasts
- **WHEN** a session actor emits a `FileOutput` event
- **THEN** the Slack adapter SHALL upload the file to the Slack thread using
  `files.uploadV2`

#### Scenario: TUI adapter receives FileOutput broadcast

- **GIVEN** the TUI adapter is subscribed to session broadcasts
- **WHEN** a session actor emits a `FileOutput` event
- **THEN** the TUI adapter SHALL print the local file path to the terminal

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

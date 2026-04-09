# thread-history-backfill Specification

## Purpose

Define the channel-agnostic contract for fetching thread history on first
mention, normalizing multimodal content, and injecting it as read-only
session context before the first LLM turn.

## Requirements

### Requirement: Channel-agnostic thread history fetch contract

The system SHALL define an `IThreadHistoryFetcher` interface in the channel
abstraction layer that returns an ordered list of `ChannelInput` items
representing the thread's prior messages. Each channel adapter that supports
threaded conversations SHALL implement this interface. The session layer
SHALL NOT reference any channel-specific types when consuming backfilled
history.

#### Scenario: Slack adapter implements history fetcher

- **GIVEN** a Slack channel adapter is configured
- **WHEN** the thread history fetcher is resolved for a Slack session
- **THEN** the returned implementation uses Slack's `conversations.replies` API
- **AND** the result type is `IReadOnlyList<ChannelInput>`

#### Scenario: Session layer consumes history without channel coupling

- **GIVEN** backfilled history is provided as `IReadOnlyList<ChannelInput>`
- **WHEN** the session processes the backfill
- **THEN** no Slack-specific, Teams-specific, or Discord-specific types are
  referenced by the session actor or pipeline

### Requirement: Multimodal content in backfill

Backfilled messages SHALL include all content types supported by the existing
inbound pipeline: text, images, and file attachments. Image content SHALL be
downloaded, content-scanned, and stored in the session media directory using
the same pipeline as live messages. Non-image file types SHALL follow the
same filtering rules as live inbound messages.

#### Scenario: Thread with images backfilled with full content

- **GIVEN** a thread contains 3 messages: text-only, text+image, image-only
- **WHEN** the history fetcher retrieves the thread
- **THEN** all 3 messages are returned as `ChannelInput` items
- **AND** images are downloaded via the file API with bot token auth
- **AND** images are content-scanned before inclusion
- **AND** image bytes are included as `DataContent` in the `ChannelInput`

#### Scenario: Content scan rejection in backfill

- **GIVEN** a backfilled message contains an image that fails content scanning
- **WHEN** the history fetcher processes that message
- **THEN** the image is excluded from the `ChannelInput` for that message
- **AND** the text portion of the message is still included
- **AND** remaining messages in the thread are not affected

#### Scenario: File download failure in backfill

- **GIVEN** a backfilled message contains an image whose download fails
- **WHEN** the history fetcher processes that message
- **THEN** the failed image is skipped with a warning log
- **AND** the text portion of the message is still included
- **AND** the overall backfill continues with remaining messages

### Requirement: Backfill as read-only context injection

Backfilled messages SHALL be injected into the session as a read-only context
block before the first LLM turn. The LLM SHALL see the thread history as
prior conversation context but SHALL NOT believe it participated in those
messages. The context block SHALL include sender attribution and timestamps
for each message.

#### Scenario: Backfill injected before first LLM turn

- **GIVEN** a thread has 5 prior messages before the bot is mentioned
- **WHEN** the session initializes and processes the mention
- **THEN** the 5 prior messages appear in the LLM context as a thread history
  block
- **AND** the thread history block precedes the mention message in context
  ordering

#### Scenario: Backfill includes sender attribution

- **GIVEN** a backfilled thread has messages from users Alice and Bob
- **WHEN** the context block is assembled
- **THEN** each message includes the sender's display identity
- **AND** each message includes a UTC timestamp

#### Scenario: LLM does not believe it participated in backfilled history

- **GIVEN** a session with backfilled thread history
- **WHEN** the LLM receives the context
- **THEN** the context block is framed as "messages exchanged before you were
  mentioned"
- **AND** the backfilled messages do not appear as assistant-role turns

### Requirement: Backfill only on new session creation

Thread history backfill SHALL only execute when a session is newly created
from a first mention. Sessions recovering from persistence (daemon restart)
SHALL NOT re-fetch thread history — their own persisted conversation state
is authoritative.

#### Scenario: First mention triggers backfill

- **GIVEN** no session exists for thread `C0123/T456`
- **WHEN** an `app_mention` event arrives in that thread
- **THEN** the adapter fetches thread history before delivering the mention
  to the session

#### Scenario: Recovered session skips backfill

- **GIVEN** a session for thread `C0123/T456` exists in persistence
- **WHEN** the daemon restarts and a new message arrives in that thread
- **THEN** the session recovers from persistence
- **AND** no thread history fetch is performed

#### Scenario: Existing active session skips backfill

- **GIVEN** a session for thread `C0123/T456` is already active
- **WHEN** another message arrives in that thread
- **THEN** the message is delivered directly to the existing session
- **AND** no thread history fetch is performed

### Requirement: No artificial caps on backfill size

The thread history fetcher SHALL retrieve all available messages in the
thread without imposing an artificial message count or token limit. If the
backfilled content exceeds the session's context window capacity, the
existing compaction pipeline SHALL handle overflow.

#### Scenario: Long thread backfilled in full

- **GIVEN** a thread contains 200 messages with mixed text and images
- **WHEN** the history fetcher retrieves the thread
- **THEN** all 200 messages are fetched (paginated as needed)
- **AND** all are included in the backfill context

#### Scenario: Backfill exceeds context window

- **GIVEN** backfilled thread history plus the mention message exceeds the
  compaction token limit
- **WHEN** the first LLM turn runs
- **THEN** the compaction pipeline activates
- **AND** the oldest backfilled content is compacted first

### Requirement: Bot message filtering in backfill

The history fetcher SHALL exclude the bot's own messages from backfilled
history. Messages from other bots and integration webhooks SHALL also be
excluded to reduce noise.

#### Scenario: Bot's own messages excluded

- **GIVEN** a thread contains messages from users and from the Netclaw bot
- **WHEN** the history fetcher retrieves the thread
- **THEN** messages from the Netclaw bot (matched by bot ID) are excluded
- **AND** user messages are included

#### Scenario: Other bot messages excluded

- **GIVEN** a thread contains messages from a CI bot and a user
- **WHEN** the history fetcher retrieves the thread
- **THEN** messages with a `bot_id` field are excluded
- **AND** user messages are included

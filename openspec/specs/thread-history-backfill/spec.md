# thread-history-backfill Specification

## Purpose

Define the channel-agnostic contract for hydrating thread history into the
first inbound event a session sees, and the semantics that any threaded
channel adapter must honour when implementing it. Hydration is an adapter-
internal concern: the session layer is unaware of whether any particular
`SendUserMessage` was enriched with prior-thread context.

## Requirements

### Requirement: Channel-agnostic thread history fetcher

The system SHALL define `IThreadHistoryFetcher` in the channel abstraction
layer. The interface SHALL return `IReadOnlyList<ChannelInput>` in
chronological order for a given `SessionId`. The interface SHALL be pure
retrieval — it SHALL NOT mutate session state, post messages, or update
cursors. Channel adapters that support threaded conversations SHALL provide
an implementation; adapters that do not SHALL NOT.

#### Scenario: Slack adapter provides a fetcher

- **GIVEN** the Slack channel adapter is registered
- **WHEN** `IThreadHistoryFetcher` is resolved from DI
- **THEN** `SlackThreadHistoryFetcher` is returned
- **AND** it uses `ISlackApiClient.Conversations.Replies` internally

#### Scenario: Fetcher is side-effect free

- **GIVEN** a fetcher implementation
- **WHEN** `FetchThreadHistoryAsync` is called
- **THEN** no session state is modified
- **AND** no cursor is advanced
- **AND** no messages are posted to the channel

### Requirement: Hydration merges into the triggering inbound event

Threaded channel adapters SHALL hydrate prior thread messages by merging
them into the triggering inbound event's `ChannelInput` before enqueueing,
not by delivering them as separate messages. The merged `ChannelInput` SHALL
contain a single `TextContent` that frames the historical content with
`[thread history — messages exchanged before this inbound event]` and
`[end thread history]` delimiters, followed by the triggering message's
live text. Image attachments from gap messages SHALL be appended as
`DataContent` items on the merged `ChannelInput`. The session layer SHALL
NOT receive a distinct "backfill" message.

#### Scenario: Merged input appears as a single user turn

- **GIVEN** 3 gap messages and 1 triggering mention
- **WHEN** hydration completes
- **THEN** exactly one `SendUserMessage` reaches the session
- **AND** the LLM sees the thread history and the live mention as a single
  user turn

#### Scenario: Historical sender attribution included

- **GIVEN** a gap message from user `U0123` at `2026-04-09 10:15 UTC`
- **WHEN** the merge runs
- **THEN** the text block contains `<user: U0123, 2026-04-09 10:15 UTC>`
  followed by that message's text

#### Scenario: Image attachments preserved

- **GIVEN** a gap message has one image attachment
- **WHEN** the merge runs
- **THEN** the image bytes appear as a `DataContent` on the merged input
- **AND** the text block records `[image attachments: 1]` for that entry

### Requirement: Multimodal content handling reuses the live pipeline

Image attachments in hydrated messages SHALL be downloaded via the adapter's
existing file-download path with the adapter's authentication credentials
and SHALL be content-scanned through `IContentScanner` before inclusion.
Per-message download or scan failures SHALL be logged and skipped without
aborting the rest of the hydration. Non-image file types SHALL follow the
same filtering rules as live inbound messages.

#### Scenario: Image rejected by content scanner is skipped

- **GIVEN** a gap message image fails content scanning
- **WHEN** the adapter processes that message
- **THEN** the image is excluded from the merged input
- **AND** the text portion of the message is still included
- **AND** the rest of the hydration continues

#### Scenario: Image download failure is skipped

- **GIVEN** a gap message image download fails
- **WHEN** the adapter processes that message
- **THEN** the failed image is skipped with a warning log
- **AND** the text portion of the message is still included

### Requirement: Prompt injection gate on hydrated content

Hydrated message text SHALL be evaluated by `IPromptInjectionDetector`
before being merged. Messages flagged `Risk = High` SHALL be dropped with a
warning log. If the detector fails for a hydrated message, that message
SHALL be dropped and the adapter SHALL surface a warning to the user in
the thread so they know some prior context was excluded. Adapters SHALL
NOT silently pass unchecked content through the hydration path.

#### Scenario: High-risk historical message dropped

- **GIVEN** a gap message triggers the injection detector with `High` risk
- **WHEN** the adapter processes that message
- **THEN** the message is excluded from the merged input
- **AND** a warning is logged with sender and message identifiers

#### Scenario: Detector failure surfaces a user-visible warning

- **GIVEN** the injection detector throws while evaluating a gap message
- **WHEN** the adapter processes that message
- **THEN** the message is excluded from the merged input
- **AND** the adapter posts a user-visible warning in the thread once per
  inbound event

### Requirement: Cursor-based gap computation and stale drop

Threaded channel adapters SHALL maintain a durable per-thread cursor marking
the most recently successfully processed inbound event. Inbound events whose
ordering key (e.g., Slack `ts`) is at or before the cursor SHALL be dropped
as stale with a telemetry counter recording the drop. On the first non-stale
inbound per adapter runtime the adapter SHALL hydrate the gap of messages
whose ordering key is strictly after the cursor and strictly before the
triggering event. The cursor SHALL be advanced only after the triggering
event has been enqueued onto the session input channel.

#### Scenario: Replay after restart is filtered

- **GIVEN** the cursor persisted before restart is `X`
- **WHEN** the adapter restarts and the transport replays an event with
  ordering key `≤ X`
- **THEN** the replayed event is dropped
- **AND** a `stale_event` telemetry counter is incremented

#### Scenario: Gap after restart is hydrated exactly once

- **GIVEN** the persisted cursor is `X` and the first inbound after restart
  has ordering key `Y > X`
- **WHEN** the event is processed
- **THEN** messages with ordering key strictly between `X` and `Y` are
  merged into the triggering event
- **AND** subsequent inbound events in the same runtime do not re-hydrate

#### Scenario: Cursor advances only after successful enqueue

- **GIVEN** hydration and merge succeed but the session input channel write
  fails
- **WHEN** the write failure is handled
- **THEN** the cursor is not advanced
- **AND** the next inbound event will attempt hydration again

### Requirement: No artificial hydration size cap

Adapters SHALL retrieve all messages in the gap without imposing an
artificial message count or token limit. If the merged content exceeds the
session's context window, the existing compaction pipeline SHALL handle
overflow.

#### Scenario: Long thread hydrated in full

- **GIVEN** a thread contains 200 messages with mixed text and images
- **WHEN** the adapter hydrates the gap on a fresh session
- **THEN** all 200 messages are fetched (paginated as needed)
- **AND** all surviving messages are included in the merged content

#### Scenario: Hydration exceeds context window

- **GIVEN** the merged `ChannelInput` exceeds the compaction token limit
- **WHEN** the first LLM turn runs
- **THEN** the compaction pipeline activates
- **AND** the oldest hydrated content is compacted first

### Requirement: Bot message filtering

Adapters SHALL exclude the bot's own messages and any other bot or
integration-webhook messages from hydration to reduce noise and prevent
circular context.

#### Scenario: Bot's own messages excluded

- **GIVEN** a thread contains messages from users and the Netclaw bot
- **WHEN** the adapter hydrates the thread
- **THEN** messages from the Netclaw bot are excluded

#### Scenario: Other bot messages excluded

- **GIVEN** a thread contains messages from a CI bot and a user
- **WHEN** the adapter hydrates the thread
- **THEN** messages carrying a `bot_id` are excluded
- **AND** user messages are included

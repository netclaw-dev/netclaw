# netclaw-session Delta Spec

## ADDED Requirements

### Requirement: Thread history context injection

The session actor SHALL accept backfill-flagged `SendUserMessage` commands
and assemble them into a read-only thread history context block. The context
block SHALL be inserted into the conversation history as a user-role message
before any live conversation turns. Backfilled messages SHALL include inline
multimodal content (images as `DataContent`) so the LLM receives them as
vision content. The context block SHALL be framed with delimiters indicating
it is prior thread history, not conversation the assistant participated in.

#### Scenario: Backfill messages assembled into context block

- **GIVEN** 5 `SendUserMessage` commands arrive with the backfill flag set
- **WHEN** the session actor processes them
- **THEN** the messages are assembled into a single thread history context
  block
- **AND** the block is framed with `[thread history]` / `[end thread history]`
  delimiters
- **AND** each message includes sender attribution and UTC timestamp

#### Scenario: Backfill context includes images

- **GIVEN** a backfill message includes `MediaReferences` for image files
- **WHEN** the context block is assembled for the LLM call
- **THEN** image content is loaded from the session media directory
- **AND** included as `DataContent` alongside the text in the context block

#### Scenario: Backfill context precedes live conversation

- **GIVEN** a session receives backfill messages followed by the triggering
  mention
- **WHEN** the first LLM turn is assembled
- **THEN** the thread history context block appears before the mention message
- **AND** the mention message appears as a normal user turn

#### Scenario: Compaction treats backfill content uniformly

- **GIVEN** a session has a large thread history context block plus ongoing
  conversation
- **WHEN** compaction is triggered
- **THEN** the compaction pipeline processes the thread history block the same
  as any other conversation content
- **AND** older backfilled content is compacted before recent live messages

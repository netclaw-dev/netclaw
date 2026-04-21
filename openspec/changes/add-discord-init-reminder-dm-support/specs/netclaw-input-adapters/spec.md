## ADDED Requirements

### Requirement: Discord DM reminder turn normalization

Reminder turns routed to Discord sessions SHALL be normalized through the
Discord input path into `SendUserMessage` with Discord source metadata and
deterministic entity identity. Reminder-origin turns SHALL preserve reminder
correlation metadata for idempotent handling.

#### Scenario: Discord current-session reminder turn produces normalized command

- **GIVEN** the reminder dispatcher sends a trusted session turn for a Discord DM session
- **WHEN** the Discord gateway routes the turn into the input pipeline
- **THEN** the resulting `SendUserMessage` has `MessageSource.ChannelType = Discord`
- **AND** the command carries reminder correlation metadata for deduplication

#### Scenario: Discord reminder turn reuses existing DM session entity key

- **GIVEN** a Discord DM session already exists for entity key `{channelId}/{threadIdOrMessageId}`
- **WHEN** a current-session reminder fires for that session
- **THEN** routing targets the existing entity key
- **AND** no transport-specific session actor type is introduced

# netclaw-input-adapters Delta Spec

## ADDED Requirements

### Requirement: Optional pre-session thread history fetch phase

Input adapters that support threaded conversations SHALL implement an optional
pre-session history fetch phase. When a new session is created from a mention
in an existing thread, the adapter SHALL fetch prior thread messages and
deliver them as backfill context before delivering the triggering message.
The `ChannelInput` contract SHALL carry an `IsBackfill` flag (default `false`)
to distinguish backfilled messages from live messages. Adapters that do not
support threads (e.g., timer adapter, TUI adapter) SHALL NOT implement
history fetch.

#### Scenario: Slack adapter fetches history before first message

- **GIVEN** an `app_mention` event arrives in an existing Slack thread
- **WHEN** the Slack adapter creates a new session for that thread
- **THEN** the adapter fetches prior thread messages before delivering the
  mention
- **AND** backfilled `ChannelInput` items have `IsBackfill = true`
- **AND** the triggering mention has `IsBackfill = false`

#### Scenario: Timer adapter does not implement history fetch

- **GIVEN** a timer fires for a scheduled task
- **WHEN** the timer adapter creates a `SendUserMessage` command
- **THEN** no thread history fetch is performed
- **AND** the `IsBackfill` flag is `false`

#### Scenario: Backfill flag propagated to SendUserMessage

- **GIVEN** a `ChannelInput` with `IsBackfill = true`
- **WHEN** `ChannelPipeline.MapToCommand` transforms it
- **THEN** the resulting `SendUserMessage` carries the backfill flag
- **AND** the session actor uses this flag to inject the message as read-only
  context

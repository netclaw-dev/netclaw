# netclaw-input-adapters Delta Spec

## ADDED Requirements

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

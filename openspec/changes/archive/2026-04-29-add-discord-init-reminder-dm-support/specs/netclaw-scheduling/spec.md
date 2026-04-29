## ADDED Requirements

### Requirement: Discord direct-message reminder delivery

The reminder subsystem SHALL support Discord direct-message delivery with the
same delivery contract used by other transports. For
`delivery.kind = channel`, `delivery.transport = "discord"` SHALL require a
canonical Discord DM destination produced by a resolver before persistence. For
`delivery.kind = current_session`, reminders created in Discord DM sessions
SHALL persist `originChannelType = Discord` and the originating session ID for
trusted re-entry routing.

#### Scenario: Channel-kind reminder persists canonical Discord DM target

- **GIVEN** a resolver for `transport = "discord"` is registered
- **WHEN** `set_reminder` is called with `delivery.kind = "channel"` and a Discord DM target alias
- **THEN** the persisted reminder stores only the resolver-canonical destination
- **AND** the raw LLM-supplied target text is not persisted

#### Scenario: Current-session reminder re-enters Discord DM session

- **GIVEN** a reminder was created from a Discord DM session with `delivery.kind = "current_session"`
- **WHEN** the reminder fires
- **THEN** delivery is routed through the Discord gateway's trusted-session path
- **AND** no new `schedule/{taskId}/{runTs}` session is created for that run

### Requirement: Required Discord delivery fails loud when not observed

When a reminder requires delivery and the origin or target transport is Discord, execution SHALL remain fail-loud.
If delivery is not observed within the configured completion window, the execution SHALL be marked failed and
operational failure signaling SHALL be emitted.

#### Scenario: Required Discord DM delivery observation timeout fails execution

- **GIVEN** a reminder execution requires delivery for a Discord destination
- **WHEN** completion output is produced but no delivery-observed signal arrives before timeout
- **THEN** the execution is recorded as failed
- **AND** an operational failure alert is emitted for retry/backoff handling

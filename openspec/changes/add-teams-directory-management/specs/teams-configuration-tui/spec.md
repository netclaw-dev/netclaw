## Purpose

Make Microsoft Teams a complete native Channels configuration experience while
keeping credentials masked, directory data bounded, and existing settings safe
to reopen and edit.

## ADDED Requirements

### Requirement: Teams is a first-class Channels adapter

The Channels picker SHALL list Teams after Mattermost and SHALL include Teams
in its configured summaries, enabled/disabled drafts, management actions, and
reset behavior. The first-connect flow SHALL collect tenant ID, application
client ID, bot ID, and client secret, then validate runtime configuration and
directory capability before saving a usable connection.

#### Scenario: Operator creates a Teams connection

- **GIVEN** Teams is not configured
- **WHEN** the operator selects Teams and completes the first-connect flow
- **THEN** the Channels TUI saves the non-secret fields in normal configuration
- **AND** saves the client secret only through the existing secret overlay
- **AND** opens Teams management without requiring the values again

### Requirement: Credentials are masked and blank-preserving

Teams client secrets SHALL be masked in all TUI summaries and never appear in
normal configuration, logs, doctor output, or diagnostics. A blank secret on
re-edit SHALL preserve the stored secret. Secret replacement SHALL require the
operator to use the explicit credential rotation action.

#### Scenario: Blank secret preserves a working connection

- **GIVEN** a Teams connection already has a stored client secret
- **WHEN** the operator edits non-secret fields and leaves the secret blank
- **THEN** the existing secret remains stored
- **AND** the connection is not cleared or rotated

### Requirement: Teams management exposes channels and principals safely

The Teams management home SHALL expose counts for configured channels, users,
groups, direct-message state, and directory capability. It SHALL provide
actions to add/remove channel access, manage users, manage groups, toggle DMs,
inspect directory status, rotate credentials, enable/disable, reset, and save.
Channel configuration SHALL use team then channel selection, then audience and
per-channel principals; it SHALL save canonical IDs only. User search SHALL
display a safe identity label and support display name, UPN, and mail search.
Group search SHALL identify supported security and Microsoft 365 groups.

#### Scenario: Operator adds a group to one channel

- **GIVEN** a Teams channel has been selected by canonical team and channel IDs
- **WHEN** the operator selects an allowed group from directory search
- **THEN** the structured channel override stores that group ID for that channel
- **AND** the TUI shows a safe friendly label when available

### Requirement: Directory search remains responsive and non-blocking

The Teams TUI SHALL debounce directory search by approximately 250–350ms,
cancel superseded requests, require a minimum query length, and display no more
than 50 results. It SHALL use asynchronous operations without blocking waits.
Directory status and doctor output SHALL report safe capability or consent
messages without IDs, tokens, or secret values.

#### Scenario: Directory status reports incomplete consent safely

- **GIVEN** Teams runtime credentials are valid but Graph directory consent is incomplete
- **WHEN** the operator opens directory status or runs doctor
- **THEN** the result explains that directory capability is unavailable or lacks required consent
- **AND** it contains no secret, token, or raw directory principal ID

### Requirement: Approval rendering never blocks on directory lookup

Teams approval rendering SHALL use only an already-cached directory label when
it is available. An approval callback SHALL perform no new directory request.
The terminal operator label SHALL prefer the callback-provided display name,
then a cached `Display Name <UPN>` or UPN, then `Authorized operator`; it SHALL
NOT display a canonical user ID.

#### Scenario: Approval callback has no cached user label

- **GIVEN** an authorized approval callback has no display name and no cached directory user
- **WHEN** Teams renders the terminal card
- **THEN** it uses `Authorized operator`
- **AND** no Graph request is made

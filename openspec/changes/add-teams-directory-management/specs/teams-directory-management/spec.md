## Purpose

Provide bounded, least-privilege Microsoft Graph directory discovery for Teams
administration without exposing tenant data, credentials, or SDK types outside
the Teams directory boundary.

## ADDED Requirements

### Requirement: Teams directory access uses the existing app identity

The Teams directory capability SHALL authenticate with the configured Teams
tenant ID, client ID, and client secret using app-only access and the
`https://graph.microsoft.com/.default` scope. It SHALL require only
`Team.ReadBasic.All`, `Channel.ReadBasic.All`, `User.Read.All`, and
`GroupMember.Read.All` application permissions and SHALL NOT require or
recommend `Directory.Read.All`. It SHALL NOT persist an access token or a
second secret.

#### Scenario: Missing Graph consent is reported safely

- **GIVEN** a complete Teams credential whose tenant lacks one required Graph permission
- **WHEN** the operator validates the Teams directory capability
- **THEN** the result identifies the unavailable directory capability without a token or secret
- **AND** the operator can correct consent and retry without replacing configuration

### Requirement: Directory results are canonical and bounded

The system SHALL expose only SDK-neutral team, channel, user, and group
directory records. Canonical IDs SHALL be authoritative configuration values;
friendly names, UPNs, and mail addresses are display/search data only. Search
queries SHALL have a minimum length and bounded input, use server-side
filtering/search and paging, and return no more than 50 results. The system
SHALL NOT enumerate a whole tenant directory for interactive configuration.

#### Scenario: Friendly user lookup persists only the object ID

- **GIVEN** an operator searches for a user by display name, UPN, or mail
- **WHEN** the directory returns a matching user
- **THEN** the UI displays human-readable identity fields
- **AND** the resulting access configuration stores only the user's canonical ID

#### Scenario: Short search is not sent to Graph

- **GIVEN** the directory search minimum length has not been met
- **WHEN** the operator enters a shorter query
- **THEN** no directory request is issued
- **AND** the UI asks for a longer query

### Requirement: Directory operations have finite resource limits

Every directory request SHALL honor caller cancellation and complete within a
bounded operation deadline of approximately five seconds. Retryable transient
failures MAY retry only within that deadline and SHALL honor a service-supplied
retry-after value. Directory cache-aside storage SHALL be size-bounded and
shall not contain a secret, token, or unbounded raw query. Profile and
membership results SHALL expire within ten minutes, team/channel/group records
within thirty minutes, and search results within five minutes.

#### Scenario: Superseded search is cancelled

- **GIVEN** an operator starts a directory search
- **WHEN** they change the query before its debounced request completes
- **THEN** the earlier request is cancelled or its result is discarded
- **AND** only results for the latest query can update the UI

#### Scenario: Retry exhaustion returns a safe unavailable result

- **GIVEN** a directory service returns retryable throttling responses beyond the bounded deadline
- **WHEN** the operation expires
- **THEN** it returns a safe unavailable result
- **AND** it does not retry indefinitely or expose response credentials

### Requirement: Team and channel selection remains recoverable

The directory capability SHALL support selecting a friendly team followed by a
friendly channel and SHALL save their canonical IDs. When a saved ID cannot be
resolved during a later display refresh, the UI SHALL show a safe abbreviated
ID and SHALL NOT delete or broaden the saved access configuration. An advanced
canonical-ID entry path SHALL remain available and validate its supplied IDs.

#### Scenario: Cached label is unavailable on reopen

- **GIVEN** a configured Teams channel has a canonical saved ID
- **AND** its friendly directory label is no longer available
- **WHEN** the operator reopens Teams configuration
- **THEN** the channel remains configured and renders a safe abbreviated ID
- **AND** no configuration entry is deleted

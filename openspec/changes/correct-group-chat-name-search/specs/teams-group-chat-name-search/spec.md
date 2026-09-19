## Purpose

Let an operator find a named Teams Group Chat directly from its visible title and add its canonical ID.
Keep discovery bounded, explicit about coverage, and separate from runtime access authority.

## ADDED Requirements

### Requirement: Group Chat entry searches chat titles

The Group Chat add action SHALL open a `Group Chat name` input without a participant selector.
The search SHALL match partial or full chat titles without case sensitivity.
The application SHALL NOT interpret that input as a user, group, or channel name.

#### Scenario: Partial chat title

- **GIVEN** a group chat has the title `BostonTech Operations`
- **WHEN** the operator searches for `bostontech`
- **THEN** the search can return that chat
- **AND** the application does not submit `bostontech` to user search

#### Scenario: Pasted full title

- **GIVEN** the operator has an active Group Chat search
- **WHEN** the operator pastes a different full chat title
- **THEN** the application invalidates prior results before selection
- **AND** Enter searches for the new chat title

### Requirement: Discovery coverage is independent of access grants

The search SHALL discover chat metadata through tenant users without a manually selected participant.
The application SHALL NOT limit discovery sources to configured access grants.
It SHALL deduplicate matches by canonical chat ID and exclude non-group chat types.
Discovery SHALL use the existing application credentials and metadata permissions without message access.

#### Scenario: Chat outside the current principal list

- **GIVEN** a matching named Group Chat contains no configured allowed user
- **WHEN** the search reaches a tenant member of that chat
- **THEN** the application can display the matching Group Chat
- **AND** the search does not add that member to any access list

#### Scenario: Meeting and duplicate records

- **GIVEN** two tenant users expose the same matching Group Chat and one matching meeting chat
- **WHEN** the search processes those records
- **THEN** it shows one Group Chat result
- **AND** it excludes the meeting chat

### Requirement: Search is bounded and reports its coverage

Each search action SHALL make a bounded number of metadata requests with finite timeouts.
The TUI SHALL offer `Continue search` when metadata remains.
It SHALL distinguish an incomplete search, unavailable source metadata, and a complete search with no matches.
It SHALL display errors for consent failures, rate limits, expired continuations, or resource bounds.

#### Scenario: Match after an empty batch

- **GIVEN** the first batch contains no matching chat and more metadata remains
- **WHEN** the batch completes
- **THEN** the TUI offers `Continue search` and identifies incomplete coverage
- **AND** a later batch can return the matching chat

#### Scenario: Unavailable source

- **GIVEN** a tenant user's chat metadata is unavailable
- **WHEN** the search reaches that user
- **THEN** the TUI reports incomplete coverage
- **AND** it does not claim that the requested chat is absent from the tenant

#### Scenario: Continuation from another query

- **GIVEN** a continuation belongs to one tenant and chat-title query
- **WHEN** a request reuses it with a different tenant or query
- **THEN** the application rejects the continuation without following its metadata URLs

### Requirement: Search results do not grant access

The application SHALL retain the existing canonical-ID validation, review, and save boundary.
Only a selected canonical Group Chat ID SHALL enter the configured destination list.
Chat titles, member metadata, discovery cursors, and tenant user IDs SHALL remain temporary presentation data.
The existing ingress switch, principal rules, and mention rules SHALL remain authoritative.
Slack Socket Mode and other adapters SHALL retain their existing behavior.

#### Scenario: Select a discovered Group Chat

- **GIVEN** a discovered result has a valid canonical Group Chat ID
- **WHEN** the operator selects and saves that result
- **THEN** the configuration stores the canonical ID
- **AND** discovery creates no principal grant or implicit ingress enablement

#### Scenario: Invalid discovered ID

- **GIVEN** a metadata record has a noncanonical Group Chat ID
- **WHEN** the search processes it
- **THEN** the application excludes it from selectable Group Chat results
- **AND** it writes no configuration value from that record

#### Scenario: Optional consent absent

- **GIVEN** the application lacks optional `Chat.ReadBasic.All` consent
- **WHEN** Group Chat discovery fails
- **THEN** the TUI identifies the failed metadata operation
- **AND** the advanced canonical-ID path and existing local management remain available

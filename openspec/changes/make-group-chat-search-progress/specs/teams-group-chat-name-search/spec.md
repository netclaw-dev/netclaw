## Purpose

Make Teams Group Chat title search advance automatically with visible progress and reusable results.
Keep each search run bounded and preserve the existing canonical-ID access boundary.

## ADDED Requirements

### Requirement: One search action advances across metadata batches

The TUI SHALL continue through available metadata batches without a separate action after each batch.
Each run SHALL have finite request and elapsed-time limits.
The TUI SHALL preserve matches from earlier batches and display cumulative request, chat-record, and completed-user counts.
It SHALL distinguish active search, paused search, complete search, partial coverage, and explicit failure.

#### Scenario: Match after several empty batches

- **GIVEN** a named Group Chat appears after three empty metadata batches
- **WHEN** the operator submits its partial or full title once
- **THEN** the search advances to that match without another key press
- **AND** the TUI retains that match when later batches arrive

#### Scenario: A user has several chat pages

- **GIVEN** a search receives another page for a user whose chat list is not complete
- **WHEN** the TUI receives that successful batch
- **THEN** request and chat-record counts reflect the additional work
- **AND** the completed-user count does not falsely increase

#### Scenario: Automatic run reaches its bound

- **GIVEN** more metadata remains after the automatic run reaches its request or time bound
- **WHEN** the run stops
- **THEN** the TUI offers `Resume search` and retains its matches
- **AND** it does not report a complete search with no matches

### Requirement: Metadata traversal permits progress across sources

The search SHALL visit other discovered users before it exhausts one user's chat pages.
Title discovery SHALL request metadata without participant expansion or message access.
The adapter SHALL reject repeated continuation pages, foreign metadata URLs, and exceeded resource bounds explicitly.
Search results SHALL remain deduplicated by canonical Group Chat ID.

#### Scenario: A later user has the desired chat

- **GIVEN** an early user has many chat pages and a later discovered user has a matching chat
- **WHEN** the search processes those users
- **THEN** it can reach the later user's first page before it exhausts the earlier user's chat pages

#### Scenario: The provider repeats a continuation page

- **GIVEN** a provider response repeats a page already consumed by the search
- **WHEN** the adapter processes that response
- **THEN** the search reports an explicit metadata failure
- **AND** it does not follow that page indefinitely or report complete coverage

### Requirement: Stop and resume preserve a successful checkpoint

The TUI SHALL offer `Stop search` and `Ctrl+S` during an active search.
An operator stop SHALL preserve earlier matches and the last successful continuation.
`Resume search` SHALL continue from that checkpoint when it remains valid.
Input edits or departure from the search SHALL cancel prior work and reject stale responses.

#### Scenario: Stop during a later batch

- **GIVEN** an earlier batch returned a match and a continuation
- **WHEN** the operator stops a later request and resumes the search
- **THEN** the earlier match remains selectable
- **AND** the resumed request uses the last successful continuation

#### Scenario: Old response arrives after a new query

- **GIVEN** the operator changes the chat title during a request
- **WHEN** that request returns after cancellation
- **THEN** its results do not replace the new query's results
- **AND** its continuation cannot advance the new query

### Requirement: Search progress does not change access authority

The existing Group Chat review and canonical-ID save path SHALL remain authoritative.
Discovery SHALL NOT add principal grants, enable ingress, or change mention rules.
The application SHALL retain explicit Graph failures and the advanced canonical-ID path.
Slack Socket Mode and other channel adapters SHALL retain their current behavior.

#### Scenario: Select a result during automatic search

- **GIVEN** an active search shows a valid Group Chat match
- **WHEN** the operator selects and applies that match
- **THEN** only its canonical ID enters the Group Chat destination list
- **AND** later search responses do not change that selection or grant access

#### Scenario: Consent or metadata request fails

- **GIVEN** Graph denies a request or returns invalid metadata
- **WHEN** the current search batch fails
- **THEN** the TUI identifies the failure instead of reporting no matches
- **AND** it preserves earlier matches and leaves configured access unchanged

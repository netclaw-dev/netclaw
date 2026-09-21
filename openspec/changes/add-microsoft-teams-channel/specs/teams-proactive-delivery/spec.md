## Purpose

Provide restart-safe Teams reminder delivery using previously authenticated
conversation destinations without persisting tokens or requesting Graph access.

## ADDED Requirements

### Requirement: Reminder creation authority is separate from Teams delivery capability

The Teams channel SHALL NOT treat a captured proactive destination or a passed
channel ACL as authority to create reminders. Generic tool policy SHALL decide
whether `set_reminder` is available from the resolved source audience. An
unmapped Teams channel SHALL remain Public and SHALL NOT expose scheduling by
default. An operator MAY map an independently approved canonical team/channel
identity to Team through the structured `Teams.ChannelAudienceOverrides`
array; that Team session MAY use the generic `current_session` reminder path.
Canonical identities containing configuration delimiters SHALL be represented
as structured values, not dictionary keys. Duplicate matching structured
overrides SHALL fail closed. The mapping SHALL NOT relax tenant, team, channel,
sender, mention, canonical-root, or destination validation.

#### Scenario: Unmapped allowed channel remains unable to schedule

- **GIVEN** a Teams channel root passes tenant, team, channel, sender, and mention ACL checks
- **AND** no explicit channel audience mapping applies
- **WHEN** the session requests `set_reminder`
- **THEN** the Public audience profile denies the tool before reminder creation
- **AND** no reminder or delivery state is created

#### Scenario: Exact approved Team channel uses current session

- **GIVEN** an independently approved canonical team/channel identity is mapped to Team
- **AND** the channel root passes every normal ingress and ACL check
- **WHEN** the generic `set_reminder` tool creates a `current_session` reminder
- **THEN** the reminder retains that canonical Teams root session
- **AND** delivery remains subject to the existing destination generation and fail-closed resolution rules

### Requirement: Teams reminder destinations are explicit and validated

The Teams channel SHALL resolve current-session delivery and explicit Teams
conversation, user, or channel targets only when a matching persisted,
tenant-bound destination is known. It SHALL reject unknown or ambiguous targets
without sending a message or performing Graph discovery.

#### Scenario: Known current-session reminder is delivered

- **WHEN** a reminder targets an active or recoverable Teams session
- **THEN** it is delivered through that session's Teams conversation

#### Scenario: Unknown user target is rejected

- **WHEN** a reminder targets a Teams user with no persisted approved personal destination
- **THEN** target resolution fails with an actionable error and no Graph lookup occurs

#### Scenario: A stale destination generation is rejected

- **GIVEN** a reminder reservation records a destination generation
- **WHEN** a later authenticated activity refreshes that destination
- **THEN** output for the old reservation does not post to the refreshed destination
- **AND** the result remains associated with the original delivery identity

### Requirement: Proactive generations and terminal outcomes are crash-safe

The binding SHALL assign generation one to its first accepted destination.
An unchanged accepted destination SHALL keep its generation; a changed accepted
destination SHALL durably advance it without rollover. Each delivery record
SHALL retain the generation at reservation time. Output for a stale generation
SHALL not post to the newer destination and SHALL only update the original
delivery record according to the stale-result policy.

A `FailedPermanent` result for the current generation SHALL record both the
terminal delivery state and destination invalidation in one durable event. A
permanent result for an older generation SHALL not invalidate a newer one.

#### Scenario: An invalid destination result commits atomically

- **GIVEN** a delivery is sending to the current destination generation
- **WHEN** Teams reports that destination invalid
- **THEN** one durable delivery event records `FailedPermanent` and invalidates
  that same generation
- **AND** recovery never exposes that generation as available

#### Scenario: A stale permanent result cannot revoke a refresh

- **GIVEN** a delivery belongs to generation N
- **AND** an accepted activity refreshes the binding to generation N+1
- **WHEN** the delivery records a permanent result for generation N
- **THEN** generation N+1 remains available after recovery

### Requirement: Proactive correlation and retention are bounded

The binding SHALL correlate reminder output by immutable delivery key, not by
a single mutable active-delivery field. A terminal or unknown key SHALL ignore
late output. It SHALL retain at most 1,024 delivery records, including terminal
records required to suppress duplicate sends. A new key at capacity SHALL fail
closed; an existing `Sent` key remains idempotently acknowledged. Recovery
SHALL reject a snapshot that exceeds the bound or references a generation newer
than its captured destination.

Snapshots SHALL retain the last destination generation even after invalidation,
and all bounded delivery records needed for delivery state and terminal duplicate
suppression. A later accepted capture SHALL advance from that retained generation.

#### Scenario: Concurrent completions remain isolated

- **GIVEN** two reminder deliveries are sending
- **WHEN** their outputs arrive in either order
- **THEN** each completion changes only its matching delivery record
- **AND** a late duplicate completion causes no post or additional transition

### Requirement: Proactive diagnostics use authoritative binding state

The Teams binding actor SHALL provide proactive diagnostics from its recovered
durable state. The diagnostics SHALL contain only health states, migration
states, bounded counts, and safe reason codes. They SHALL NOT contain Teams
identifiers, service URLs, content, credentials, headers, tokens, or provider
exception text.

#### Scenario: Destination data is not disclosed

- **GIVEN** a binding has a captured personal or channel destination
- **WHEN** an operator requests its proactive diagnostics
- **THEN** the response reports availability and bounded counts
- **AND** it does not include destination values

#### Scenario: Diagnostics expose only bounded delivery state

- **WHEN** a binding reports proactive diagnostics
- **THEN** it reports bounded destination, invalidation, pending, terminal,
  retryable, permanent, unknown, missing-target, and capacity state
- **AND** its ambiguous-target count is zero because one binding owns at most one
  current destination
- **AND** it does not report raw identifiers, content, credentials, request data,
  or provider exception text

### Requirement: Proactive delivery is independent of the inbound HTTP request

The authenticated `/api/messages` request SHALL translate its SDK activity into
Netclaw-owned immutable ingress data before actor routing. The binding SHALL
retain only validated durable destination state; it SHALL not retain an HTTP
context, request service scope, request cancellation token, SDK activity/context,
or request-bound client. Later delivery SHALL use the daemon application-level
Teams client and the persisted destination.

#### Scenario: A personal request completes before a reminder delivery

- **GIVEN** an authenticated allowed personal activity captures a destination
- **WHEN** the HTTP response and its request scope are disposed
- **THEN** a later generic reminder delivers to that persisted personal destination

#### Scenario: A channel-root request completes before a reminder delivery

- **GIVEN** an authenticated allowed channel-root activity captures a destination
- **WHEN** the HTTP response and its request scope are disposed
- **THEN** a later generic reminder replies to the captured canonical root
- **AND** it does not fall back to a top-level or different-root post

### Requirement: Legacy Teams proactive state migrates fail closed

The Teams channel SHALL decode historical Teams persistence manifests only for
recovery. It SHALL convert valid legacy state to Teams-owned v2 snapshots. A
failed migration snapshot SHALL leave migration recoverable on restart. It
SHALL NOT synthesize a destination from malformed or insufficient legacy state.

#### Scenario: A legacy recovery writes a retained v2 snapshot

- **GIVEN** a binding has valid legacy Teams journal or snapshot state
- **WHEN** the binding recovers
- **THEN** it writes a Teams-owned v2 snapshot before legacy journal compaction
- **AND** a sequence-1 snapshot remains available for the next restart

#### Scenario: Insufficient legacy destination state is rejected

- **GIVEN** a legacy Teams destination omits required identity or service URL data
- **WHEN** the binding recovers
- **THEN** recovery fails closed
- **AND** no destination is fabricated

### Requirement: Proactive delivery state is durable and classified

The Teams channel SHALL persist reminder delivery state before initiating an
outbound send. The state model is `Pending`, `Sending`, `Sent`,
`FailedRetryable`, `FailedPermanent`, and `DeliveryUnknown`. It SHALL classify
transient throttling and server failures separately from permanent credential,
uninstalled-app, authorization, malformed-target, and expired-destination
failures. It SHALL NOT claim exactly-once external delivery without tenant-backed
proof of a Teams server-enforced idempotency mechanism.

#### Scenario: Confirmed reminder execution is not resent

- **GIVEN** a reminder execution is durably recorded as `Sent`
- **WHEN** the same reminder execution is received again
- **THEN** no additional Teams send is attempted

#### Scenario: Retryable failure is retried using the same execution identity

- **GIVEN** a reminder execution is durably recorded as `FailedRetryable`
- **WHEN** retry policy permits another attempt
- **THEN** the retry uses the same reminder execution identity
- **AND** its state transition is persisted

#### Scenario: Delivery outcome is unknown after interruption

- **WHEN** outbound delivery was initiated but confirmation was not durably recorded before interruption
- **THEN** the execution is recorded as `DeliveryUnknown`
- **AND** the system does not falsely report confirmed delivery
- **AND** recovery follows an explicit retry-or-operator-review policy

#### Scenario: Confirmed permanent failure is not automatically retried

- **GIVEN** a reminder execution is durably recorded as `FailedPermanent`
- **WHEN** normal retry processing runs
- **THEN** no automatic Teams send is attempted

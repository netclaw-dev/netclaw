## Purpose

Define how Teams presents and protects a session-owned approval without turning
an Adaptive Card lifetime or callback into a separate authorization policy.

## ADDED Requirements

### Requirement: Teams card state is opaque transport state

Teams SHALL persist only the bounded correlation, nonce hash, optional prompt
locator, offered option keys, forwarding state, and presentation-recovery data
needed to bind and replay protect a card action. It SHALL not persist a raw
nonce, card JSON, raw tool arguments, token, SDK object, or policy/grant
decision. The session journal remains authoritative for whether an approval
call is pending and for its selected option semantics.

#### Scenario: Valid card action forwards an exact session option

- **GIVEN** a current card action with valid opaque binding and requester
- **WHEN** the requester selects an offered option
- **THEN** Teams forwards that exact option key to the shared session response path
- **AND** Teams does not transform the option or write a policy or grant

### Requirement: Card-token expiry reissues presentation without deciding approval

Teams card nonce expiry SHALL invalidate only that card callback. When the
session still has a pending approval, Teams SHALL produce a fresh bounded card
binding that can be presented deterministically. Expiry SHALL NOT send a
synthetic `Deny`, resolve the session wait, or execute the tool. A stale card
SHALL never authorize after reissue.

#### Scenario: Expired card leaves the session pending

- **GIVEN** a Teams approval card whose nonce has expired while its session call remains pending
- **WHEN** the requester submits the expired action
- **THEN** Teams returns an expired presentation result and issues a fresh valid card binding
- **AND** no `ToolInteractionResponse` is sent for the expired action
- **AND** the session remains paused pending an explicit decision

#### Scenario: Reissued card resolves exactly once

- **GIVEN** an expired card has been reissued for a still-pending session call
- **WHEN** the requester submits a valid option on the replacement card
- **THEN** the session receives the exact selected option once
- **AND** an explicit deny releases the session wait without tool execution
- **AND** duplicate old or new callbacks cannot execute the tool a second time

### Requirement: Card presentation failure is visible without changing session state

Teams SHALL report card delivery failure through normal delivery observability.
It SHALL leave the session-owned approval pending and SHALL not manufacture a
deny merely because a particular card presentation fails. It SHALL invalidate
the attempted opaque nonce binding, retain only bounded presentation-recovery
state, and create a fresh binding on a later recovery opportunity. It SHALL
not persist a raw nonce or tight-loop local delivery retries.

#### Scenario: Approval card delivery fails

- **GIVEN** a pending session approval and a failed Teams card delivery
- **WHEN** Teams records the transport result
- **THEN** the failure is observable through channel delivery telemetry or feedback
- **AND** the session approval remains pending until an explicit session decision

#### Scenario: Restart reissues a failed presentation safely

- **GIVEN** a card delivery failed after its nonce hash was persisted
- **WHEN** the Teams binding recovers
- **THEN** it invalidates the failed binding and presents a fresh bounded card
- **AND** the failed nonce cannot authorize an action
- **AND** the session has no synthetic denial or terminal consume record

#### Scenario: Successful unbound card remains valid

- **GIVEN** Teams accepts a card but returns no activity ID
- **WHEN** the binding records the successful delivery and later recovers
- **THEN** it treats the card as presented rather than as a delivery failure
- **AND** a valid callback still requires the nonce, sender, tenant, offered
  option, and expiry checks

### Requirement: Uncertain callback forwarding remains recoverable

Teams SHALL persist a bounded forwarding state for the exact validated selected
option before it sends that option to the shared approval-response flow. It
SHALL write terminal consumption only after the session acknowledges the option
or deterministically reports that the approval is no longer pending. A feedback
failure SHALL leave a usable retry path for the same option and SHALL NOT permit
a second option or a second tool execution.

#### Scenario: Feedback fails before the binding receives an acknowledgement

- **GIVEN** a valid Teams approval action and a session feedback round trip
  that fails before the binding receives a response
- **WHEN** Teams returns the action result
- **THEN** the card remains retryable for the exact selected option
- **AND** the core approval remains pending until the session resolves it
- **AND** no terminal Teams consume record is written solely because feedback
  failed

#### Scenario: Recovery re-drives an uncertain selection exactly once

- **GIVEN** a persisted Teams forwarding state after a lost session response
- **WHEN** the binding recovers
- **THEN** it re-drives the same selected option through the shared flow
- **AND** a session acknowledgement or stale-session response terminalizes the
  Teams transport state
- **AND** repeated callbacks cannot execute the selected tool a second time

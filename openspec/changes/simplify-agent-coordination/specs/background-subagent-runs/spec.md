## Purpose

This capability lets a parent accept, supervise, and receive child work across process restarts without repeating uncertain child effects.

## ADDED Requirements

### Requirement: Durable child acceptance precedes execution

The system SHALL validate a child request and its effective authority before it records acceptance. It SHALL record acceptance before it starts child work. `spawn_agent` and routed skill activation SHALL return the accepted run identity after that durable acceptance and before child completion.

The same accepted start identity SHALL NOT start a second child. A retry with the same identity and different payload SHALL fail as a conflict. A distinct authorized start SHALL remain distinct even when its task text matches another task. Task text SHALL NOT serve as run identity.

#### Scenario: Accepted start returns before child completion

- **GIVEN** a valid child request whose acceptance write succeeds
- **WHEN** the parent invokes `spawn_agent` or routed skill activation
- **THEN** the parent receives the accepted run identity before child completion
- **AND** the child starts only after acceptance persists

#### Scenario: Failed acceptance starts no child

- **GIVEN** a valid child request whose acceptance write fails
- **WHEN** the parent invokes the child
- **THEN** the request returns a failure
- **AND** no child action starts

#### Scenario: Repeated accepted identity does not start another child

- **GIVEN** a child start has durable acceptance
- **WHEN** the same start identity is received again
- **THEN** the system returns the existing accepted run
- **AND** it starts no second child

#### Scenario: Conflicting retry does not reuse an accepted identity

- **GIVEN** a child start identity already has durable acceptance
- **WHEN** the system receives that identity with a different payload
- **THEN** the system returns an explicit conflict
- **AND** it starts no child for the conflicting payload

### Requirement: Child execution keeps the parent's authority

The system SHALL preserve the original requester, audience, and authority boundary for child tools, approvals, status, and cancellation. A child result SHALL NOT grant authority. A child worker SHALL remain ephemeral and SHALL NOT restart an uncertain external action after owner recovery. Status SHALL distinguish durable acceptance from terminal completion. The run owner's status response SHALL include the pending approval count. Other requesters SHALL NOT see that count. An approval for a stale or unrelated run, call, requester, or attempt SHALL grant no authority.

The system SHALL persist cancellation intent before it acknowledges the intent. That acknowledgement SHALL NOT mean that the run is complete. The system SHALL report terminal cancellation only after the local dispatch confirms closure, and it SHALL start no new dispatch after closure. An uncertain external effect SHALL remain explicit.

#### Scenario: Child approval keeps the original requester

- **GIVEN** a child tool requires human approval
- **WHEN** the parent or an authorized requester resolves that approval
- **THEN** the decision applies only to that child call and its recorded requester
- **AND** a stale or unrelated decision grants no authority

#### Scenario: Recovery does not repeat an uncertain child action

- **GIVEN** an accepted child has an external action with no recorded terminal result
- **WHEN** the parent owner recovers
- **THEN** the system does not start the child again automatically
- **AND** the run reports an explicit non-success or uncertain state

#### Scenario: Status enforces run ownership

- **GIVEN** one requester owns a child run
- **WHEN** another requester asks for its status
- **THEN** the system denies that status request
- **AND** the owner can observe recorded progress and pending approval facts

#### Scenario: Accepted status is not terminal status

- **GIVEN** a child run has durable acceptance but no terminal result
- **WHEN** its owner requests status
- **THEN** the system reports a nonterminal state
- **AND** it does not report child completion

#### Scenario: Pending approval count is owner-only

- **GIVEN** an owned child run has one pending approval
- **WHEN** its owner requests status
- **THEN** the response includes a pending approval count of one
- **WHEN** that approval settles and the owner requests status again
- **THEN** the response includes a pending approval count of zero
- **AND** a non-owner cannot observe that count

#### Scenario: Stale approval does not grant authority

- **GIVEN** an approval refers to a stale run, call, requester, or attempt
- **WHEN** the system receives that approval
- **THEN** it rejects the approval as stale
- **AND** it dispatches no action under that approval

#### Scenario: Cancellation reports closure after local dispatch stops

- **GIVEN** an owner requests cancellation of an active child run
- **WHEN** the system persists cancellation intent
- **THEN** it acknowledges the intent without reporting terminal cancellation
- **WHEN** the local dispatch confirms closure
- **THEN** the system can report terminal cancellation
- **AND** it starts no new child dispatch after closure
- **AND** an uncertain external effect remains explicit

### Requirement: Ready results resume the parent at a safe boundary

The system SHALL save a child's raw terminal result before asynchronous preparation. A preparation failure SHALL leave that result available for retry. The system SHALL make a result eligible for delivery when preparation completes.

The parent SHALL select a ready child result without ordering it by child completion time. It SHALL deliver that result at the next safe model boundary. It SHALL NOT interrupt an active model call or incomplete tool batch. Ordinary user inputs SHALL retain their existing ordered batch.

The parent SHALL record result consumption with the correlated parent decision. It SHALL keep an unconsumed result available after recovery. Delivery SHALL use the canonical assistant/tool result pair with a fresh delivery call ID that differs from the initial acceptance call ID. Consumption SHALL retain the child's original authority for downstream tools and approval. The system SHALL NOT fabricate an execution receipt or successful result when no such result exists.

#### Scenario: A prepared result passes an earlier unready result

- **GIVEN** child A finishes first but its result is not ready
- **AND** child B's result is ready
- **WHEN** the parent reaches a safe model boundary
- **THEN** child B's result is eligible for delivery first
- **AND** the order does not depend on which child finished first

#### Scenario: A result waits for an active tool batch

- **GIVEN** a child result becomes ready while the parent has an incomplete tool batch
- **WHEN** the result becomes eligible for delivery
- **THEN** it does not interrupt that batch
- **AND** the parent can consume it at a later safe boundary

#### Scenario: Ordinary inputs keep their order

- **GIVEN** ordinary inputs A and B form an ordered parent batch
- **WHEN** a child result becomes ready
- **THEN** the child result does not reorder or replace inputs A and B

#### Scenario: Preparation failure keeps the raw result

- **GIVEN** a child has saved a terminal result
- **WHEN** result preparation fails
- **THEN** the raw result remains available
- **AND** the system can retry preparation without repeating child work

#### Scenario: Recovery keeps an unconsumed result

- **GIVEN** a child result is ready but no parent decision consumed it
- **WHEN** the parent recovers
- **THEN** the same result remains eligible for delivery
- **AND** the system does not repeat child execution

#### Scenario: Result delivery uses a new call identity

- **GIVEN** the parent accepted a child with an initial call identity
- **WHEN** the parent receives the child's terminal result
- **THEN** the parent receives a canonical assistant/tool result pair
- **AND** the delivery call ID differs from the initial acceptance call ID

#### Scenario: Consumption keeps original downstream authority

- **GIVEN** the parent consumes a child result
- **WHEN** the parent acts on that result or requests approval
- **THEN** downstream tools and approval use the original requester and authority boundary
- **AND** child result content grants no new authority

#### Scenario: Missing execution result stays explicit

- **GIVEN** the journal has no actual terminal execution result
- **WHEN** the parent prepares a child result for delivery
- **THEN** the system reports an explicit non-success or uncertain state
- **AND** it does not fabricate an execution receipt

### Requirement: Deployed child journal records remain recoverable

Before the system writes a new child journal format, it SHALL provide a tested read or migration path for deployed `tta-v1`, `cra-v1`, `cre-v1`, and extended existing records.

The path SHALL preserve accepted identities, terminal results, authority, pending consent, job lineage, and exactly-once transfer of delivered child results. It SHALL NOT execute an uncertain input or infer duplicate identity from matching task text. It SHALL preserve the original journal and a restore path until conversion succeeds.

#### Scenario: Valid deployed records migrate without lost facts

- **GIVEN** a valid deployed journal with old child and extended session records
- **WHEN** the system reads or migrates that journal
- **THEN** accepted identities, terminal results, authority, consent, lineage, and result transfer remain intact
- **AND** the converted journal replays with the candidate reader

#### Scenario: Invalid adoption receives a safe disposition

- **GIVEN** the reported journal contains an invalid task-adoption event
- **WHEN** the system migrates or recovers that journal
- **THEN** it does not execute the uncertain input
- **AND** it records an explicit safe disposition without comparing prompt text as identity
- **AND** it preserves the original journal and restore path

#### Scenario: New writes wait for compatibility proof

- **GIVEN** deployed child records lack a tested read or migration path
- **WHEN** the candidate starts a session with those records
- **THEN** it does not write the new child journal format to that session
- **AND** it reports the compatibility failure clearly

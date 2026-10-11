## ADDED Requirements

### Requirement: Active background jobs defer idle session passivation

Before idle passivation, the session SHALL check its actor-local background-job state. A shell job SHALL count as active only while it lacks a reap timestamp. A reaped job record SHALL NOT block idle passivation. A completed job SHALL release the idle-passivation guard when the session removes its active record. This rule applies to idle passivation only. It does not block explicit shutdown or restart passivation.

#### Scenario: Active shell job defers idle passivation

- **GIVEN** a session has a shell background job without a reap timestamp
- **AND** the session is in phase `Ready`
- **WHEN** the idle timeout fires
- **THEN** the session remains active
- **AND** it does not start idle passivation

#### Scenario: Reaped shell job record does not defer idle passivation

- **GIVEN** a session has a shell background job record with a reap timestamp
- **AND** the session is in phase `Ready`
- **WHEN** the idle timeout fires
- **THEN** the job record does not block idle passivation

#### Scenario: Job completion releases the idle-passivation guard

- **GIVEN** a session has an active shell background job
- **WHEN** the session processes the job result and removes its active record
- **THEN** the job no longer blocks idle passivation

## MODIFIED Requirements

### Requirement: Background jobs are reaped on session passivation

When a session enters passivation, it SHALL request that the background job
manager kill all running or pending jobs owned by that session, and SHALL wait
for the manager's acknowledgement (bounded by a short timeout) before taking
its final snapshot. The manager SHALL kill each owned job's entire process
tree and mark the job definitions with a distinct `Reaped` status,
distinguishable from agent- or user-initiated cancellation. Reaping SHALL NOT
produce a `DeliverTrustedSessionTurn` — delivering a turn would rehydrate the
session being torn down. If the acknowledgement times out, the session SHALL
log the failure loudly and proceed with passivation; the manager's kill
operation SHALL be idempotent. Active jobs SHALL defer idle-driven passivation.
Explicit shutdown and restart passivation SHALL still reap active jobs.

#### Scenario: Running jobs are reaped at explicit shutdown or restart

- **GIVEN** a daemon shutdown or restart requires session passivation while jobs run
- **WHEN** the session enters passivation
- **THEN** the manager kills the process trees of all jobs owned by that
  session
- **AND** the job definitions are marked `Reaped`
- **AND** no termination turn is delivered to the session
- **AND** the session's final snapshot records the jobs as reaped

#### Scenario: Reap handshake completes before final snapshot

- **GIVEN** a session entering passivation with running jobs
- **WHEN** the session requests the reap
- **THEN** the session waits for the manager's acknowledgement before taking
  the final snapshot

#### Scenario: Reap acknowledgement timeout fails loud and proceeds

- **GIVEN** the manager does not acknowledge the reap request within the
  handshake timeout
- **WHEN** the timeout elapses
- **THEN** the session logs the failure explicitly
- **AND** passivation proceeds
- **AND** a subsequent retry or daemon shutdown still terminates the process
  (kill is idempotent; processes do not outlive the daemon)

#### Scenario: Completion racing passivation is not double-processed

- **GIVEN** a job completes at the same time its owning session passivates
- **WHEN** both the completion delivery and the reap occur
- **THEN** job-ID deduplication ensures the session processes at most one
  terminal outcome for the job

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

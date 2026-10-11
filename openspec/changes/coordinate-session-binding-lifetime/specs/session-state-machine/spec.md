## REMOVED Requirements

### Requirement: Passivating behavior

**Reason**: The old requirement made subscriber count control idle passivation. The session now uses active-work state. Channel bindings stop only after committed session deactivation.

**Migration**: Use `Idle passivation follows active work` for idle eligibility. Keep the existing passivation sequence and abort window.

## ADDED Requirements

### Requirement: Idle passivation follows active work

The session actor SHALL enter `Passivating` when its idle timeout fires in phase `Ready` and its active-work check returns false. The default idle timeout SHALL be one hour. Subscriber count and journaled approval state SHALL NOT change idle eligibility. The `background-job-execution` capability defines the active-job check. In `Passivating`, the actor SHALL request final memory distillation from the observer actor, if present, wait up to five seconds, save a snapshot, notify the lifecycle observer, and stop itself. Idle passivation SHALL retain the post-snapshot grace window. The actor SHALL emit `SessionDeactivated` only when it commits to stop.

#### Scenario: Session passivates with no active work and a live subscriber

- **GIVEN** the session is in phase `Ready`
- **AND** a live channel subscriber is attached
- **AND** a journaled approval is outstanding
- **AND** no active work remains
- **WHEN** the one-hour idle timeout fires
- **THEN** the session enters `Passivating`
- **AND** it requests final memory distillation from the observer, if present

#### Scenario: Processing phase disables the idle timeout

- **GIVEN** the session is processing a foreground turn
- **WHEN** the idle period would elapse while the foreground turn remains active
- **THEN** the session remains in `Processing`
- **AND** it does not emit `SessionDeactivated`

#### Scenario: Passivation completes after distillation

- **GIVEN** the session is in phase `Passivating`
- **WHEN** `SessionDistillationCompleted` arrives from the observer
- **THEN** the actor saves a snapshot
- **AND** notifies the lifecycle observer of deactivation
- **AND** emits `SessionDeactivated` once
- **AND** stops itself

#### Scenario: Passivation completes on timeout

- **GIVEN** the session is in phase `Passivating`
- **WHEN** five seconds elapse without `SessionDistillationCompleted`
- **THEN** the actor saves a snapshot and stops itself
- **AND** it does not wait indefinitely for the observer
- **AND** it emits `SessionDeactivated` once

#### Scenario: Passivation without an observer actor

- **GIVEN** the session has no observer actor
- **AND** no active work remains
- **WHEN** the idle timeout fires
- **THEN** the actor saves a snapshot and stops itself
- **AND** it does not request final distillation
- **AND** it emits `SessionDeactivated` once

#### Scenario: User input aborts passivation before commit

- **GIVEN** the session is in the post-snapshot grace window
- **WHEN** the session receives `SendUserMessage` during the idle grace window
- **THEN** the actor returns to `Ready`
- **AND** it does not emit `SessionDeactivated`
- **AND** it handles the message

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

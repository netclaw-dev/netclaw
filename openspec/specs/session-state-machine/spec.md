## ADDED Requirements

### Requirement: Explicit session phase lifecycle

The session actor SHALL maintain an explicit `SessionPhase` enum tracking its
current lifecycle phase. Legal phases are `Recovering`, `Ready`, `Processing`,
`Compacting`, and `Passivating`. All phase transitions SHALL go through a
`TransitionTo(SessionPhase)` method that validates the transition is legal and
throws `InvalidOperationException` for illegal transitions.

#### Scenario: Legal transition from Ready to Processing

- **GIVEN** the session actor is in phase `Ready`
- **WHEN** a `SendUserMessage` is accepted
- **THEN** the actor transitions to phase `Processing`
- **AND** the `_currentPhase` field reflects `Processing`

#### Scenario: Legal transition from Processing to Compacting

- **GIVEN** the session actor is in phase `Processing`
- **WHEN** compaction threshold is reached after an LLM response
- **THEN** the actor transitions to phase `Compacting`

#### Scenario: Legal transition from Processing to Ready

- **GIVEN** the session actor is in phase `Processing`
- **WHEN** the turn completes with no compaction needed and no buffered messages
- **THEN** the actor transitions to phase `Ready`

#### Scenario: Illegal transition throws InvalidOperationException

- **GIVEN** the session actor is in phase `Compacting`
- **WHEN** code attempts `TransitionTo(Passivating)`
- **THEN** an `InvalidOperationException` is thrown
- **AND** the phase remains `Compacting`

### Requirement: Legal phase transition rules

The system SHALL enforce the following transition rules:

- `Recovering → Ready`
- `Ready → Processing, Compacting, Passivating`
- `Processing → Ready, Compacting`
- `Compacting → Ready, Processing`
- `Passivating` is terminal (no transitions out)

Any transition not in this set SHALL throw `InvalidOperationException`.

#### Scenario: Passivating is terminal

- **GIVEN** the session actor is in phase `Passivating`
- **WHEN** any `TransitionTo()` call is attempted
- **THEN** an `InvalidOperationException` is thrown

#### Scenario: Recovering only transitions to Ready

- **GIVEN** the session actor is in phase `Recovering`
- **WHEN** `TransitionTo(Processing)` is attempted
- **THEN** an `InvalidOperationException` is thrown

### Requirement: Passivating behavior

The session actor SHALL enter `Passivating` phase when idle timeout fires and no
subscribers are active. In `Passivating`, the actor SHALL buffer incoming
`SendUserMessage` commands, request final memory distillation from the observer
actor (if present), wait up to 5 seconds for completion, save a snapshot, notify
the lifecycle observer, and stop itself.

#### Scenario: Idle timeout triggers passivation with no subscribers

- **GIVEN** the session actor is in phase `Ready` with `_subscribers.Count == 0`
- **WHEN** `ReceiveTimeout` fires
- **THEN** the actor transitions to phase `Passivating`
- **AND** sends `RequestFinalDistillation` to the observer actor (if present)

#### Scenario: Idle timeout deferred when subscribers active

- **GIVEN** the session actor is in phase `Ready` with `_subscribers.Count > 0`
- **WHEN** `ReceiveTimeout` fires
- **THEN** the actor remains in phase `Ready`
- **AND** does NOT transition to `Passivating`

#### Scenario: Passivation completes after distillation

- **GIVEN** the session actor is in phase `Passivating`
- **WHEN** `SessionDistillationCompleted` is received from the observer
- **THEN** the actor saves a snapshot
- **AND** notifies `ISessionLifecycleObserver.OnSessionDeactivated()`
- **AND** stops itself via `Context.Stop(Self)`

#### Scenario: Passivation completes on timeout

- **GIVEN** the session actor is in phase `Passivating`
- **WHEN** 5 seconds elapse without `SessionDistillationCompleted`
- **THEN** the actor saves a snapshot and stops itself
- **AND** does NOT wait indefinitely for the observer

#### Scenario: Passivation without observer actor

- **GIVEN** the session actor has no observer actor (no memory store configured)
- **WHEN** idle timeout triggers passivation
- **THEN** the actor saves a snapshot and stops itself immediately
- **AND** does NOT attempt to send `RequestFinalDistillation`

#### Scenario: Messages buffered during passivation

- **GIVEN** the session actor is in phase `Passivating`
- **WHEN** a `SendUserMessage` arrives
- **THEN** the message is buffered
- **AND** the buffered message is available for processing after rehydration

### Requirement: Phase transition logging and observability

Each phase transition SHALL be logged at Info level with the source and target
phases. The observer actor SHALL be notified of phase changes via
`SessionPhaseChanged` messages so it can react (e.g., trigger distillation on
`Passivating`).

#### Scenario: Phase transition logged

- **GIVEN** the session actor transitions from `Ready` to `Processing`
- **WHEN** the transition completes
- **THEN** an Info log entry is emitted: `"session_phase_transition from=Ready to=Processing"`

#### Scenario: Observer notified of phase change

- **GIVEN** the session actor has an observer actor
- **WHEN** the actor transitions to `Passivating`
- **THEN** the observer actor receives a `SessionPhaseChanged(Passivating)` message

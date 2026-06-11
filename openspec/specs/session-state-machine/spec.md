# session-state-machine Specification

## Purpose

The session-state-machine capability defines the explicit lifecycle of a Netclaw
session actor. Each session tracks a single `SessionPhase` and moves between
phases only through validated transitions, so that turn processing, context
compaction, idle passivation, coordinated restart-drain, and outstanding tool
approval prompts all interact through one well-defined state machine rather than
ad-hoc flags. The state machine guarantees that illegal transitions fail loudly,
that every transition is observable, and that tool-interaction responses are
never silently dropped because the session moved out of `Processing` while an
approval prompt was outstanding.
## Requirements
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
- `Passivating → Ready`

Any transition not in this set SHALL throw `InvalidOperationException`.

#### Scenario: Passivating may abort back to Ready

- **GIVEN** the session actor is in phase `Passivating`
- **WHEN** a racing message aborts shutdown before the final stop
- **THEN** `TransitionTo(Ready)` is allowed

#### Scenario: Recovering only transitions to Ready

- **GIVEN** the session actor is in phase `Recovering`
- **WHEN** `TransitionTo(Processing)` is attempted
- **THEN** an `InvalidOperationException` is thrown

### Requirement: Passivating behavior

The session actor SHALL enter `Passivating` phase when idle timeout fires and no
subscribers are active. In `Passivating`, the actor SHALL request final memory
distillation from the observer actor (if present), wait up to 5 seconds for
completion, save a snapshot, notify the lifecycle observer, and stop itself.
Idle-driven passivation SHALL include a short post-snapshot grace window where a
racing inbound message can abort the stop and return the actor to `Ready`
instead of forcing a full stop and recovery cycle.

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
- **THEN** idle passivation is aborted
- **AND** the actor transitions `Passivating → Ready`
- **AND** the message is handled immediately

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

### Requirement: Tool interaction response accepted in all session phases

The session actor SHALL accept a `ToolInteractionResponse` in the `Ready`,
`Passivating`, and `Compacting` phases, not only in `Processing`. A response
SHALL NOT be left unhandled (dead-lettered) because the session moved out of
`Processing` while an approval prompt was outstanding. Handling SHALL preserve
the legal phase-transition rules: re-driving a tool batch transitions
`Ready → Processing`, and aborting passivation transitions `Passivating → Ready`.

#### Scenario: Response in Ready re-drives the tool batch

- **GIVEN** the session actor is in phase `Ready` with a restored pending tool
  interaction (e.g. after cold recovery)
- **WHEN** a `ToolInteractionResponse` for that call arrives
- **THEN** the actor SHALL transition `Ready → Processing`
- **AND** re-drive the parked tool batch and continue the turn

#### Scenario: Response in Passivating aborts passivation then is handled

- **GIVEN** the session actor is in phase `Passivating`
- **WHEN** a `ToolInteractionResponse` for that call arrives before the actor stops
- **THEN** the actor SHALL abort passivation, cancel its passivation timers,
  and transition `Passivating → Ready`
- **AND** then handle the response normally
- **AND** if the call is still pending the turn resumes from that state
- **AND** if the call is expired the actor emits the visible expired-prompt notice

### Requirement: Restart-drain passivation is non-interruptible

When a coordinated daemon restart requests session drain, the actor SHALL reject
new work, allow the current in-flight turn or compaction to finish, and then
enter `Passivating`. Once restart-drain mode reaches `Passivating`, shutdown is
non-interruptible: inbound user messages and tool-interaction responses SHALL
NOT abort passivation.

#### Scenario: Restart drain finishes current turn before passivating

- **GIVEN** the session actor is in `Processing` or `Compacting`
- **WHEN** restart drain is requested
- **THEN** the actor rejects new work
- **AND** allows the current in-flight work to finish
- **AND** only then transitions to `Passivating`

#### Scenario: Restart-drain passivation rejects racing inbound work

- **GIVEN** the session actor is in `Passivating` because restart drain is active
- **WHEN** a `SendUserMessage` or `ToolInteractionResponse` arrives
- **THEN** the actor does NOT abort passivation
- **AND** the shutdown continues to completion

#### Scenario: Response in Compacting is buffered and replayed

- **GIVEN** the session actor is in phase `Compacting`
- **WHEN** a `ToolInteractionResponse` arrives
- **THEN** the actor SHALL buffer the response rather than re-driving mid-compaction
- **AND** SHALL replay the buffered response to itself after compaction completes

#### Scenario: Unknown call id does not transition phase

- **GIVEN** the session actor is in phase `Ready` with no matching pending
  interaction and no reconstructable call in history
- **WHEN** a `ToolInteractionResponse` arrives
- **THEN** the actor SHALL remain in phase `Ready`
- **AND** SHALL emit a user-visible "approval prompt expired" message

### Requirement: Approval turn state is explicit

The session actor SHALL maintain explicit approval turn state for approval-paused work, separate from the coarse `SessionPhase` lifecycle. The approval turn state SHALL identify whether there is no active approval turn, a running turn, a turn waiting for one or more approval responses, a recovered waiting turn, a redrive in progress, or an abandoned approval turn being healed. Approval response handling SHALL consult this approval turn state instead of inferring the turn solely from nullable transport metadata or scattered pending dictionaries.

#### Scenario: Live approval request records waiting state

- **GIVEN** a session is processing a turn with a valid turn context
- **WHEN** a tool call emits an approval request
- **THEN** the session records approval turn state for the original turn context
- **AND** the waiting state includes the pending approval call id

#### Scenario: Recovered approval records recovered waiting state

- **GIVEN** a session recovers journaled pending approval state
- **WHEN** recovery completes
- **THEN** the session records recovered approval turn state with the restored turn context
- **AND** later approval responses are handled against that state

#### Scenario: Abandoned approval state heals the transcript

- **GIVEN** a recovered approval turn is waiting for a user response
- **WHEN** the user sends a new message instead of answering the approval prompt
- **THEN** the session transitions the approval turn state to abandoned
- **AND** the unanswered assistant tool calls are closed with synthetic tool results before the new turn is processed

### Requirement: Approval redrive uses actor-owned state transitions

Approval response handling SHALL transition through actor-owned approval states when redriving a parked tool batch. A redrive SHALL use the restored turn context attached to the approval turn state, transition the coarse `SessionPhase` through legal transitions, and clear approval turn state only after the parked batch and continuation path have completed or been abandoned.

#### Scenario: Ready approval response enters redrive state

- **GIVEN** a recovered session is in `Ready` with approval turn state waiting for call `call-1`
- **WHEN** a valid `ToolInteractionResponse` for `call-1` arrives
- **THEN** the session transitions to a redrive approval state
- **AND** the coarse phase transitions from `Ready` to `Processing`

#### Scenario: Redrive completion clears approval state

- **GIVEN** a session is redriving a recovered approval turn
- **WHEN** the parked tool batch completes and the continuation turn finishes
- **THEN** the session clears the approval turn state
- **AND** no pending or resolved approval state remains for the completed call ids


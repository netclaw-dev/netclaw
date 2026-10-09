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

The session actor SHALL enter `Passivating` after idle timeout only when no subscriber, live child, or unadmitted child result requires it.
A live child includes accepted, running, cancelling, and terminal-acknowledgement states that still need their owner.
An admitted pending result SHALL remain protected through the ordinary pending-input lifecycle.
After those obligations end, normal idle passivation SHALL resume.

In `Passivating`, the actor SHALL request final memory distillation from the observer actor when present.
It SHALL wait up to five seconds, save a snapshot, notify the lifecycle observer, and stop itself.
Idle passivation SHALL retain the post-snapshot grace window that lets racing input abort the stop and return to `Ready`.
Child ownership SHALL NOT alter shell-job reap semantics.

#### Scenario: Idle timeout triggers passivation with no subscribers

- **GIVEN** the session is `Ready` with no subscriber or remaining child obligation
- **WHEN** `ReceiveTimeout` fires
- **THEN** it transitions to `Passivating`
- **AND** it requests final distillation when an observer exists

#### Scenario: Idle timeout deferred when subscribers active

- **GIVEN** the session is `Ready` with active subscribers
- **WHEN** `ReceiveTimeout` fires
- **THEN** it remains `Ready`
- **AND** it does not start idle passivation

#### Scenario: Passivation completes after distillation

- **GIVEN** the session is `Passivating`
- **WHEN** final distillation completes
- **THEN** it saves a snapshot and notifies the lifecycle observer
- **AND** it stops itself

#### Scenario: Passivation completes on timeout

- **GIVEN** the session is `Passivating`
- **WHEN** five seconds pass without final distillation completion
- **THEN** it saves a snapshot and stops
- **AND** it does not wait indefinitely for the observer

#### Scenario: Passivation without observer actor

- **GIVEN** the session has no memory observer
- **WHEN** eligible idle passivation starts
- **THEN** it saves a snapshot and stops through the existing grace contract
- **AND** it requests no final distillation

#### Scenario: Messages buffered during passivation

- **GIVEN** idle passivation remains inside its interruptible grace window
- **WHEN** a user message arrives
- **THEN** the session aborts that idle stop and returns to `Ready`
- **AND** it handles the message under normal input rules

#### Scenario: Live child prevents idle owner loss

- **GIVEN** the session has no subscribers but owns a live background child
- **WHEN** idle timeout fires
- **THEN** the owner remains available for terminal receipt, prompts, status, and cancellation
- **AND** it cannot stop merely because the original start turn ended

#### Scenario: Undelivered result prevents idle loss

- **GIVEN** a terminal child receipt is durable but continuation admission remains pending
- **WHEN** idle timeout fires
- **THEN** the owner retains its delivery obligation
- **AND** normal idle passivation resumes after admission and ordinary pending-input settlement

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

When coordinated restart requests drain, the actor SHALL reject new work and complete its current parent turn or compaction.
It SHALL also cancel accepted live children through the run cancellation contract and bounded framework-only finalization.
It SHALL retain child terminal and prompt-settlement records before passivation whenever the owner remains operational.
Abrupt owner loss SHALL use the explicit recovery-loss contract instead.
Once restart-drain reaches `Passivating`, new user messages and approval prompt answers SHALL NOT abort shutdown.

#### Scenario: Restart drain finishes current turn before passivating

- **GIVEN** the parent is `Processing` or `Compacting`
- **WHEN** coordinated restart requests drain
- **THEN** it rejects new work and completes its current parent phase
- **AND** accepted children settle through cancellation rather than an unbounded lifetime wait

#### Scenario: Restart-drain passivation rejects racing inbound work

- **GIVEN** restart-drain reached `Passivating`
- **WHEN** new user input or an approval prompt answer arrives
- **THEN** it does not abort shutdown
- **AND** a settled child prompt cannot resume task work

#### Scenario: Response in Compacting is buffered and replayed

- **GIVEN** an ordinary live approval response arrives during `Compacting`
- **WHEN** the response is admitted under the existing run or parent prompt authority
- **THEN** its safe response transition respects the existing compaction boundary
- **AND** it does not redispatch a parent batch or cancelled child mid-compaction

#### Scenario: Unknown call id does not transition phase

- **GIVEN** the session has no live or reconstructable prompt for a call ID
- **WHEN** an answer arrives
- **THEN** the actor retains its phase
- **AND** it reports an expired approval prompt without tool execution

#### Scenario: Coordinated restart settles a live child

- **GIVEN** an accepted child still runs when drain begins
- **WHEN** the owner closes dispatch and finalization reaches its bounded terminal result
- **THEN** that cancelled result and prompt disposition remain durable
- **AND** recovery cannot automatically relaunch the child

#### Scenario: Coordinated restart settles an unanswered child approval prompt

- **GIVEN** a real accepted child waits for its original requester's unanswered approval prompt
- **WHEN** coordinated drain cancels the child and commits its terminal receipt
- **THEN** the final snapshot retains that run's `Cancelled` outcome and exact prompt correlation
- **AND** the prompt resolution is durable `Denied` before the terminal receipt
- **AND** no child retry or reusable authorization grant results

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


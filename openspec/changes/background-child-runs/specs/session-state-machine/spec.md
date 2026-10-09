## MODIFIED Requirements

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

## ADDED Requirements

### Requirement: Session input progress has a durable boundary

The session SHALL keep an admitted ordinary input pending until accepted durable progress consumes it or an explicit closure event closes it. `ToolBatchStarted` SHALL consume the input into durable turn history. `TurnRecorded` SHALL complete the turn. `InputClosed` SHALL close an input explicitly.

The session SHALL validate state transitions before it appends their events. A rejected transition SHALL not add an invalid event to the journal. Child results SHALL use the child-result contract and SHALL NOT enter ordinary user-input admission.

#### Scenario: Tool batch start consumes its pending input

- **GIVEN** an ordinary input remains pending in the durable session state
- **WHEN** `ToolBatchStarted` records accepted turn progress
- **THEN** the input leaves the pending queue
- **AND** its content remains in durable turn history

#### Scenario: Completed turn records its terminal event

- **GIVEN** `ToolBatchStarted` consumed the input
- **WHEN** the turn completes
- **THEN** the session records `TurnRecorded`
- **AND** replay reports the turn as complete

#### Scenario: Explicit closure removes an unconsumed input

- **GIVEN** an input remains pending and cannot proceed
- **WHEN** the session records `InputClosed`
- **THEN** replay reports the input as explicitly closed
- **AND** the session does not silently discard it

#### Scenario: Invalid transition does not poison the journal

- **GIVEN** a proposed transition violates the current durable input order
- **WHEN** the session validates the transition
- **THEN** it rejects the transition before journal append
- **AND** a later recovery can replay the journal

#### Scenario: Child result bypasses ordinary input admission

- **GIVEN** a child result becomes ready for its parent
- **WHEN** the parent accepts that result
- **THEN** the session does not persist it as an ordinary user input
- **AND** the result follows the correlated tool-result path

### Requirement: The hub forwards session deactivation to every client

The session hub SHALL forward `SessionDeactivated` to the connected client as the wire type `session_deactivated`. `SessionOutputDtoMapper` SHALL map it in both directions. A client SHALL NOT read it as an unknown type or as an error output.

A client with a wait in progress, such as a headless run, needs this output. A daemon restart then ends the wait with a clear failure. Without it, the client waits for a turn that never comes.

#### Scenario: A daemon restart reaches a waiting client

- **GIVEN** a CLI client waits on a session that has an open child run
- **WHEN** the daemon prepares for restart and the session emits `SessionDeactivated`
- **THEN** the client receives `SessionDeactivated`
- **AND** the headless run ends with exit `1`

#### Scenario: The mapper does not turn deactivation into an unknown error

- **GIVEN** a `SessionDeactivated` output
- **WHEN** it goes through `ToDto` and `FromDto`
- **THEN** the result is a `SessionDeactivated` output for the same session
- **AND** it is not an `ErrorOutput` with the text "Unknown output type"

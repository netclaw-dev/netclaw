## REMOVED Requirements

### Requirement: Per-turn tool loop limit is iteration-based

**Reason**: A fixed iteration ceiling can stop useful work without identifying repeated calls.
**Migration**: Use the exact recurrence and adjacent-cycle rules below. Keep explicit deadlines, cancellation, and ordinary authorization.

## ADDED Requirements

### Requirement: Tool recurrence governs repeated work

The system SHALL use exact per-call recurrence and the existing adjacent-cycle rules to stop repeated tool work. It SHALL NOT stop a useful task only because it reached `MaxToolIterationsPerTurn` or a child iteration limit.

A call identity SHALL use the canonical tool and prepared arguments. It SHALL exclude valid call metadata and correlation IDs, and it SHALL retain rejected metadata. It SHALL count one observation for each call identity per completed model feedback round. Unrelated calls and duplicate call counts SHALL NOT erase recurrence evidence. A distinct actual outcome SHALL reset evidence only for the affected identity.

After two equal completed feedback rounds for one identity, the system SHALL refuse the next matching call before dispatch and return a corrective tool result for every matching call ID. If the model repeats that identity after correction, the system SHALL settle the turn without another model request. The existing adjacent period-one through period-three cycle guard SHALL remain active for eligible complete batches.

A trusted repeat exception SHALL apply only to an authorized, non-cancel status query for an accessible pending background job. It SHALL NOT apply to cancellation, denied lookup, a different job, or model-authored claims. Every call SHALL pass normal authorization and tool validation.

The live session owner SHALL preserve recurrence evidence and unresolved corrections across context compaction. Process recovery SHALL NOT restore a detector checkpoint. It SHALL replay recorded calls and results, then let the model select the next action without automatic tool re-execution. A callback SHALL affect only the dispatch that currently owns it; a stale callback SHALL NOT write history, change recurrence, cancel another dispatch, or resume the model.

#### Scenario: Unrelated work does not clear recurrence

- **GIVEN** two completed feedback rounds contain the same call identity and outcome
- **AND** unrelated diagnostic calls occur between them
- **WHEN** the model requests the repeated call again
- **THEN** the runtime refuses it before dispatch
- **AND** it leaves eligible unrelated work available

#### Scenario: Duplicate calls count once per round

- **GIVEN** one model response contains several calls with the same identity
- **WHEN** their results complete
- **THEN** the runtime records one observation for that identity in the round
- **AND** a changed duplicate count does not clear its recurrence evidence

#### Scenario: A changed outcome permits productive work

- **GIVEN** a call identity has prior recurrence evidence
- **WHEN** its actual outcome changes
- **THEN** the runtime resets evidence only for that identity
- **AND** it still applies normal authorization and validation

#### Scenario: Correction precedes dispatch

- **GIVEN** two equal completed feedback rounds establish recurrence
- **WHEN** the model submits the next matching call
- **THEN** the runtime dispatches no matching call
- **AND** it returns one corrective result for each matching call ID

#### Scenario: Repeated call after correction settles without another model request

- **GIVEN** the model received a corrective result for a repeated call
- **WHEN** it requests the same identity again
- **THEN** the runtime records a terminal failure or partial result
- **AND** it makes no further model request

#### Scenario: Pending job status query uses a narrow exception

- **GIVEN** an authorized status query confirms that one accessible job remains pending
- **WHEN** the model repeats that same non-cancel status query
- **THEN** the recurrence rule permits the query
- **AND** the job lookup repeats its normal authority checks

#### Scenario: The exception does not permit cancellation

- **GIVEN** a status query has a valid pending-job exception
- **WHEN** the model requests cancellation or a different job status
- **THEN** the exception does not apply
- **AND** the call follows ordinary recurrence rules

#### Scenario: Useful work can exceed the former iteration limit

- **GIVEN** a task needs more useful tool rounds than the former configured limit
- **WHEN** each round makes distinct progress and passes normal policy
- **THEN** the session can continue the task
- **AND** recurrence rules remain active

#### Scenario: Live compaction preserves recurrence evidence

- **GIVEN** a live session has recurrence evidence or an unresolved correction
- **WHEN** the runtime compacts its context
- **THEN** the live owner retains that evidence and correction
- **AND** the next model request follows the same recurrence decision

#### Scenario: Process recovery does not restore detector checkpoint

- **GIVEN** a session restarts after it records completed tool calls and results
- **WHEN** the session resumes
- **THEN** it replays those recorded facts without a detector checkpoint
- **AND** it does not repeat a tool call automatically

#### Scenario: Stale callback cannot affect a later dispatch

- **GIVEN** a callback belongs to a dispatch that no longer owns the active turn
- **WHEN** that callback arrives
- **THEN** the runtime ignores it for history, recurrence, cancellation, and model continuation
- **AND** only the current dispatch owner can change the active turn

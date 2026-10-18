## REMOVED Requirements

### Requirement: Tool recurrence governs repeated work

**Reason**: The old requirement stops the turn when the model repeats a refused call. The person then sees a runtime error. Aaron's rule of 18 October 2026 replaces the stop with a second refusal and a model-facing nudge.

**Migration**: Use "Tool recurrence refuses repeated work and continues the turn". It keeps every other rule and scenario of the old requirement.

## ADDED Requirements

### Requirement: Tool recurrence refuses repeated work and continues the turn

The system SHALL use exact per-call recurrence and the existing adjacent-cycle rules to stop repeated tool work. It SHALL NOT stop a useful task only because it reached `MaxToolIterationsPerTurn` or a child iteration limit.

A call identity SHALL use the canonical tool and prepared arguments. It SHALL exclude valid call metadata and correlation IDs, and it SHALL retain rejected metadata. A valid `_background: true` request SHALL be part of the call identity, because it changes how the call runs. It SHALL count one observation for each call identity per completed model feedback round. Unrelated calls and duplicate call counts SHALL NOT erase recurrence evidence. A distinct actual outcome SHALL reset evidence only for the affected identity.

After two equal completed feedback rounds for one identity, the system SHALL refuse the next matching call before dispatch and return a corrective tool result for every matching call ID. The existing adjacent period-one through period-three cycle guard SHALL remain active for eligible complete batches.

If the model repeats a refused identity or a refused adjacent action, the system SHALL refuse the repeated call again before dispatch. It SHALL return a corrective tool result for every refused call ID. It SHALL then add one model-facing system nudge after the tool results of that batch, and it SHALL make the next model request. The turn SHALL continue. The system SHALL NOT fail the turn, send an error to the person, or report the error category `ModelBehavior` for this case. A person SHALL see the refusal only when the model writes about it.

The parent nudge SHALL name each refused tool. It SHALL tell the model that the call did not execute and that the same call gets the same refusal. It SHALL list these alternatives: run a slow shell command as a background job with `_background: true` and `check_background_job`, change the command or its arguments, divide the work into smaller steps, and ask the person. It SHALL name `skill_read_resource('netclaw-operations', 'references/long-running-commands.md')`.

A child run SHALL follow the same rule. The child nudge SHALL NOT offer a background job, because a child has no background job manager. It SHALL tell the child that it can stop and give its result to the parent, with the name of the command that did not finish.

The system SHALL NOT add a count limit or a time limit for repeated refusals. The existing bounds remain the backstop: cancellation by the person, the wait limit of a caller, child run timeouts, context compaction, and provider budgets.

A trusted repeat exception SHALL apply only to an authorized, non-cancel status query for an accessible pending background job. It SHALL NOT apply to cancellation, denied lookup, a different job, or model-authored claims. Every call SHALL pass normal authorization and tool validation.

The live session owner SHALL preserve recurrence evidence and unresolved corrections across context compaction. Process recovery SHALL NOT restore a detector checkpoint. It SHALL replay recorded calls and results, then let the model select the next action without automatic tool re-execution. A callback SHALL affect only the dispatch that currently owns it; a stale callback SHALL NOT write history, change recurrence, cancel another dispatch, or resume the model.

Schematic flow. It omits authorization, validation, and persistence steps:

```text
round 1: call C executes             -> outcome O
round 2: call C executes             -> outcome O   (two equal rounds)
round 3: call C refused              -> corrective result, no nudge
round 4: call C refused again        -> corrective result + system nudge
round 5: model request continues     -> model selects another action
```

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
- **AND** it adds no nudge for this first correction

#### Scenario: A repeated refused call is refused again with a nudge

- **GIVEN** the model received a corrective result for a `shell_execute` call
- **WHEN** it requests the same identity again
- **THEN** the runtime dispatches no matching call
- **AND** it returns one corrective result for each matching call ID
- **AND** the next model request ends with a system nudge that names `shell_execute`
- **AND** the nudge names `skill_read_resource('netclaw-operations', 'references/long-running-commands.md')`
- **AND** the turn continues

#### Scenario: A repeated refused call does not fail the turn

- **GIVEN** the model repeats a refused call in a parent session
- **WHEN** the model then writes a text answer
- **THEN** the turn completes without an error output
- **AND** a headless run exits with code 0 and prints no `[turn:failed]` line
- **AND** the person sees only the text of the model

#### Scenario: Another action after the nudge runs

- **GIVEN** the runtime refused a repeated call and added the nudge
- **WHEN** the model requests a call with different arguments
- **THEN** that call executes after normal authorization and validation

#### Scenario: A different call in the same batch is not refused

- **GIVEN** one batch contains a repeated refused call and a call with a new identity
- **WHEN** the runtime evaluates the batch
- **THEN** it refuses only the repeated call
- **AND** the new call executes

#### Scenario: A background run of a refused command is a different action

- **GIVEN** a parent session refused a foreground `shell_execute` call
- **WHEN** the model sends the same command with a valid `_background: true`
- **THEN** the runtime does not refuse it as a repeat
- **AND** the call follows normal authorization and the background job rules

#### Scenario: A timeout change alone is the same action

- **GIVEN** the runtime refused a call
- **WHEN** the model sends the same call with only a new `_timeout_seconds` value
- **THEN** the runtime refuses it again
- **AND** it adds the nudge

#### Scenario: A child repeats a refused call

- **GIVEN** a child run received a corrective result for a call
- **WHEN** the child requests the same identity again
- **THEN** the runtime refuses the call again without dispatch
- **AND** the next child model request ends with a child nudge that offers no background job
- **AND** the child run continues and can complete normally

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

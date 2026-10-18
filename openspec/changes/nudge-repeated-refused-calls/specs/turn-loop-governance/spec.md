## REMOVED Requirements

### Requirement: Tool recurrence governs repeated work

**Reason**: The old requirement stops the turn when the model repeats a refused call. The person then sees a runtime error. Aaron's rule of 18 October 2026 replaces the stop with a second refusal and a model-facing nudge. After five refused repeats in one turn, the nudge escalates. A sixth refused repeat ends the turn quietly with the text of the model.

**Migration**: Use "Tool recurrence refuses repeated work and continues the turn". It keeps every other rule and scenario of the old requirement.

## ADDED Requirements

### Requirement: Tool recurrence refuses repeated work and continues the turn

The system SHALL use exact per-call recurrence and the existing adjacent-cycle rules to stop repeated tool work. It SHALL NOT stop a useful task only because it reached `MaxToolIterationsPerTurn` or a child iteration limit.

A call identity SHALL use the canonical tool and prepared arguments. It SHALL exclude valid call metadata and correlation IDs, and it SHALL retain rejected metadata. A valid `_background: true` request on `shell_execute` SHALL be part of the call identity, because it changes how that call runs. Only `shell_execute` runs in the background. Another tool runs a background request in the foreground, so `_background` SHALL NOT change the identity of another tool. It SHALL count one observation for each call identity per completed model feedback round. Unrelated calls and duplicate call counts SHALL NOT erase recurrence evidence. A distinct actual outcome SHALL reset evidence only for the affected identity.

After two equal completed feedback rounds for one identity, the system SHALL refuse the next matching call before dispatch and return a corrective tool result for every matching call ID. The existing adjacent period-one through period-three cycle guard SHALL remain active for eligible complete batches.

If the model repeats a refused identity or a refused adjacent action, the system SHALL refuse the repeated call again before dispatch. It SHALL return a corrective tool result for every refused call ID. It SHALL then add one model-facing system nudge after the tool results of that batch, and it SHALL make the next model request. The turn SHALL continue. The system SHALL NOT fail the turn, send an error to the person, or report the error category `ModelBehavior` for this case. A person SHALL see the refusal only when the model writes about it.

The history SHALL keep at most one repeat nudge. When the system adds a repeat nudge, it SHALL remove the earlier repeat nudge from the history.

The parent nudge SHALL name each refused tool. It SHALL tell the model that the call did not execute and that the same call gets the same refusal. It SHALL list these alternatives: run a slow shell command as a background job with `_background: true` and `check_background_job`, change the command or its arguments, divide the work into smaller steps, and ask the person. It SHALL name `skill_read_resource('netclaw-operations', 'references/long-running-commands.md')`.

A child run SHALL follow the same rule. The child nudge SHALL NOT offer a background job, because a child has no background job manager. It SHALL tell the child that it can stop and give its result to the parent, with the name of the command that did not finish.

The system SHALL count refused repeats for each turn. One refused repeat is one model response that repeats at least one refused identity or the refused adjacent action. Several repeated calls in one response count as one refused repeat. A first correction and a new call do not count. A different completed action SHALL NOT reset the count.

Only new person input SHALL reset the count, with one exception for a new turn:

- A new turn SHALL start with a count of zero, whoever starts it: a person, or an automation input such as a reminder fire, a webhook call, or a background job result. Without this rule, an automation session that no person writes to would keep the count of earlier turns and end its first later refused repeat at once.
- Person input that joins a running turn SHALL reset the count, because it also resets the recurrence evidence. An example is a new Slack message in the same thread.
- A child result that the session delivers in the middle of a turn SHALL NOT reset the count, although it resets the recurrence evidence.
- An automation input that joins a running automation turn SHALL NOT reset the count, although it resets the recurrence evidence. Its requester is a fixed system sender (`reminder-system`, `background-job-system`, or `webhook:{route}`), so every later fire of the same kind joins the turn. An automation input has the principal `VerifiedAutomation` or `SystemProcess`.
- A turn that a child result starts after the earlier turn ended has no new input. It SHALL continue the count of the earlier turn.

Refused repeats 1 to 4 of a turn SHALL get the nudge above. Refused repeat 5 SHALL get an escalated nudge in place of that nudge. The escalated nudge SHALL name each refused tool and SHALL tell the model these facts and steps:

- The call did not execute.
- Stop this action.
- End the turn now.
- Tell the person what blocked the work, and name the action that did not finish.
- Another refused call ends the turn.
- No call in that response runs.

The last fact covers a call next to the repeat, for example a `send_message` call of a reminder turn. The model must not rely on such a call. The call stays refused after the escalated nudge.

If the model sends refused repeat 6, the system SHALL end the turn quietly:

- It SHALL NOT dispatch any call of that response, also a new call next to the repeat.
- It SHALL NOT make another model request for the turn.
- If the response contains text, the turn SHALL complete with that text as the reply. The history keeps the text without the tool calls.
- If the response contains no text, the turn SHALL complete with no new reply. The person keeps the text that the model wrote before.
- If the response contains no text, the `TurnCompleted` output SHALL set `QuietEnd` to true. Every other `TurnCompleted` output SHALL set it to false.
- It SHALL NOT emit an error output, add an error reply, record a failed turn, or report `ModelBehavior`.
- Slack, Discord, and Mattermost SHALL post nothing for a `TurnCompleted` output with `QuietEnd` true and no earlier post in the turn. In particular, they SHALL NOT post the empty-turn fallback text. That text is Netclaw text, not text of the model, and its advice to send the message again starts the same loop again. These channels SHALL still clear the typing indicator. They SHALL still post the empty-turn fallback for any other turn that produced no reply.
- A headless run SHALL exit with code 0 and print no `[turn:failed]` line. Its JSON envelope SHALL have no `errorCategory` and no `errorKind`. Its `response` SHALL hold only text that the model wrote.

`QuietEnd` crosses the hub as the optional boolean `quietEnd` of the `turn_completed` output. A missing or null field comes from an earlier daemon and SHALL read as false, so an empty turn from an earlier daemon keeps the fallback.

A reminder turn that ends quietly SHALL report success with empty output. The reminder gets no failure alert, because the person-facing rule allows no Netclaw error. The session log SHALL hold the warning `turn_refused_repeat_end`, so an operator can find the end.

A child run SHALL use the same count for its run. Its escalated nudge SHALL tell the child to stop this action, to give its result to the parent, to name the action that did not finish, and that no call in that response runs. Refused repeat 6 SHALL end the child run quietly. The run result SHALL be successful, with the outcome `partial` and the reason `tool_cycle_stopped`. Its output SHALL be the last text that the child model wrote in the run: the text of the final response, or the latest earlier text. The output is empty when the child wrote no text. The parent SHALL get this result as a successful partial result, not as an error.

The end at refused repeat 6 is the only runtime bound for a refused loop. Child results and automation inputs do not extend it, because they do not reset the count. Only person input extends it, and each person message gives the model a new instruction. These events do not stop the loop: a cancel request (the TUI and the session have no cancel command), a caller wait limit, a child inactivity watchdog, and context compaction. These events can stop it before refused repeat 6: the model selects another action, a provider error or quota, the model obeys a message from the person, and a daemon stop or restart.

A trusted repeat exception SHALL apply only to an authorized, non-cancel status query for an accessible pending background job. It SHALL NOT apply to cancellation, denied lookup, a different job, or model-authored claims. Every call SHALL pass normal authorization and tool validation.

The live session owner SHALL preserve recurrence evidence and unresolved corrections across context compaction. Process recovery SHALL NOT restore a detector checkpoint. It SHALL replay recorded calls and results, then let the model select the next action without automatic tool re-execution. A callback SHALL affect only the dispatch that currently owns it; a stale callback SHALL NOT write history, change recurrence, cancel another dispatch, or resume the model.

Schematic flow. It omits authorization, validation, and persistence steps:

```text
round 1:      call C executes        -> outcome O
round 2:      call C executes        -> outcome O   (two equal rounds)
round 3:      call C refused         -> corrective result, no nudge
rounds 4..7:  call C refused again   -> corrective result + nudge        (refused repeats 1..4)
round 8:      call C refused again   -> corrective result + escalated nudge (refused repeat 5)
round 9:      call C again           -> no dispatch, no model request; the turn completes
                                        with the text of round 9, or with no new reply
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

#### Scenario: The fifth refused repeat gets the escalated nudge

- **GIVEN** the model sent four refused repeats of `search_tools` in one turn
- **WHEN** it sends the same call again
- **THEN** the runtime refuses the call without dispatch
- **AND** the next model request ends with the escalated nudge
- **AND** the escalated nudge tells the model to stop, to end the turn, and to tell the person what blocked it
- **AND** the escalated nudge tells the model that no call in that response runs

#### Scenario: A refused repeat after the escalation ends the turn quietly

- **GIVEN** the model received the escalated nudge in a parent session
- **WHEN** it sends a refused call again with the text "The search stays blocked."
- **THEN** the runtime dispatches no call and makes no further model request
- **AND** the turn completes with the reply "The search stays blocked."
- **AND** no error output, error reply, failed turn, or `ModelBehavior` category occurs
- **AND** a headless run exits with code 0 and its JSON `response` is "The search stays blocked."

#### Scenario: A silent repeat after the escalation ends the turn with no new reply

- **GIVEN** the model received the escalated nudge
- **WHEN** it sends a refused call again with no text
- **THEN** the turn completes with no new reply and no error
- **AND** the `TurnCompleted` output has `QuietEnd` true
- **AND** the next person message starts a turn whose history has no tool call without a result

#### Scenario: A channel posts nothing for a quiet end with no text

- **GIVEN** a Slack, Discord, or Mattermost thread whose turn posted nothing
- **WHEN** the session completes the turn with `QuietEnd` true
- **THEN** the binding posts no text, also not the empty-turn fallback
- **AND** the typing indicator clears

#### Scenario: An ordinary empty turn still posts the fallback

- **GIVEN** a Slack, Discord, or Mattermost thread whose turn posted nothing
- **WHEN** the session completes the turn with `QuietEnd` false
- **THEN** the binding posts the empty-turn fallback, as before

#### Scenario: An earlier daemon sends no quiet end flag

- **GIVEN** a `turn_completed` output with no `quietEnd` field
- **WHEN** a client reads it
- **THEN** `QuietEnd` is false

#### Scenario: A new call next to the last repeat does not run

- **GIVEN** the model received the escalated nudge
- **WHEN** one response repeats the refused call and also requests a new call
- **THEN** the runtime ends the turn quietly
- **AND** the new call does not run

#### Scenario: Person input starts a new count

- **GIVEN** the model sent four refused repeats in a turn
- **WHEN** a new person message joins the running turn
- **THEN** the count starts again from zero
- **AND** the next refused repeat gets the plain nudge, not the escalated nudge

#### Scenario: A child result in the middle of the loop does not reset the count

- **GIVEN** the model sent three refused repeats in a turn and a child run is open
- **WHEN** the session delivers the child result in the middle of the turn
- **AND** the model sends the refused call again, which runs twice and is then refused and repeated again
- **THEN** the count continues from three
- **AND** the second refused repeat after the delivery is repeat 5 and gets the escalated nudge

#### Scenario: An automation input that joins an automation turn does not reset the count

- **GIVEN** a reminder turn had four refused repeats
- **WHEN** another reminder fire joins the running turn, and the model then repeats a refused call
- **THEN** the count stays at four
- **AND** the first refused repeat after the join is repeat 5 and gets the escalated nudge

#### Scenario: A new turn starts a new count

- **GIVEN** a turn ended with three refused repeats
- **WHEN** a person or an automation input starts the next turn
- **THEN** the next turn counts from zero
- **AND** its fifth refused repeat gets the escalated nudge

#### Scenario: Other work does not reset the count

- **GIVEN** the model sent four refused repeats in a turn
- **WHEN** a different call completes and the model then repeats a refused call
- **THEN** that refused repeat is repeat 5 and gets the escalated nudge

#### Scenario: Only the latest repeat nudge stays in history

- **GIVEN** a turn has three refused repeats
- **WHEN** the next model request starts
- **THEN** its history holds exactly one repeat nudge, the latest one

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

#### Scenario: A background request on another tool is the same action

- **GIVEN** the runtime refused a `web_fetch` call
- **WHEN** the model sends the same call with `_background: true`
- **THEN** the runtime refuses it again as a repeat
- **AND** it adds the nudge

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

#### Scenario: A child ignores the escalated child nudge

- **GIVEN** a child run received the escalated child nudge
- **WHEN** the child sends a refused call again
- **THEN** the child run ends without dispatch and without another model request
- **AND** the run result is successful with the outcome `partial` and the reason `tool_cycle_stopped`
- **AND** its output is the last text that the child model wrote
- **AND** the parent gets no error for the child

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

## ADDED Requirements

### Requirement: Child results reach the parent as admitted notices

When a child run ends, the system SHALL write the result to `result.md` in the run folder and SHALL admit exactly one notice input for the run. The notice SHALL name the agent, the run, the outcome, and the absolute path of `result.md`. It SHALL NOT hold the child output. The parent model SHALL read the file with a real `file_read` call. The system SHALL NOT build a synthetic `child_run_result` tool pair.

The notice SHALL carry the recorded requester of the run owner. It SHALL join a running turn only for the same requester, and otherwise it SHALL wait and run in its own turn. Notices and other inputs SHALL run in admission order. A notice SHALL NOT change the authority of a running turn and SHALL NOT abandon an open approval.

#### Scenario: A result becomes one metadata notice

- **GIVEN** a child run ends with output
- **WHEN** the owner finishes the result
- **THEN** `result.md` holds the outcome, the output, and the log path
- **AND** the journal holds one notice that names the agent, the run, the outcome, and the path
- **AND** the notice text does not hold the output

#### Scenario: The model reads the result with a real tool call

- **GIVEN** a notice for an ended run
- **WHEN** the next model request is built
- **THEN** the notice is a user message and no `child_run_result` call exists
- **AND** a `file_read` call returns the text as a real tool result

#### Scenario: A notice of another requester waits

- **GIVEN** Bob's turn runs and Alice's child ends
- **WHEN** Alice's notice is admitted
- **THEN** it waits for its own turn
- **AND** Bob's tool calls keep Bob's authority

#### Scenario: A result is not lost or repeated

- **GIVEN** an ended run with no notice
- **WHEN** the session recovers, passivates, or restarts
- **THEN** exactly one notice is admitted for the run
- **AND** a replay of the notice adds no second notice

#### Scenario: A notice that a crash left unconsumed runs once

- **GIVEN** the journal holds an admitted notice that no turn consumed, because the session stopped without a drain
- **WHEN** the session recovers
- **THEN** the notice runs once under the recorded authority of the run owner
- **AND** a later message does not leave the notice behind, and a snapshot passes after the notice turn
- **AND** the message that wakes the session buffers behind the notice turn and does not run first
- **AND** an unconsumed input of a person is not part of this rule
- **AND** a notice with a damaged recorded authority stays unconsumed, the log shows an Error with the run id and the reason, and the recovery of the other notices and of the lost children continues

#### Scenario: A restart reminder does not become input while the session is busy

- **GIVEN** a notice turn runs, either after a crash recovery or for a lost child
- **WHEN** the restart reminder arrives
- **THEN** the session acknowledges it and does not admit it as input
- **AND** when the session is idle and a pending input remains that no turn served, the reminder resumes that input
- **AND** when no pending input remains, the session drops the reminder and logs that nothing is owed

#### Scenario: A rejected notice does not pin the session

- **GIVEN** the reducer refuses the notice of an ended run
- **WHEN** the owner handles the refusal
- **THEN** the log names the real reason and the path of the result file
- **AND** the session does not count the run as owed, so the run neither keeps the session in memory nor drives the restart reminder

#### Scenario: A failed result write still yields a notice

- **GIVEN** the render or the write of `result.md` fails with any non-fatal error
- **WHEN** the owner learns of the failure
- **THEN** the owner admits one notice that says the write failed and that the file can be missing or out of date

### Requirement: A child run folder records the task and the result

The system SHALL write `task.md` in the run folder before the child makes its first model call. It SHALL record the agent, the requester sender and principal, and the brief. The system SHALL remove the run folder when the owner rejects the start. The system SHALL refuse `spawn_agent` when the requester policy does not expose `file_read`, and SHALL start no child in that case.

#### Scenario: The task file exists before the child runs

- **GIVEN** a spawn by Alice
- **WHEN** the child makes its first model call
- **THEN** `task.md` holds the agent, Alice, and the brief

#### Scenario: A rejected start leaves no folder

- **GIVEN** the owner rejects a start
- **WHEN** the spawner reports the rejection
- **THEN** the run folder does not exist

#### Scenario: A requester without file_read cannot start a child

- **GIVEN** a profile without `file_read`
- **WHEN** the model calls `spawn_agent`
- **THEN** the call fails with a reason that names `file_read`
- **AND** no run is accepted

### Requirement: Child run files stay out of the working context

The system SHALL NOT add a read of `task.md` or `result.md` in a child run folder to the `recent_files` list of the working context. This holds for the envelope layout and for the legacy layout. A project file with the same name, and a file under `artifacts/` of the run, SHALL stay in the list.

#### Scenario: A read of the result file adds nothing

- **GIVEN** a notice for an ended run
- **WHEN** the model reads `result.md` of the run with `file_read`
- **THEN** `recent_files` does not name `result.md` or `task.md` of the run

#### Scenario: A project file stays in the list

- **GIVEN** a project file named `result.md` and a file in `artifacts/` of a run
- **WHEN** the model reads them
- **THEN** both stay in `recent_files`

## MODIFIED Requirements

### Requirement: Child execution keeps the parent's authority

The system SHALL preserve the original requester, audience, and authority boundary for child tools, approvals, status, and cancellation. A child result SHALL NOT grant authority. The turn that serves a notice SHALL have the recorded authority of the run owner and SHALL keep its own authority from its start to its end. A child worker SHALL remain ephemeral and SHALL NOT restart an uncertain external action after owner recovery. Status SHALL distinguish durable acceptance from terminal completion. The run owner's status response SHALL include the pending approval count. Other requesters SHALL NOT see that count. An approval for a stale or unrelated run, call, requester, or attempt SHALL grant no authority.

The system SHALL persist cancellation intent before it acknowledges the intent. That acknowledgement SHALL NOT mean that the run is complete. The system SHALL report terminal cancellation only after the local dispatch confirms closure, and it SHALL start no new dispatch after closure. An uncertain external effect SHALL remain explicit.

#### Scenario: Child approval keeps the original requester

- **GIVEN** a child tool requires human approval
- **WHEN** the parent or an authorized requester resolves that approval
- **THEN** the decision applies only to that child call and its recorded requester
- **AND** a stale or unrelated decision grants no authority

#### Scenario: Recovery does not repeat an uncertain child action

- **GIVEN** an accepted child has an external action with no recorded terminal result
- **WHEN** the parent owner recovers
- **THEN** the system does not start the child again automatically
- **AND** the run reports an explicit non-success or uncertain state

#### Scenario: Status enforces run ownership

- **GIVEN** one requester owns a child run
- **WHEN** another requester asks for its status
- **THEN** the system denies that status request
- **AND** the owner can observe recorded progress and pending approval facts

#### Scenario: Accepted status is not terminal status

- **GIVEN** a child run has durable acceptance but no terminal result
- **WHEN** its owner requests status
- **THEN** the system reports a nonterminal state
- **AND** it does not report child completion

#### Scenario: Turn authority stays fixed when a result is ready

- **GIVEN** a turn of one requester runs several tool batches
- **WHEN** a child result of the same or another requester becomes ready
- **THEN** the notice of the same requester joins the turn without a change of authority
- **AND** the notice of another requester waits for its own turn

#### Scenario: Pending approval count is owner-only

- **GIVEN** an owned child run has one pending approval
- **WHEN** its owner requests status
- **THEN** the response includes a pending approval count of one
- **WHEN** that approval settles and the owner requests status again
- **THEN** the response includes a pending approval count of zero
- **AND** a non-owner cannot observe that count

#### Scenario: Stale approval does not grant authority

- **GIVEN** an approval refers to a stale run, call, requester, or attempt
- **WHEN** the system receives that approval
- **THEN** it rejects the approval as stale
- **AND** it dispatches no action under that approval

#### Scenario: Cancellation reports closure after local dispatch stops

- **GIVEN** an owner requests cancellation of an active child run
- **WHEN** the system persists cancellation intent
- **THEN** it acknowledges the intent without reporting terminal cancellation
- **WHEN** the local dispatch confirms closure
- **THEN** the system can report terminal cancellation
- **AND** it starts no new child dispatch after closure
- **AND** an uncertain external effect remains explicit

### Requirement: Deployed child journal records remain recoverable

Before the system writes a new child journal format, it SHALL provide a tested read or migration path for deployed `tta-v1`, `cra-v1`, `cre-v1`, `cre-v2`, `ss-v2`, and extended existing records.

The path SHALL preserve accepted identities, terminal results, authority, pending consent, job lineage, and exactly-once transfer of delivered child results. A result that a deployed parent already received as a tool pair SHALL stay received: the history SHALL hold the pair once and no notice SHALL follow. A result that the parent never received SHALL become one notice and no pair. The reader SHALL keep `ResultPrepared` and the child continuation as read-only data, and no writer SHALL set them. The system SHALL NOT execute an uncertain input or infer duplicate identity from matching task text. It SHALL preserve the original journal and a restore path until conversion succeeds.

#### Scenario: Valid deployed records migrate without lost facts

- **GIVEN** a valid deployed journal with old child and extended session records
- **WHEN** the system reads or migrates that journal
- **THEN** accepted identities, terminal results, authority, consent, lineage, and result transfer remain intact
- **AND** the converted journal replays with the candidate reader

#### Scenario: Invalid adoption receives a safe disposition

- **GIVEN** the reported journal contains an invalid task-adoption event
- **WHEN** the system migrates or recovers that journal
- **THEN** it does not execute the uncertain input
- **AND** it records an explicit safe disposition without comparing prompt text as identity
- **AND** it preserves the original journal and restore path

#### Scenario: New writes wait for compatibility proof

- **GIVEN** deployed child records lack a tested read or migration path
- **WHEN** the candidate starts a session with those records
- **THEN** it does not write the new child journal format to that session
- **AND** it reports the compatibility failure clearly

#### Scenario: A delivered result is not delivered again

- **GIVEN** a deployed journal where the parent consumed a prepared result
- **WHEN** the session recovers
- **THEN** the history holds the legacy pair once
- **AND** no notice is admitted

#### Scenario: An undelivered result becomes one notice

- **GIVEN** a deployed journal with a prepared result that no parent decision consumed
- **WHEN** the session recovers
- **THEN** exactly one notice is admitted and no pair is delivered

## REMOVED Requirements

### Requirement: Ready results resume the parent at a safe boundary

**Reason**: The child precedence, the delivery slot, and the synthetic tool pair are deleted. A ready result is an admitted notice and follows the ordinary input rules.

**Migration**: See "Child results reach the parent as admitted notices" and "Deployed child journal records remain recoverable".

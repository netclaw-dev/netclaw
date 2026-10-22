## ADDED Requirements

### Requirement: A child run parks at consent

A child call that needs consent SHALL NOT wait in memory. The child SHALL let the other calls of the same model
response finish, and then it SHALL send its park to the owning session and stop. The session SHALL accept the park
before it shows any prompt of the park. The park SHALL hold the child conversation with the results of the calls that
did not park, the parts of the batch that wait for the parked calls, the working directories that the batch ran with,
the loop-guard state, the skill overlay, and the consent requests of the parked calls. A parked call SHALL NOT run
while the run is parked. A call that already has a result SHALL NOT run again. The session SHALL number the prompts of
a run from a counter that never repeats a number in one run.

The session SHALL accept a park only for a run that it owns and that is still accepted. When the session refuses the
park, cannot record it, or finds it above the size limit, the run SHALL end with its own stated reason (a cancelled
run ends as cancelled; the other cases end as failed), no prompt SHALL appear, and no parked call SHALL run. A later
result from the stopped child SHALL NOT change that reason.

#### Scenario: A child parks after its siblings end

- **GIVEN** a child response with two calls, one that needs consent and one that does not
- **WHEN** the call that does not need consent ends
- **THEN** the session accepts the park with the result of that call
- **AND** the session shows the consent prompt only after the park is accepted
- **AND** no child actor runs for the run

#### Scenario: A park for a cancelled run is refused

- **GIVEN** the owner cancelled a child run while its batch ran
- **WHEN** the park of that run arrives
- **THEN** the session refuses the park and shows no prompt
- **AND** the run ends as cancelled

#### Scenario: A park that the journal rejects ends the run

- **GIVEN** a child that sends its park
- **WHEN** the journal rejects the park
- **THEN** the run ends as failed with the reason `tool_execution_failed`
- **AND** no prompt appears and the parked call does not run

#### Scenario: A park above the size limit ends the run

- **GIVEN** a child whose park is above the size limit
- **WHEN** the child sends its park
- **THEN** the run ends as failed with the reason `park_too_large`
- **AND** no prompt appears and the parked call does not run

### Requirement: A parked run resumes once from its park

When every consent request of a park has an answer, the session SHALL record the resume before any parked call runs,
and then start a new child from the park. A live answer and an answer after a restart SHALL use the same resume path.
The resumed child SHALL run only the parked calls, each at most once, in the working directories that the batch ran
with. An approved call SHALL get a one-time consent for the exact prompted candidates of that call only. A refused
call SHALL get a refusal result and SHALL NOT run. The session SHALL NOT resume a run while a restart drain is
pending. When the journal rejects the resume record, the run SHALL end as failed, and the result notice SHALL say "The
parked calls did not run."

The child SHALL apply the result effects of the finished calls at the park, and the result effects of the parked calls
after they run. The nudges of the batch SHALL come after all results. A parked call that sets the working directory
SHALL win over a finished call of the same batch that sets it. The resumed child SHALL keep the loop-guard state and
the round count of the park, so its next checkpoint round is above the recorded round.

The resumed child SHALL take its requester, boundary, channel, and delivery targets from the run record, and its
working directories from the park, never from the current turn or the current parent. Its audience SHALL be the
recorded audience. It SHALL resolve its model, its tools, its agent profile, its operating rules, and its project
instructions under the current configuration and policy, as a new spawn does.

When a spawn rule fails at resume (no spawner, subagents disabled, a `Public` audience, a deleted agent profile, no
child tool, no result reader, or no usable model provider), the run SHALL end as failed with that reason, the result
notice SHALL state the reason, and the parked calls SHALL NOT run.

#### Scenario: An approval resumes the parked call once

- **GIVEN** a child parked at one consent request
- **WHEN** the requester approves once
- **THEN** the session records the resume, starts the child, and the parked call runs once
- **AND** the call that ran before the park does not run again

#### Scenario: A refusal runs nothing

- **GIVEN** a child parked at one consent request
- **WHEN** the requester denies it
- **THEN** the resumed child gets the refusal result for that call
- **AND** the call does not run

#### Scenario: Two parked calls wait for both answers

- **GIVEN** a child parked at two consent requests
- **WHEN** the requester approves the first request only
- **THEN** the run stays parked and neither call runs
- **WHEN** the requester approves the second request
- **THEN** both calls run, each once

#### Scenario: The first checkpoint after a resume is above the park round

- **GIVEN** a child parked after round N
- **WHEN** the requester approves and the resumed child ends its batch
- **THEN** the child sends the checkpoint of round N+1
- **AND** the session accepts it

#### Scenario: A finished project change applies at the park

- **GIVEN** a child batch with a finished `set_working_directory` call and a parked shell call
- **WHEN** the requester approves the shell call
- **THEN** the shell call runs in the working directory that the batch started with
- **AND** the new project directory of the finished call is in place after the batch

#### Scenario: A parked project change wins

- **GIVEN** a child batch with a finished `set_working_directory` call to A and a parked `set_working_directory` call
  to B
- **WHEN** the requester approves the parked call
- **THEN** the project directory after the batch is B

#### Scenario: The resumed child keeps the run authority

- **GIVEN** Alice's child is parked
- **AND** Bob's turn runs in the same session
- **WHEN** Alice approves the parked request
- **THEN** the resumed child runs under Alice's recorded authority and audience

#### Scenario: A deleted agent profile ends the run

- **GIVEN** a parked child whose agent profile the operator deleted
- **WHEN** the requester approves the parked request
- **THEN** the run ends as failed with the reason `spawn_unavailable`
- **AND** the parked call does not run

#### Scenario: No usable model at resume

- **GIVEN** a parked child, and the model provider can no longer give a client
- **WHEN** the requester approves the parked request
- **THEN** the run ends as failed with the reason `spawn_error`
- **AND** the parked call does not run

#### Scenario: A rejected resume record ends the run

- **GIVEN** a parked child whose last request gets an answer
- **WHEN** the journal rejects the resume record
- **THEN** the run ends as failed and the notice says "The parked calls did not run."
- **AND** no child actor starts

### Requirement: A parked run can be cancelled with no actor

The owner SHALL be able to cancel a parked run. The session SHALL record the cancel request and the cancelled end in
one durable write, withdraw the open prompts of the run with the reason "cancelled", write the result file, and admit
the result notice. `check_agent_run` SHALL report a parked run as `parked` with its open request count.

#### Scenario: Cancel of a parked run

- **GIVEN** a parked child run with one open prompt
- **WHEN** the owner cancels the run
- **THEN** the run ends as cancelled and the parked call does not run
- **AND** a later click on the prompt gets the cancelled withdrawal text

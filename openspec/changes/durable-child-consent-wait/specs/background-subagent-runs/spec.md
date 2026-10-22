## ADDED Requirements

### Requirement: A child run parks at consent in its run record

A child call that needs consent SHALL NOT wait in memory. The child SHALL let the other calls of the same model
response finish, and then it SHALL send its park to the owning session and stop. The session SHALL record the park in
the run record as one durable event before it shows any prompt of the park. The park SHALL hold the child conversation,
the results of the calls that did not park, the consent requests of the parked calls, and the spawn facts that the run
needs to start again. A parked call SHALL NOT run while the run is parked. A call that already has a result SHALL NOT
run again. The session SHALL number the prompts of a run from a durable counter, so a prompt number never repeats in
one run.

#### Scenario: A child parks after its siblings end

- **GIVEN** a child response with two calls, one that needs consent and one that does not
- **WHEN** the call that does not need consent ends
- **THEN** the session records the run as parked with the result of that call
- **AND** the session shows the consent prompt only after the park is recorded
- **AND** no child actor runs for the run

#### Scenario: A park for a cancelled run is refused

- **GIVEN** the owner cancelled a child run while its batch ran
- **WHEN** the park of that run arrives
- **THEN** the session refuses the park and shows no prompt
- **AND** the run ends as cancelled

### Requirement: A parked run resumes once from its run record

When every consent request of a park has an answer, the session SHALL record the resume in the run record before any
parked call runs, and then start a new child from the run record. A live answer and an answer after a restart SHALL use
the same resume path. The resumed child SHALL run only the parked calls, each at most once. An approved call SHALL get a
one-time consent for the exact prompted candidates of that call only. A refused call SHALL get a refusal result and
SHALL NOT run. The session SHALL NOT resume a run while a restart drain is pending.

The resumed child SHALL take its requester, audience, boundary, and delivery targets from the run record, never from
the current turn. It SHALL resolve its model, its tools, its operating rules, and its project instructions under the
current configuration and policy, as a new spawn does. When a spawn rule fails at resume (subagents disabled, no child
tool, no result reader, or no usable model provider), the run SHALL end as failed with that reason, the result notice
SHALL state the reason, and the parked calls SHALL NOT run.

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

#### Scenario: The resumed child keeps the run authority

- **GIVEN** Alice's child is parked
- **AND** Bob's turn runs in the same session
- **WHEN** Alice approves the parked request
- **THEN** the resumed child runs under Alice's recorded authority and audience

#### Scenario: No usable model at resume

- **GIVEN** a parked child, and the model provider is no longer usable
- **WHEN** the requester approves the parked request
- **THEN** the run ends as failed with the reason `model_unavailable`
- **AND** the parked call does not run

### Requirement: A parked run can be cancelled with no actor

The owner SHALL be able to cancel a parked run. The session SHALL record the cancel request and the cancelled end in
one durable write, withdraw the open prompts of the run with the reason "cancelled", write the result file, and admit
the result notice. `check_agent_run` SHALL report a parked run as `parked` with its open request count.

#### Scenario: Cancel of a parked run

- **GIVEN** a parked child run with one open prompt
- **WHEN** the owner cancels the run
- **THEN** the run ends as cancelled and the parked call does not run
- **AND** a later click on the prompt gets the cancelled withdrawal text

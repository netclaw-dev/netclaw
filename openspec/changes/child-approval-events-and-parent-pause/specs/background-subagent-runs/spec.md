## ADDED Requirements

### Requirement: The parent learns of child consent requests without a poll

When a child consent request opens, or when the last open request of a requester closes, the parent session SHALL
get one approval notice. The session SHALL merge the events of one model boundary into one notice for each run-owner
requester. When the session is idle, it SHALL admit a ready notice at once, and the notice SHALL start one turn. When
a turn runs, the session SHALL admit the notice at the next tool-batch end or turn end. A notice SHALL be ready when it
holds a new request, or when no request of that requester stays open. An answer while other requests stay open SHALL
wait for the next ready notice. The notice SHALL name the agent, the run, and the tool of each request, and SHALL say
that the operator answers in the channel.

#### Scenario: A fan-out gives one notice for each boundary

- **GIVEN** a parent turn runs a model call
- **WHEN** five children ask for consent during that model call
- **THEN** the session admits one approval notice at the next tool-batch end or turn end
- **AND** the notice lists the five requests

#### Scenario: An idle parent wakes once

- **GIVEN** the parent session is idle
- **WHEN** one child asks for consent
- **THEN** the session admits one approval notice and starts one turn

#### Scenario: The last close gives a closing notice

- **GIVEN** two open child consent requests
- **WHEN** the operator answers the first
- **THEN** no notice is admitted
- **WHEN** the operator answers the second
- **THEN** one closing notice lists both answers

### Requirement: An open child consent request pauses the session

While any child consent request of the session is open, the session SHALL be paused. While paused:

- the session SHALL refuse `spawn_agent`, and the refusal text SHALL name the waiting runs and tell the model to ask
  the operator to answer or to offer to cancel a child;
- a child start from a person's slash command SHALL be accepted;
- an automated input and a child result notice SHALL NOT start a turn or join a running turn;
- an approval notice SHALL be admitted;
- the running turn SHALL be able to finish;
- a person's input SHALL start a turn and SHALL NOT end the pause.

The pause SHALL end only when every open child consent request closes by an answer, a refusal, or a withdrawal. No
child consent request SHALL expire. When the pause ends, the session SHALL admit the closing notice and then run the
waiting inputs.

#### Scenario: Spawn is refused while paused

- **GIVEN** a child consent request is open
- **WHEN** the parent model calls `spawn_agent`
- **THEN** the call returns an error that names the waiting runs
- **AND** no run folder stays

#### Scenario: A person's message does not end the pause

- **GIVEN** a child consent request is open
- **WHEN** Alice writes "start the other review now"
- **THEN** Alice's turn runs
- **AND** a `spawn_agent` call in that turn is refused

#### Scenario: A child result waits for the pause to end

- **GIVEN** child A waits for consent and child B ends
- **WHEN** the result notice of B is admitted
- **THEN** it does not start or join a turn
- **AND** it runs after the last open request closes

#### Scenario: Slash command start is accepted

- **GIVEN** a child consent request is open
- **WHEN** a person starts a child with a slash command
- **THEN** the child start is accepted

### Requirement: A closed child prompt is withdrawn on every surface

When a child run is cancelled, ends, or is lost while its consent request is open, the session SHALL close the request
as withdrawn with a reason (cancelled by parent, child ended, daemon restarted). The session SHALL emit a prompt-closed
output for each closed request. An answer to a withdrawn request SHALL get the reason text and SHALL run nothing and
grant nothing. No text SHALL name the child run or the agent.

#### Scenario: Cancel withdraws the prompts

- **GIVEN** a child run with two open consent requests
- **WHEN** the parent cancels the run
- **THEN** the session emits two prompt-closed outputs with the reason "cancelled by parent"
- **AND** the closing notice reports both as withdrawn

### Requirement: A child run parks at consent and resumes from its journal

A child run that needs consent SHALL park: it SHALL record its conversation and the parked calls in its own journal,
the parent SHALL record the run as parked, and the child actor SHALL stop. A parked call SHALL NOT run while the run is
parked. When every parked consent request of the run has an answer, the parent SHALL record the resume, recreate the
child from its journal, and run only the parked calls, each once. A refused call SHALL get a refusal result and SHALL
NOT run. The resumed child SHALL use the frozen system prompt and the frozen model of its first journal event. If the
frozen model no longer exists, the run SHALL end as lost with the reason `frozen_model_unavailable`, the parent notice
SHALL state the reason, and the parked call SHALL NOT run. The session SHALL NOT use another model.

#### Scenario: A live answer resumes the parked call once

- **GIVEN** a child parked at one consent request
- **WHEN** the operator approves once
- **THEN** the parent records the resume, recreates the child, and the parked call runs once

#### Scenario: Two parked calls wait for both answers

- **GIVEN** a child batch with two parked calls
- **WHEN** the operator approves the first call only
- **THEN** neither call runs
- **WHEN** the operator approves the second call
- **THEN** both calls run, each once

#### Scenario: Frozen model is gone

- **GIVEN** a parked child whose frozen model was removed from the configuration
- **WHEN** the operator approves the parked call
- **THEN** the run ends as lost with the reason `frozen_model_unavailable`
- **AND** the parked call does not run
- **AND** no model call uses another model

### Requirement: The parent purges a child journal last

When a child run ends, the system SHALL keep this order: the child records its final event; the parent records the
run end, writes `result.md`, and admits the result notice; then the parent purges the child journal and its
snapshots and records the purge. A lost run SHALL follow the same order without the child's final event. Recovery
SHALL purge the journal of each reported run that has no purge record. A purge SHALL be idempotent. `task.md`,
`result.md`, and the child log file SHALL stay.

#### Scenario: No purge before the notice

- **GIVEN** a child run ends
- **WHEN** the result notice is not yet admitted
- **THEN** the child journal still exists

#### Scenario: Recovery purges a leftover journal

- **GIVEN** a reported run whose purge did not complete before a crash
- **WHEN** the session recovers
- **THEN** the session purges the child journal and records the purge

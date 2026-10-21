## MODIFIED Requirements

### Requirement: Child execution keeps the parent's authority

The system SHALL preserve the original requester, audience, and authority boundary for child tools, approvals, status, and cancellation. A child result SHALL NOT grant authority. A child worker SHALL remain ephemeral and SHALL NOT restart an uncertain external action after owner recovery. A parked child run is not uncertain: nothing ran after its park. The owner SHALL recreate a parked run from its child journal only, under the authority of the run record and never under the authority of the current turn. Status SHALL distinguish durable acceptance from terminal completion. The run owner's status response SHALL include the pending approval count. Other requesters SHALL NOT see that count. An approval for a stale or unrelated run, call, requester, or attempt SHALL grant no authority.

The system SHALL persist cancellation intent before it acknowledges the intent. That acknowledgement SHALL NOT mean that the run is complete. The system SHALL report terminal cancellation only after the local dispatch confirms closure, and it SHALL start no new dispatch after closure. A parked run has no local dispatch, so its cancellation SHALL be terminal at once. An uncertain external effect SHALL remain explicit.

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

#### Scenario: Resumed child uses the authority of the run record

- **GIVEN** Alice's child is parked
- **AND** Bob's turn runs when Alice approves the parked call
- **WHEN** the owner recreates the child
- **THEN** the child runs with the authority of Alice's run record
- **AND** Bob's turn authority does not reach the child

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

## ADDED Requirements

### Requirement: The parent learns of child consent requests without a poll

When a child consent request opens, or when the last open request of a run owner closes, the parent session SHALL get
one approval notice for that owner. The session SHALL merge the events of one model boundary into one notice for each
run owner. When the session is idle, it SHALL admit a ready notice at once, and the notice SHALL start one turn. When a
turn runs or a compaction runs, the session SHALL admit the notice at the next tool-batch end, turn end, or compaction
end. A notice SHALL be ready when it holds a new request, or when no request of that owner stays open. An answer while
other requests of the owner stay open SHALL wait for the next ready notice. The notice SHALL name the agent, the run,
and the tool of each request of that owner only, and SHALL say that the operator answers in the channel.

#### Scenario: A fan-out gives one notice for each boundary

- **GIVEN** a parent turn runs a model call
- **WHEN** five children of Alice ask for consent during that model call
- **THEN** the session admits one approval notice at the next tool-batch end or turn end
- **AND** the notice lists the five requests

#### Scenario: An idle parent wakes once

- **GIVEN** the parent session is idle
- **WHEN** one child asks for consent
- **THEN** the session admits one approval notice and starts one turn

#### Scenario: The last close gives a closing notice

- **GIVEN** two open child consent requests of Alice
- **WHEN** the operator answers the first
- **THEN** no notice is admitted
- **WHEN** the operator answers the second
- **THEN** one closing notice lists both answers

### Requirement: An open child consent request pauses the session

While any child consent request of the session is open, the session SHALL be paused. While paused:

- the session SHALL refuse `spawn_agent`; the refusal text SHALL name only the waiting runs that the calling turn may
  control, SHALL give the count of the others, and SHALL tell the model to ask the operator to answer or to cancel;
- a child start from a person's slash command SHALL be accepted;
- an automated input and a child result notice SHALL NOT start a turn or join a running turn;
- an approval notice SHALL be admitted;
- the running turn SHALL be able to finish;
- a person's input SHALL start a turn and SHALL NOT end the pause.

The pause SHALL end only when every open child consent request closes by an answer, a refusal, or a withdrawal. No
child consent request SHALL expire. When the pause ends, the session SHALL admit the ready notices and then run the
waiting inputs.

#### Scenario: Spawn is refused while paused

- **GIVEN** a child consent request of Alice is open
- **WHEN** the parent model calls `spawn_agent` in Alice's turn
- **THEN** the call returns an error that names Alice's waiting runs
- **AND** no run folder stays

#### Scenario: Refusal text does not list another owner's runs

- **GIVEN** a child consent request of Alice is open
- **WHEN** the parent model calls `spawn_agent` in Bob's turn
- **THEN** the error gives the count of waiting requests
- **AND** it does not name Alice's runs

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

When a child run is cancelled, ends, or is lost while its consent request is open, or when its prompt cannot be
posted, the session SHALL close the request as withdrawn with a reason: cancelled by parent, child ended, child lost,
daemon restarted, or prompt unavailable. The session SHALL emit a prompt-closed output for each closed request. An
answer to a withdrawn request SHALL get the reason text and SHALL run nothing and grant nothing. No text SHALL name the
child run or the agent. A park that the owner refuses because the run was cancelled SHALL record the run as cancelled.

#### Scenario: Cancel withdraws the prompts

- **GIVEN** a child run with two open consent requests
- **WHEN** the parent cancels the run
- **THEN** the session emits two prompt-closed outputs with the reason "cancelled by parent"
- **AND** the closing notice reports both as withdrawn

#### Scenario: A live child stops unexpectedly

- **GIVEN** a live child with an open consent request
- **WHEN** the child actor stops with no terminal result
- **THEN** the request is withdrawn with the reason "child lost"
- **AND** the reply text says that the task stopped unexpectedly

### Requirement: A child run parks at consent and resumes from its journal

A child run that needs consent SHALL park: it SHALL record its conversation and the parked calls in its child journal,
the parent SHALL record the run as parked, and the child actor SHALL stop. A parked call SHALL NOT run while the run is
parked. The owner SHALL accept a park whose prompts already have answers. When every parked prompt already has an
answer, the owner SHALL record the park and the resume together and resume at once. When every parked prompt of the
run has an answer, the parent SHALL record the resume, recreate the child from its journal, and run only the parked
calls, each once. A refused call SHALL get a refusal result and SHALL NOT run. The parent SHALL NOT resume a run while a
restart drain is pending, and SHALL NOT start a new child incarnation before the old one stopped.

The resumed child SHALL use the frozen system prompt, provider, model, tool set, project instructions, operating
rules, and findings flag of its first journal event. If the frozen provider or model or the agent definition no longer
exists, the run SHALL end as lost with its reason, the parent notice SHALL state the reason, and the parked calls SHALL
NOT run. The session SHALL NOT use another model or provider. A frozen tool that no longer exists SHALL give an
explicit error result when the child calls it.

#### Scenario: A live answer resumes the parked call once

- **GIVEN** a child parked at one consent request
- **WHEN** the operator approves once
- **THEN** the parent records the resume, recreates the child, and the parked call runs once

#### Scenario: An answer before the park

- **GIVEN** a child batch with a parked call A and a slow sibling call B
- **WHEN** the operator approves A while B runs, and B then ends
- **THEN** the owner records the park and the resume together
- **AND** call A runs once and the run is not lost

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
- **AND** the parked calls do not run
- **AND** no model call uses another model

#### Scenario: Cancel during resume preparation

- **GIVEN** the parent prepares the resume of a parked run
- **WHEN** the operator cancels the run before the preparation ends
- **THEN** the parent records the run as cancelled
- **AND** no child incarnation starts

### Requirement: The parent purges a child journal last

A run that never parked SHALL have no child journal. For a run with a child journal, the system SHALL keep this order:
the child records its final event; the parent records the run end, writes `result.md`, and admits the result notice;
the child actor stops; then the parent writes a small closing event to the child journal, deletes all journal
messages, and records the purge. No conversation row SHALL stay after the purge. A lost run SHALL follow the same order
without the child's final event. Recovery SHALL purge the journal of each ended run that has a child journal and no
purge record. A purge SHALL be idempotent. `task.md`, `result.md`, and the child log file SHALL stay.

#### Scenario: No purge before the notice

- **GIVEN** a child run with a child journal ends
- **WHEN** the result notice is not yet admitted
- **THEN** the child journal still exists

#### Scenario: No conversation row stays after the purge

- **GIVEN** a child run with a child journal on the SQLite journal
- **WHEN** the purge completes
- **THEN** the only row that stays for the child persistence ID is the closing event

#### Scenario: Recovery purges a leftover journal

- **GIVEN** an ended run whose purge did not complete before a crash
- **WHEN** the session recovers
- **THEN** the session purges the child journal and records the purge

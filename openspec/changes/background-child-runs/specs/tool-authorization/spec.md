## RENAMED Requirements

- FROM: `TA-12 Subagent consent goes through the parent session`
- TO: `TA-12 Subagent approval prompts go through the parent session`

## MODIFIED Requirements

### Requirement: TA-12 Subagent approval prompts go through the parent session

A subagent SHALL request an approval prompt through its owner session with the existing computed policy request contract.
The prompt SHALL retain the original parent requester, inherited working scope, exact child call, authorization attempt, and accepted run correlation.
The child SHALL use the parent's chat authorization grants. Its private grants SHALL NOT reach the parent.

- Prompt lifecycle facts SHALL persist before display; the execution waiter remains run-local.
- Start-tool completion or a later ordinary parent message SHALL NOT abandon a live child prompt.
- A restart cannot resume an interrupted child. Its old prompt SHALL expire visibly under the loss contract.
- Cancellation or terminal loss SHALL settle the prompt. A late answer SHALL create no retry or authorization grant.
- A child without a safe parent bridge SHALL fail with `ToolExecutionFailed` rather than synthesize a requester.
- A one-time authorization grant SHALL cover only the exact prompted retry and remain subject to launch and run-liveness checks.
- Background acceptance and child text SHALL create no approval authority.

#### Scenario: Subagent prompt reaches the parent requester

- **GIVEN** a live child belongs to an interactive Personal parent run
- **WHEN** it requests a protected shell call
- **THEN** the parent channel displays an approval prompt for the original eligible requester
- **AND** a valid answer can resume the exact child call under normal authorization

#### Scenario: Subagent prompt expires after restart

- **GIVEN** an unanswered child prompt and an interrupted child after restart
- **WHEN** the original requester answers the old prompt
- **THEN** the system reports an expired prompt
- **AND** no child call or new authorization grant results

#### Scenario: Another speaker cannot answer the original prompt

- **GIVEN** a child prompt belongs to one original requester
- **WHEN** another speaker joins a later parent turn and answers that prompt
- **THEN** the answer cannot replace the recorded requester
- **AND** the protected child call remains unexecuted

#### Scenario: Start completion does not end a live prompt

- **GIVEN** the start call returned accepted and the child later requests approval
- **WHEN** the eligible requester answers through the parent
- **THEN** the run-owned wait remains available for exact retry authorization
- **AND** its lifetime does not depend on the completed start tool

## ADDED Requirements

### Requirement: TA-17 Child controls enforce parent session and original requester ownership

The deferred child control SHALL require an authorized parent invocation in the owning session and original eligible requester context.
It SHALL use the existing original authority record, rather than parallel audience, requester, or path-root fields.
It SHALL be unavailable to children through discovery, schema load, and direct dispatch.
Unauthorized requests SHALL reveal no target existence, state, task text, result paths, or requester details.
Cancellation SHALL acknowledge local cutover separately from admission and SHALL create no tool or path grant.
Child result locations SHALL remain data for ordinary file access, not access authority.

#### Scenario: Eligible parent cancels its child

- **GIVEN** an authorized parent invocation under the original eligible requester context
- **WHEN** it cancels its own accepted run
- **THEN** the owner admits cancellation and later reports local dispatch closure
- **AND** no broader shell or path authority results

#### Scenario: Child cannot cancel a sibling

- **GIVEN** a child knows a sibling run identifier
- **WHEN** it attempts direct child-control dispatch
- **THEN** the runtime denies the operation without target details
- **AND** the sibling remains live

#### Scenario: Known identifier does not authorize cross-session status

- **GIVEN** another session obtains a child identifier from text
- **WHEN** it requests status or cancellation
- **THEN** the runtime returns a non-disclosing denial
- **AND** no state mutation or result access occurs

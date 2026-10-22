## MODIFIED Requirements

### Requirement: TA-12 Subagent consent goes through the parent session

A subagent SHALL ask for consent through its parent session with the same
consent request contract, a parent-scoped correlation identifier, the parent
requester, and the inherited parent working directory. A subagent SHALL use
chat grants of its parent session, and its own chat grants SHALL NOT reach the
parent.

- A durably accepted child SHALL record its consent request and resolution
  with its original run, concrete call, requester, and authority.
- The parent SHALL preserve those facts while it serves another user turn.
- A child that waits for consent SHALL be parked in its run record. Its prompt
  SHALL stay valid across a daemon restart, a config reload, a snapshot, and
  passivation. An answer after a restart SHALL resume the parked call.
- The resumed call SHALL run under the requester, boundary, channel, and
  delivery targets of the run record, and in the working directories that its
  batch ran with. It SHALL NOT take them from the current turn or the current parent.
- The resumed call SHALL pass every gate check again under the current policy
  at call time. A deny rule that applies at resume SHALL win over the answer.
  The answer SHALL stay bound to the exact tool name and candidate keys of the
  prompted call, as in the parent re-drive.
- The resumed call SHALL run at most once. The session SHALL record the resume
  before the call runs. Recovery SHALL report uncertain child work after a
  recorded resume without a new child dispatch.
- A child that was running, and not parked, at a restart SHALL be recorded as
  lost, and recovery SHALL withdraw any open prompt of that child. The accepted
  start result SHALL remain distinct from the later terminal result.
- An answer for a prompt that already has an answer, or that was withdrawn,
  SHALL NOT run the tool.
- A subagent without a parent bridge SHALL NOT ask. Its run SHALL fail with
  `ToolExecutionFailed`.
- A one-time consent in a subagent SHALL cover only that exact call.

#### Scenario: Subagent prompt reaches the parent requester

- **GIVEN** a subagent spawned in an interactive Personal session
- **WHEN** the subagent calls `shell_execute` with an uncovered command
- **THEN** the parent channel shows a consent request for the parent requester
- **AND** the subagent resumes after the answer

#### Scenario: Parked subagent prompt survives a restart

- **GIVEN** an unanswered subagent consent request of a parked child and a daemon restart
- **WHEN** the requester answers the old prompt
- **THEN** the session resumes the child and the call runs once

#### Scenario: Subagent prompt expires after restart

- **GIVEN** an open subagent consent request of a child that was not parked, for example a request that an older
  binary recorded, and a daemon restart
- **WHEN** the requester answers the old prompt
- **THEN** Netclaw rejects the answer with the "Netclaw restarted" withdrawal text
- **AND** the tool does not run

#### Scenario: A deny rule added after the park wins

- **GIVEN** a parked child call `gh pr merge 42`
- **AND** the operator adds `gh pr merge` to the shell deny list and reloads
- **WHEN** the requester approves the parked call
- **THEN** the gate denies the call and the command does not run

#### Scenario: Another parent turn does not replace child approval authority

- **GIVEN** Alice starts a child that requests consent
- **AND** the parent accepts another user turn
- **WHEN** Alice answers the original child prompt
- **THEN** the child receives consent only for the original concrete call
- **AND** the prompt retains Alice's original authority and options

#### Scenario: Another requester cannot approve the child call

- **GIVEN** Alice owns a child consent request
- **WHEN** Bob answers that request
- **THEN** the system rejects the answer before a grant or tool dispatch

#### Scenario: A crash after the resume record does not repeat the call

- **GIVEN** the session recorded the resume of a parked child
- **WHEN** the daemon crashes before the child records its next checkpoint or end
- **THEN** recovery records the run as lost
- **AND** the parked call does not run again

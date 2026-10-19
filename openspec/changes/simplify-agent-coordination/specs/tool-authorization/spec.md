## MODIFIED Requirements

### Requirement: TA-11 An unanswered consent request survives restart

A parent-session consent request SHALL pause only its own tool call. Other
calls in the batch SHALL continue.

- The session SHALL journal `ToolApprovalRequested` with the turn context
  before it shows the prompt, and `ToolApprovalResolved` when the answer is
  accepted.
- No timer SHALL deny an unanswered request. The wait SHALL end only with an
  answer, a cancellation, or a new user message that abandons the parked
  batch.
- An unanswered request SHALL NOT keep the session in memory. After a daemon
  restart or passivation, an answer SHALL recover the session from its
  journal, apply the answer, and re-drive the parked batch once every sibling
  request has an answer.
- A re-drive SHALL use the persisted turn context and audience. It SHALL NOT
  execute a completed sibling call again.
- An answer for a call that already has an answer SHALL fail loud with reason
  `approval_already_answered` and an already-answered notice. It SHALL NOT run
  the tool and SHALL NOT grant authority. This SHALL hold during the turn,
  after the turn ends, and after a daemon restart or passivation.
- Netclaw SHALL treat a call as answered when the live workflow records an
  answer, or when session history holds a tool result for the call that is
  not a synthetic abandonment result.
- An answer for an unknown call, a call that recovery removed, or a call that
  an abandonment closed SHALL fail loud with reason `approval_prompt_expired`
  and an expired-prompt notice. It SHALL NOT run the tool.

#### Scenario: Answer after restart resumes the call

- **GIVEN** a journaled consent request for `shell_execute` and a daemon restart
- **WHEN** the requester selects `approve_once`
- **THEN** the session recovers, re-drives the parked batch, and runs the call once

#### Scenario: Duplicate answer does not run the tool again

- **GIVEN** a consent request that already has an accepted answer
- **WHEN** a second answer arrives for the same call, during the turn, after the turn ends, or after a restart
- **THEN** Netclaw replies `approval_already_answered` and posts an already-answered notice
- **AND** Netclaw does not post an expired-prompt notice
- **AND** the tool does not run again

#### Scenario: Answer for an unknown call expires

- **GIVEN** a session with no consent request for a call ID
- **WHEN** an answer arrives for that call ID
- **THEN** Netclaw replies `approval_prompt_expired` and posts an expired-prompt notice
- **AND** no tool runs

#### Scenario: Late answer to a superseded prompt expires

- **GIVEN** an unanswered consent request that a new user message abandoned
- **WHEN** the requester answers the old prompt
- **THEN** Netclaw replies `approval_prompt_expired` and posts an expired-prompt notice
- **AND** the tool does not run

### Requirement: TA-12 Subagent consent goes through the parent session

A subagent SHALL ask for consent through its parent session with the same
consent request contract, a parent-scoped correlation identifier, the parent
requester, and the inherited parent working directory. A subagent SHALL use
chat grants of its parent session, and its own chat grants SHALL NOT reach the
parent.

- A durably accepted child SHALL record its consent request and resolution with its original run, concrete call, requester, and authority.
- The parent SHALL preserve those facts while it serves another user turn.
- After a daemon or parent restart, Netclaw SHALL reject the old child prompt as expired.
  Recovery SHALL report uncertain child work without a new child dispatch.
  The accepted start result SHALL remain distinct from the later terminal result.
- An answer that arrives after the subagent wait ends SHALL NOT run the tool.
- A subagent without a parent bridge SHALL NOT ask. Its run SHALL fail with
  `ToolExecutionFailed`.
- A one-time consent in a subagent SHALL cover only that exact call.

#### Scenario: Subagent prompt reaches the parent requester

- **GIVEN** a subagent spawned in an interactive Personal session
- **WHEN** the subagent calls `shell_execute` with an uncovered command
- **THEN** the parent channel shows a consent request for the parent requester
- **AND** the subagent resumes after the answer

#### Scenario: Subagent prompt expires after restart

- **GIVEN** an unanswered subagent consent request and a daemon restart
- **WHEN** the requester answers the old prompt
- **THEN** Netclaw rejects the answer as expired
- **AND** the tool does not run

#### Scenario: Another parent turn does not replace child approval authority

- **GIVEN** Alice starts a child that requests consent
- **AND** the parent accepts another user turn
- **WHEN** Alice answers the original child prompt while its wait remains live
- **THEN** the child receives consent only for the original concrete call
- **AND** the prompt retains Alice's original authority and options

#### Scenario: Another requester cannot approve the child call

- **GIVEN** Alice owns a child consent request
- **WHEN** Bob answers that request
- **THEN** the system rejects the answer before a grant or tool dispatch

## MODIFIED Requirements

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

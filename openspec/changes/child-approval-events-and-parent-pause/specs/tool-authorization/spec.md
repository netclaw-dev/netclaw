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
- The prompt SHALL show only the runtime-rendered command and scope from the
  gate, with buttons only. No user-facing text SHALL name the child run or the
  agent: not the prompt, not the closed prompt, not the reply to an answer, and
  not headless output. The parent model SHALL receive the agent name and the
  run ID in its notice.
- The parent model SHALL NOT answer, approve, or change a consent request.
  Only an operator answer that the requester check accepts SHALL resolve it.
- A text answer SHALL resolve a prompt only when the sender can answer exactly
  one open prompt. With two or more, the session SHALL resolve none and SHALL
  tell the sender to use the buttons.
- When a child run is cancelled, ends, or is lost, when a child prompt cannot
  be posted, or when a child run that is not parked is lost at restart,
  Netclaw SHALL withdraw its open prompts. An answer to a withdrawn prompt
  SHALL run nothing and grant nothing. The reply SHALL state the true reason
  without the child identity.
- A child run that is parked at consent SHALL keep its consent requests open
  across a daemon restart, a configuration reload, and a parent snapshot. An
  answer after the restart SHALL resume the parked call once, under the
  authority of the run record. The gate SHALL check the call again under the
  current policy.
- After a daemon or parent restart, Netclaw SHALL withdraw the consent requests
  of a child run that is not parked. Recovery SHALL report uncertain child work
  without a new child dispatch. The accepted start result SHALL remain distinct
  from the later terminal result.
- An answer that arrives after the request closed SHALL NOT run the tool.
- A subagent without a parent bridge SHALL NOT ask. Its run SHALL fail with
  `ToolExecutionFailed`.
- A one-time consent in a subagent SHALL cover only that exact call.

#### Scenario: Subagent prompt reaches the parent requester

- **GIVEN** a subagent spawned in an interactive Personal session
- **WHEN** the subagent calls `shell_execute` with an uncovered command
- **THEN** the parent channel shows a consent request for the parent requester
- **AND** the prompt shows the command and scope with buttons and does not name the agent or the run
- **AND** the subagent resumes after the answer

#### Scenario: Parked subagent prompt survives a restart

- **GIVEN** a subagent parked at an unanswered consent request
- **AND** a parent snapshot that deleted the old journal rows
- **AND** a daemon restart or a configuration reload
- **WHEN** the requester answers the old prompt with "approve once"
- **THEN** the parked call runs once with consent for its exact candidates
- **AND** the gate checks the call under the current policy

#### Scenario: Subagent prompt expires after restart

- **GIVEN** an unanswered subagent consent request of a child run that is not parked
- **AND** a daemon restart
- **WHEN** the requester answers the old prompt
- **THEN** Netclaw replies "This approval request was withdrawn because Netclaw restarted before the task finished. Nothing ran."
- **AND** the tool does not run

#### Scenario: Answer to a prompt of a cancelled child

- **GIVEN** the parent cancels a child run with an open consent request
- **WHEN** the requester clicks the old prompt
- **THEN** Netclaw replies "This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran."
- **AND** the reply contains no run ID and no agent name
- **AND** no grant is written and no tool runs

#### Scenario: Child prompt that cannot be posted

- **GIVEN** a channel cannot post the buttons of a child prompt
- **WHEN** the binding reports that the prompt is unavailable
- **THEN** the session withdraws the request with the reason that Netclaw could not show it
- **AND** no "reply with" text prompt is posted for it

#### Scenario: Text answer with two open prompts

- **GIVEN** two open consent requests that Alice can answer
- **WHEN** Alice sends the text "A"
- **THEN** the session resolves neither request
- **AND** Alice gets a reply that tells her to use the buttons

#### Scenario: Parent model cannot approve

- **GIVEN** a child consent request is open
- **WHEN** the parent model writes "Reply A to approve" in the thread
- **THEN** the channel shows the text as model output with no buttons
- **AND** the request stays open

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

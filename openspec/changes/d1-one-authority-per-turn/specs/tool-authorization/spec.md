## MODIFIED Requirements

### Requirement: TA-11 An unanswered consent request survives restart

A parent-session consent request SHALL pause only its own tool call. Other
calls in the batch SHALL continue.

- The session SHALL journal `ToolApprovalRequested` with the turn context
  before it shows the prompt, and `ToolApprovalResolved` when the answer is
  accepted.
- No timer SHALL deny an unanswered request. The wait SHALL end only with an
  answer, a cancellation, or a new message from a person that abandons the parked
  batch.
- Automated input SHALL NOT abandon a parked batch. Automated input is input
  from a reminder, a child result, or a `SystemProcess` principal. It SHALL
  wait in order and SHALL run once as its own turn, under its own recorded
  authority, after the approval closes. This includes a close with no
  re-drive, such as an expired prompt.
- An unanswered request SHALL NOT keep the session in memory. After a daemon
  restart or passivation, an answer SHALL recover the session from its
  journal, apply the answer, and re-drive the parked batch once every sibling
  request has an answer.
- A re-drive SHALL use the persisted turn context and audience. It SHALL NOT
  execute a completed sibling call again.
- An answer for an unknown or already resolved call SHALL fail loud with an
  expired-prompt notice and SHALL NOT run the tool.

#### Scenario: Answer after restart resumes the call

- **GIVEN** a journaled consent request for `shell_execute` and a daemon restart
- **WHEN** the requester selects `approve_once`
- **THEN** the session recovers, re-drives the parked batch, and runs the call once

#### Scenario: Duplicate answer does not run the tool again

- **GIVEN** a consent request that already has an accepted answer
- **WHEN** a second answer arrives for the same call
- **THEN** Netclaw posts an expired-prompt notice
- **AND** the tool does not run again

#### Scenario: A reminder waits behind an open approval

- **GIVEN** a parked `shell_execute` consent request that no one answered
- **WHEN** a reminder fires for the session
- **THEN** the session keeps the parked batch and the consent request
- **AND** the reminder waits
- **WHEN** the requester approves and the batch finishes
- **THEN** the reminder runs once as its own turn

#### Scenario: A message from a person abandons the approval

- **GIVEN** a parked consent request that no one answered
- **AND** a reminder waits behind it
- **WHEN** the requester sends a new message
- **THEN** the session abandons the parked batch
- **AND** the new message runs first
- **AND** the waiting reminder runs once after that turn

#### Scenario: A child result does not abandon the approval

- **GIVEN** a parked consent request that no one answered
- **WHEN** a child result arrives for the session
- **THEN** the parked batch stays open
- **AND** the child result waits for its own turn after the approval closes

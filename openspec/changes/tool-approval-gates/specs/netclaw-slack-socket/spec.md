## MODIFIED Requirements

### Requirement: Socket Mode transport

Netclaw SHALL use Slack Socket Mode as the primary transport for inbound and
outbound message handling in MVP. The Slack channel SHALL register a
Socket Mode connection for message events and approval replies. No inbound HTTP
endpoint SHALL be required for interactive approval responses.

#### Scenario: Socket session established

- **GIVEN** valid Slack app and bot tokens are configured
- **WHEN** Netclaw starts
- **THEN** it opens a Socket Mode connection
- **AND** reports connection health in operator diagnostics

#### Scenario: Approval replies received via Socket Mode message events

- **GIVEN** an active Socket Mode connection
- **WHEN** a user replies `A`, `B`, or `C` to an approval prompt in the thread
- **THEN** the Slack channel receives the reply as a Slack message event via WebSocket
- **AND** no HTTP endpoint is required

### Requirement: Thread-bound reply delivery

Netclaw SHALL post assistant responses into the same Slack thread that produced
the session command.

#### Scenario: In-thread conversation

- **GIVEN** an allowed sender posts in thread `T`
- **WHEN** the turn completes
- **THEN** Netclaw posts the reply in thread `T`

### Requirement: No required inbound public webhook

Netclaw SHALL not require a public inbound HTTP endpoint for base Slack
transport operation, including interactive approval responses.

#### Scenario: Local-only runtime

- **GIVEN** Netclaw runs with loopback-only binding
- **WHEN** Slack Socket Mode is connected
- **THEN** Slack interaction still functions for inbound and outbound messaging
- **AND** approval text replies are received via Socket Mode

## ADDED Requirements

### Requirement: Approval prompt rendering via text reply flow

The Slack channel SHALL render `ToolInteractionRequest` outputs as in-thread
text prompts. For approval-type interactions, the prompt SHALL present four
reply options: `A` = Approve Once, `B` = Approve For This Chat, `C` = Approve
Always, and `D` = Deny. The
message SHALL include the tool name and a display of what the tool wants to do
(e.g., the shell command).

#### Scenario: Approval prompt posted with A/B/C/D text options

- **GIVEN** the session emits a `ToolInteractionRequest` with `Kind=approval`
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts a text message in the session's thread with the tool name,
  command, and A/B/C/D approval instructions

#### Scenario: Only requesting user may reply to approval prompt

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** a different Slack user replies with an approval choice
- **THEN** the reply is rejected
- **AND** Slack receives a visible warning that only the requesting user can approve the action

### Requirement: Slack text approval reply routing to session

The Slack channel SHALL route parsed text approval replies back to the
originating session as `ToolInteractionResponse` messages. Routing SHALL use the
pending request state held by the thread binding actor so the reply is matched
to the correct `CallId` and requester.

#### Scenario: User replies Approve Once

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `A`
- **THEN** the Slack channel parses the text reply against the pending approval request
- **AND** sends a `ToolInteractionResponse` with `ApprovedOnce` to the session

#### Scenario: User replies Approve For This Chat

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `B`
- **THEN** a `ToolInteractionResponse` with `ApprovedSession` is sent to the session
- **AND** the approval is retained only for the current Slack thread session

#### Scenario: User replies Approve Always

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `C`
- **THEN** a `ToolInteractionResponse` with `ApprovedAlways` is sent to the session
- **AND** the approval is persisted to `tool-approvals.json`

#### Scenario: User replies Deny

- **GIVEN** an approval prompt is displayed
- **WHEN** the user replies `D`
- **THEN** a `ToolInteractionResponse` with `Denied` is sent to the session
- **AND** the tool receives a denial result

#### Scenario: No pending approval means reply falls through as normal message

- **GIVEN** no approval request is pending for the Slack thread
- **WHEN** a user sends `A`, `B`, `C`, or `D`
- **THEN** the message is not treated as an approval response

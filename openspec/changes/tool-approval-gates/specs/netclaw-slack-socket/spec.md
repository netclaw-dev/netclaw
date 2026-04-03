## MODIFIED Requirements

### Requirement: Socket Mode transport

Netclaw SHALL use Slack Socket Mode as the primary transport for inbound and
outbound message handling in MVP. The Slack channel SHALL register a
`BlockAction` event handler to receive interactive responses (button clicks)
through the Socket Mode WebSocket connection. No inbound HTTP endpoint SHALL
be required for interactive responses.

#### Scenario: Socket session established

- **GIVEN** valid Slack app and bot tokens are configured
- **WHEN** Netclaw starts
- **THEN** it opens a Socket Mode connection
- **AND** reports connection health in operator diagnostics

#### Scenario: BlockAction events received via Socket Mode

- **GIVEN** an active Socket Mode connection
- **WHEN** a user clicks a Block Kit button in a Slack message
- **THEN** the Slack channel receives a `BlockAction` event via WebSocket
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
- **AND** approval button clicks are received via Socket Mode

## ADDED Requirements

### Requirement: Approval prompt rendering via Block Kit

The Slack channel SHALL render `ToolInteractionRequest` outputs as Block Kit
messages containing an `ActionsBlock` with labeled buttons. For approval-type
interactions, the buttons SHALL be: "Approve Once", "Approve Always", and
"Deny". The message SHALL include the tool name and a display of what the tool
wants to do (e.g., the shell command). The button `value` field SHALL encode
the `SessionId` and `CallId` for routing.

#### Scenario: Approval prompt posted with buttons

- **GIVEN** the session emits a `ToolInteractionRequest` with `Kind=approval`
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts a Block Kit message in the session's thread with:
  - A text section showing the tool name and command
  - An actions block with Approve Once, Approve Always, and Deny buttons
- **AND** each button's `value` contains the session ID and call ID

#### Scenario: Approval prompt for non-shell tool

- **GIVEN** the session emits a `ToolInteractionRequest` for an MCP tool
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts a Block Kit message showing the tool name and description
- **AND** the same approve/deny button layout is used

### Requirement: BlockAction routing to session

The Slack channel SHALL route `BlockAction` events from approval buttons back
to the originating session actor as `ToolInteractionResponse` messages. The
routing SHALL extract `SessionId` and `CallId` from the button `value` and
deliver the response through the actor hierarchy
(`SlackGatewayActor` → `SlackConversationActor` → session).

#### Scenario: User clicks Approve Once

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user clicks "Approve Once"
- **THEN** the Slack channel parses the `BlockAction` event
- **AND** extracts `SessionId` and `CallId` from the button value
- **AND** sends a `ToolInteractionResponse` with `ApprovedOnce` to the session

#### Scenario: User clicks Approve Always

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user clicks "Approve Always"
- **THEN** a `ToolInteractionResponse` with `ApprovedAlways` is sent to the session
- **AND** the approval is persisted to `tool-approvals.json`

#### Scenario: User clicks Deny

- **GIVEN** an approval prompt is displayed
- **WHEN** the user clicks "Deny"
- **THEN** a `ToolInteractionResponse` with `Denied` is sent to the session
- **AND** the tool receives a denial result

#### Scenario: Approval prompt from non-subscribed session ignored

- **GIVEN** a `BlockAction` event references a session that no longer exists
- **WHEN** the routing is attempted
- **THEN** the event is silently discarded (no error to Slack)

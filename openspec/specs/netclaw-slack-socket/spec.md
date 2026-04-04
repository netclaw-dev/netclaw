# netclaw-slack-socket Specification

## Purpose

Define Slack transport behavior for Netclaw MVP using Slack Socket Mode.

## Requirements

### Requirement: Socket Mode transport

Netclaw SHALL use Slack Socket Mode as the primary transport for inbound and
outbound message handling in MVP. The Slack channel SHALL register a
`BlockAction` event handler to receive interactive responses (button clicks)
through the Socket Mode WebSocket connection. No inbound HTTP endpoint SHALL
be required for interactive responses.

#### Scenario: Socket session established

- GIVEN valid Slack app and bot tokens are configured
- WHEN Netclaw starts
- THEN it opens a Socket Mode connection
- AND reports connection health in operator diagnostics

#### Scenario: BlockAction events received via Socket Mode

- **GIVEN** an active Socket Mode connection
- **WHEN** a user clicks a Block Kit button in a Slack message
- **THEN** the Slack channel receives a `BlockAction` event via WebSocket
- **AND** no HTTP endpoint is required

### Requirement: Thread-bound reply delivery

Netclaw SHALL post assistant responses into the same Slack thread that produced
the session command.

#### Scenario: In-thread conversation

- GIVEN an allowed sender posts in thread `T`
- WHEN the turn completes
- THEN Netclaw posts the reply in thread `T`

### Requirement: No required inbound public webhook

Netclaw SHALL not require a public inbound HTTP endpoint for base Slack
transport operation, including interactive approval responses.

#### Scenario: Local-only runtime

- GIVEN Netclaw runs with loopback-only binding
- WHEN Slack Socket Mode is connected
- THEN Slack interaction still functions for inbound and outbound messaging
- **AND** approval button clicks are received via Socket Mode

### Requirement: Approval prompt rendering via Block Kit

The Slack channel SHALL render `ToolInteractionRequest` outputs as approval
prompt messages in the session thread. The prompt SHALL include the tool name,
a description of what the tool wants to do, and available response options.
The channel SHALL support both Block Kit interactive buttons and text-based
ABC option lists as fallback rendering.

#### Scenario: Approval prompt posted in thread

- **GIVEN** the session emits a `ToolInteractionRequest` with `Kind=approval`
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts an approval prompt message in the session's thread
- **AND** the message shows the tool name, command, and response options

#### Scenario: Approval prompt for non-shell tool

- **GIVEN** the session emits a `ToolInteractionRequest` for an MCP tool
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts an approval prompt showing the tool name and description

### Requirement: Approval response routing to session

The Slack channel SHALL route approval responses back to the originating session
actor as `ToolInteractionResponse` messages. Responses MAY arrive via
`BlockAction` events (button clicks) or text message parsing (ABC options).

#### Scenario: User approves via text response

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies "A" or "approve once"
- **THEN** the Slack channel parses the response
- **AND** sends a `ToolInteractionResponse` with `approve_once` to the session

#### Scenario: Approval response from non-existent session ignored

- **GIVEN** an approval response references a session that no longer exists
- **WHEN** the routing is attempted
- **THEN** the event is silently discarded

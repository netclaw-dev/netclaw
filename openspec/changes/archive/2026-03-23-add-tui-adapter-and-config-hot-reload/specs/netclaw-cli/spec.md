## ADDED Requirements

### Requirement: Cocona command routing

The application SHALL use Cocona as the CLI command routing framework. All
commands SHALL be routed through Cocona's convention-based command model with
DI integration.

#### Scenario: Command routed through Cocona

- **WHEN** operator runs `netclaw <command> [args]`
- **THEN** Cocona routes to the matching command class
- **AND** DI-registered services are available to the command handler

### Requirement: TUI command classification

Commands SHALL be classified as either TUI-interactive (rendered via Termina)
or plain-CLI (standard console output). Only `netclaw init` and `netclaw chat`
SHALL use Termina TUI. All other commands SHALL use plain console output.

#### Scenario: TUI command launches Termina

- **WHEN** operator runs `netclaw chat` or `netclaw init`
- **THEN** the command handler launches Termina as a hosted service
- **AND** the TUI renders interactive components

#### Scenario: Plain CLI command uses console output

- **WHEN** operator runs `netclaw doctor` or any non-TUI command
- **THEN** the command handler writes to standard output
- **AND** no Termina TUI is launched

### Requirement: Interactive chat command

The CLI SHALL provide `netclaw chat` as an interactive agent prompt that hosts
the full actor system in-process. The chat command SHALL use the TUI adapter
to produce `SendUserMessage` commands with entity key `tui/{sessionId}`.

#### Scenario: Start chat session

- **WHEN** operator runs `netclaw chat`
- **THEN** the actor system starts in-process
- **AND** a TUI chat interface is rendered with input panel and message history
- **AND** a new session with entity key `tui/{uuid}` is created

#### Scenario: Send message in chat

- **GIVEN** a chat session is active
- **WHEN** operator types a message and presses Enter
- **THEN** a `SendUserMessage` command is dispatched to the session parent
- **AND** the response streams into the chat history via StreamingTextNode

#### Scenario: Tool activity displayed inline

- **GIVEN** a chat session is processing a turn with tool calls
- **WHEN** tools are invoked during the turn
- **THEN** a tool activity panel appears inline showing tool name, status, and
  duration
- **AND** completed tools show checkmark with duration
- **AND** in-progress tools show spinner

#### Scenario: MCP status displayed in status bar

- **GIVEN** MCP servers are configured
- **WHEN** the chat TUI is active
- **THEN** the status bar shows MCP connectivity status
- **AND** green indicates all servers connected
- **AND** yellow indicates degraded connectivity
- **AND** red indicates servers unreachable

### Requirement: Daemon entry point

The CLI SHALL provide `netclaw run` as the explicit daemon entry point. The
daemon SHALL start the Slack Socket Mode adapter, Akka actor system, scheduled
task timers, and health endpoints. The daemon SHALL NOT render a TUI.

#### Scenario: Start daemon mode

- **WHEN** operator runs `netclaw run`
- **THEN** the Slack Socket Mode adapter connects
- **AND** the Akka actor system starts
- **AND** scheduled task timers are registered
- **AND** health endpoints are available
- **AND** no TUI is rendered

#### Scenario: Daemon logs to console

- **GIVEN** the daemon is running
- **WHEN** events occur (messages, tool calls, errors)
- **THEN** events are logged to console and/or configured log output
- **AND** no interactive input is expected

### Requirement: Doctor command

The CLI SHALL provide `netclaw doctor` as a plain CLI command that runs startup
checks and reports results with remediation guidance. The doctor command SHALL
exit with code 0 (all pass), 1 (errors), or 2 (warnings only).

#### Scenario: All checks pass

- **WHEN** operator runs `netclaw doctor`
- **AND** all startup checks pass
- **THEN** output shows checkmarks for each check
- **AND** exit code is 0

#### Scenario: Check fails with remediation

- **WHEN** operator runs `netclaw doctor`
- **AND** a startup check fails
- **THEN** output shows the failure with a remediation command
- **AND** exit code is 1

## MODIFIED Requirements

### Requirement: Interactive chat command

The CLI SHALL provide `netclaw chat` as an interactive agent prompt that
connects to the daemon via SignalR. The chat command SHALL support an optional
`--resume <session-id>` flag to attach to an existing session instead of
creating a new one.

#### Scenario: Start chat session

- **WHEN** operator runs `netclaw chat`
- **THEN** a SignalR connection is established to the daemon
- **AND** a TUI chat interface is rendered with input panel and message history
- **AND** a new session is created via `EnsureSession`

#### Scenario: Send message in chat

- **GIVEN** a chat session is active
- **WHEN** operator types a message and presses Enter
- **THEN** a `SendMessage` call is dispatched via SignalR
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

#### Scenario: Resume existing session via flag

- **WHEN** operator runs `netclaw chat --resume <session-id>`
- **THEN** a SignalR connection is established to the daemon
- **AND** the chat page attaches to the specified session via `EnsureSession`
- **AND** a "Resumed" indicator is shown

### Requirement: TUI command classification

Commands SHALL be classified as either TUI-interactive (rendered via Termina)
or plain-CLI (standard console output). `netclaw init`, `netclaw chat`, and
`netclaw sessions` SHALL use Termina TUI. All other commands SHALL use plain
console output.

#### Scenario: TUI command launches Termina

- **WHEN** operator runs `netclaw chat`, `netclaw init`, or `netclaw sessions`
- **THEN** the command handler launches Termina as a hosted service
- **AND** the TUI renders interactive components

#### Scenario: Plain CLI command uses console output

- **WHEN** operator runs `netclaw doctor` or any non-TUI command
- **THEN** the command handler writes to standard output
- **AND** no Termina TUI is launched

## ADDED Requirements

### Requirement: Session browser command

The CLI SHALL provide `netclaw sessions` as a TUI command that displays recent
sessions and allows the user to select one to resume.

#### Scenario: Launch session browser

- **WHEN** operator runs `netclaw sessions`
- **THEN** the TUI displays a list of recent sessions from the daemon catalog
- **AND** daemon connectivity is required (fails with helpful error if daemon
  is not running)

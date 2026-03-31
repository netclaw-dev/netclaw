# netclaw-cli Specification

## Purpose

Define command-line management behavior for onboarding, validation, and
diagnostics.

## Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding
wizard SHALL collect Slack credentials, provider configuration, ACL inputs,
MCP server configuration, and exposure mode selection. On completion, the
wizard SHALL run a health check to verify the baseline configuration is
functional.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, MCP, and exposure mode
  inputs
- **AND** writes a runnable baseline configuration

#### Scenario: MCP server configured during init

- **WHEN** onboarding reaches the MCP step
- **THEN** the wizard prompts for at least one MCP server profile (Memorizer
  recommended)
- **AND** validates server handshake before proceeding

#### Scenario: Exposure mode selected during init

- **WHEN** onboarding reaches the exposure step
- **THEN** the wizard presents available exposure modes (local, tailscale-serve,
  tailscale-funnel, cloudflare-tunnel)
- **AND** applies security warnings for public modes

#### Scenario: Health check on completion

- **WHEN** onboarding completes all steps
- **THEN** the wizard runs a health check covering Slack connectivity, provider
  validation, and MCP server reachability
- **AND** reports pass/fail for each component

### Requirement: Resumable onboarding

The CLI SHALL support resuming incomplete onboarding.

#### Scenario: Resume setup

- **GIVEN** onboarding is incomplete
- **WHEN** operator runs `netclaw init --resume`
- **THEN** setup continues from first incomplete step

### Requirement: Config and ACL validation

The CLI SHALL validate configuration and return actionable errors.

#### Scenario: Validation failure

- **WHEN** config validation fails
- **THEN** command exits non-zero
- **AND** output includes remediation guidance

### Requirement: Security diagnostics

The CLI SHALL report exposure mode and policy health.

#### Scenario: Doctor output

- **WHEN** operator runs `netclaw doctor`
- **THEN** output includes exposure mode, policy status, and prioritized issues

### Requirement: Optional smoke test command

The CLI SHALL expose an explicit smoke-test command for live provider checks.

#### Scenario: Run Ollama smoke test

- **WHEN** operator runs `netclaw test smoke --provider ollama`
- **THEN** CLI executes provider connectivity smoke checks
- **AND** outputs a concise pass/fail report

### Requirement: Project management commands

The CLI SHALL provide `netclaw project list|add|remove` commands for managing
the project registry. Projects represent registered repositories with their
paths, capabilities, and associated AGENTS.md files.

#### Scenario: List registered projects

- **WHEN** operator runs `netclaw project list`
- **THEN** output displays all registered projects with paths and capabilities

#### Scenario: Add a project

- **WHEN** operator runs `netclaw project add --path /home/user/repos/myproject`
- **THEN** the project is added to the project registry
- **AND** the system scans for an AGENTS.md file in the project root

#### Scenario: Remove a project

- **GIVEN** a project is registered
- **WHEN** operator runs `netclaw project remove myproject`
- **THEN** the project is removed from the registry

### Requirement: Environment discovery command

The CLI SHALL provide `netclaw environment scan|show` commands for discovering
and displaying the capability inventory of the host environment.

#### Scenario: Scan environment

- **WHEN** operator runs `netclaw environment scan`
- **THEN** the system discovers installed tools (git, gh, claude, opencode,
  dotnet), git credentials, MCP server reachability, and host capabilities
- **AND** writes the inventory to the environment inventory file

#### Scenario: Show environment

- **WHEN** operator runs `netclaw environment show`
- **THEN** output displays the current environment inventory with tool
  availability, credential status, and capability details

### Requirement: Memory display command

The CLI SHALL provide `netclaw memory show` for displaying the contents of
agent memory files (personality, project registry, environment inventory).

#### Scenario: Show agent memory

- **WHEN** operator runs `netclaw memory show`
- **THEN** output displays the contents of personality files, project registry,
  and environment inventory in a readable format

#### Scenario: Show specific memory category

- **WHEN** operator runs `netclaw memory show --category personality`
- **THEN** output displays only the personality/soul files

### Requirement: Schedule management commands

The CLI SHALL provide `netclaw schedule list|show|pause|resume|delete` commands
for managing scheduled tasks.

#### Scenario: List scheduled tasks

- **WHEN** operator runs `netclaw schedule list`
- **THEN** output displays all scheduled tasks with name, schedule, status, and
  last execution result

#### Scenario: Show scheduled task details

- **WHEN** operator runs `netclaw schedule show my-task`
- **THEN** output displays the full task definition including schedule, required
  tool grants, instructions, and execution history

#### Scenario: Pause a scheduled task

- **GIVEN** a scheduled task is active
- **WHEN** operator runs `netclaw schedule pause my-task`
- **THEN** the task is paused and will not execute until resumed

#### Scenario: Resume a paused task

- **GIVEN** a scheduled task is paused
- **WHEN** operator runs `netclaw schedule resume my-task`
- **THEN** the task is reactivated and will execute on its next scheduled time

#### Scenario: Delete a scheduled task

- **GIVEN** a scheduled task exists
- **WHEN** operator runs `netclaw schedule delete my-task`
- **THEN** the task is permanently removed from the schedule registry

### Requirement: Personality reset command

The CLI SHALL provide `netclaw personality reset` to delete existing personality
files and re-trigger the conversational personality bootstrap on the next
conversation.

#### Scenario: Reset personality

- **WHEN** operator runs `netclaw personality reset`
- **THEN** existing personality/soul files are deleted
- **AND** the next conversation triggers the conversational personality bootstrap

#### Scenario: Reset confirmation

- **WHEN** operator runs `netclaw personality reset`
- **THEN** the CLI requires explicit confirmation before deleting personality
  files

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

### Requirement: Session browser command

The CLI SHALL provide `netclaw sessions` as a TUI command that displays recent
sessions and allows the user to select one to resume.

#### Scenario: Launch session browser

- **WHEN** operator runs `netclaw sessions`
- **THEN** the TUI displays a list of recent sessions from the daemon catalog
- **AND** daemon connectivity is required (fails with helpful error if daemon
  is not running)

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

### Requirement: Memory provider in status output

The `netclaw status` command SHALL display the active memory provider and
its health status.

#### Scenario: Status shows memory provider

- **WHEN** the operator runs `netclaw status`
- **THEN** the output includes a `memory:` line showing:
  - Provider name (`files` or `memorizer`)
  - Health status (`healthy`, `degraded`, or `unavailable`)
  - For Memorizer: endpoint URL and tool count
  - For files: memory count and index path

### Requirement: Signed manifest verification during update

The `netclaw update` command SHALL verify the minisign signature of
`manifest.json` before trusting its contents. The command SHALL download
`manifest.json.sig` alongside the manifest and verify the Ed25519 signature
against the embedded public key. The command SHALL reject the manifest and abort
the update if signature verification fails.

#### Scenario: Successful update with valid signature

- **WHEN** operator runs `netclaw update`
- **AND** the manifest signature verifies against the embedded public key
- **THEN** the update proceeds normally using the verified manifest checksums

#### Scenario: Update aborted on invalid signature

- **WHEN** operator runs `netclaw update`
- **AND** the manifest signature does not verify
- **THEN** the command exits with a non-zero code
- **AND** an error message warns of possible manifest tampering

#### Scenario: Update aborted when signature file missing

- **WHEN** operator runs `netclaw update`
- **AND** `manifest.json.sig` cannot be downloaded
- **THEN** the command exits with a non-zero code
- **AND** an error message explains the signature file is missing

### Requirement: Periodic daemon update check

The daemon SHALL periodically recheck for available updates while running.
The default recheck interval SHALL be 24 hours. The recheck SHALL use the same
`UpdateCheckService` and signature verification as the CLI update command.

#### Scenario: Daemon detects update after startup

- **GIVEN** the daemon started with no update available
- **WHEN** a new release is published and 24 hours elapse
- **THEN** the daemon detects the available update on the next periodic check

#### Scenario: Recheck interval respects cache

- **GIVEN** the update check cache duration is 1 hour
- **WHEN** the periodic timer fires at the 24-hour interval
- **THEN** a fresh manifest fetch is performed (cache has long expired)

### Requirement: Update availability operational alert

The daemon SHALL emit an `UpdateAvailable` operational alert via
`IOperationalNotificationSink` when an update is detected. The alert SHALL
be emitted at most once per detected version (deduplicated by the existing
webhook deduplication mechanism).

#### Scenario: Alert emitted on update detection

- **GIVEN** the daemon detects an available update
- **WHEN** the update check result indicates `IsUpdateAvailable`
- **THEN** an `UpdateAvailable` operational alert is emitted with severity
  "info"
- **AND** the alert summary includes the current and available versions

#### Scenario: Alert delivered to configured webhooks

- **GIVEN** a Slack webhook is configured in notifications config
- **WHEN** an `UpdateAvailable` alert is emitted
- **THEN** the webhook receives a notification formatted per the webhook format
  (Generic JSON or Slack Block Kit)

#### Scenario: Alert not duplicated within dedup window

- **GIVEN** an `UpdateAvailable` alert was recently emitted for the same version
- **WHEN** the periodic recheck runs again within the deduplication window
- **THEN** no duplicate alert is emitted

<!-- Delta from 2026-03-24 skill platform + security TUI changes -->
## ADDED Requirements

### Requirement: Signed manifest verification during update

The `netclaw update` command SHALL verify the minisign signature of
`manifest.json` before trusting its contents. The command SHALL download
`manifest.json.sig` alongside the manifest and verify the Ed25519 signature
against the embedded public key. The command SHALL reject the manifest and abort
the update if signature verification fails.

#### Scenario: Successful update with valid signature

- **WHEN** operator runs `netclaw update`
- **AND** the manifest signature verifies against the embedded public key
- **THEN** the update proceeds normally using the verified manifest checksums

#### Scenario: Update aborted on invalid signature

- **WHEN** operator runs `netclaw update`
- **AND** the manifest signature does not verify
- **THEN** the command exits with a non-zero code
- **AND** an error message warns of possible manifest tampering

#### Scenario: Update aborted when signature file missing

- **WHEN** operator runs `netclaw update`
- **AND** `manifest.json.sig` cannot be downloaded
- **THEN** the command exits with a non-zero code
- **AND** an error message explains the signature file is missing

### Requirement: Periodic daemon update check

The daemon SHALL periodically recheck for available updates while running.
The default recheck interval SHALL be 24 hours. The recheck SHALL use the same
`UpdateCheckService` and signature verification as the CLI update command.

#### Scenario: Daemon detects update after startup

- **GIVEN** the daemon started with no update available
- **WHEN** a new release is published and 24 hours elapse
- **THEN** the daemon detects the available update on the next periodic check

#### Scenario: Recheck interval respects cache

- **GIVEN** the update check cache duration is 1 hour
- **WHEN** the periodic timer fires at the 24-hour interval
- **THEN** a fresh manifest fetch is performed (cache has long expired)

### Requirement: Update availability operational alert

The daemon SHALL emit an `UpdateAvailable` operational alert via
`IOperationalNotificationSink` when an update is detected. The alert SHALL
be emitted at most once per detected version (deduplicated by the existing
webhook deduplication mechanism).

#### Scenario: Alert emitted on update detection

- **GIVEN** the daemon detects an available update
- **WHEN** the update check result indicates `IsUpdateAvailable`
- **THEN** an `UpdateAvailable` operational alert is emitted with severity
  "info"
- **AND** the alert summary includes the current and available versions

#### Scenario: Alert delivered to configured webhooks

- **GIVEN** a Slack webhook is configured in notifications config
- **WHEN** an `UpdateAvailable` alert is emitted
- **THEN** the webhook receives a notification formatted per the webhook format
  (Generic JSON or Slack Block Kit)

#### Scenario: Alert not duplicated within dedup window

- **GIVEN** an `UpdateAvailable` alert was recently emitted for the same version
- **WHEN** the periodic recheck runs again within the deduplication window
- **THEN** no duplicate alert is emitted

### Requirement: MCP tool permissions CLI

The system SHALL provide a `netclaw mcp tools` subcommand for viewing and
managing per-server tool grants across audience profiles.

#### Scenario: List tools for a server

- **GIVEN** the daemon is running and `memorizer` is connected
- **WHEN** operator runs `netclaw mcp tools memorizer`
- **THEN** the CLI displays all discovered tools from `memorizer`
- **AND** each tool shows its grant status per audience (Public, Team, Personal)
- **AND** tools not granted to any audience are visually distinguished

#### Scenario: List tools when daemon is unavailable

- **GIVEN** the daemon is not running
- **WHEN** operator runs `netclaw mcp tools memorizer`
- **THEN** the CLI reports that tool discovery requires the daemon
- **AND** exits with a non-zero exit code

#### Scenario: Snapshot current tools as grants

- **GIVEN** the daemon is running and `memorizer` exposes 5 tools
- **WHEN** operator runs `netclaw mcp tools memorizer --snapshot`
- **THEN** the CLI populates `McpServerToolGrants` for all audience profiles that allow `memorizer`
- **AND** each profile's grant list contains all 5 currently discovered tool names
- **AND** the updated config is written to `netclaw.json`

#### Scenario: Help for tools subcommand

- **WHEN** operator runs `netclaw mcp tools --help`
- **THEN** the CLI displays usage, subcommand description, and available flags

### Requirement: MCP tool permissions TUI

The system SHALL provide an interactive TUI mode for `netclaw mcp tools`
(invoked without a server name argument) that allows operators to browse
servers, view discovered tools, and toggle per-tool grants per audience.

#### Scenario: Launch TUI without arguments

- **GIVEN** the daemon is running with MCP servers connected
- **WHEN** operator runs `netclaw mcp tools` (no server name)
- **THEN** the TUI launches showing a list of configured MCP servers

#### Scenario: Browse tools for a server

- **GIVEN** the TUI is showing the server list
- **WHEN** operator selects a server
- **THEN** the TUI shows all discovered tools for that server
- **AND** each tool shows its current grant status for the selected audience

#### Scenario: Cycle audience in TUI

- **GIVEN** the TUI is showing tools for a server
- **WHEN** operator presses left/right arrow to cycle audience
- **THEN** the tool grant checkboxes update to reflect the selected audience's grants

#### Scenario: Toggle tool grant in TUI

- **GIVEN** the TUI is showing tools for a server under the Team audience
- **WHEN** operator toggles a tool's checkbox
- **THEN** the tool is added to or removed from the Team profile's `McpServerToolGrants` for this server

#### Scenario: Toggle server access in TUI

- **GIVEN** the TUI is showing tools for a server not allowed for the Team audience
- **WHEN** operator presses the enable/disable key
- **THEN** the server is added to the Team profile's `AllowedMcpServers`
- **AND** all tools start unchecked (secure by default)

#### Scenario: Save changes from TUI

- **GIVEN** the operator has toggled tool grants or server access in the TUI
- **WHEN** operator presses the save key
- **THEN** the updated `AllowedMcpServers` and `McpServerToolGrants` are written to `netclaw.json`
- **AND** the TUI confirms the save

### Requirement: MCP doctor advisory for ungated servers

The `netclaw doctor` command SHALL include an advisory check for MCP servers
that have no `McpServerToolGrants` configured on any audience profile.

#### Scenario: Server with no tool grants triggers advisory

- **GIVEN** `memorizer` is enabled and connected
- **AND** no audience profile has `McpServerToolGrants` entries for `memorizer`
- **WHEN** operator runs `netclaw doctor`
- **THEN** an info-level advisory is reported for `memorizer`
- **AND** the message suggests adding tool grants for supply-chain protection

#### Scenario: Server with tool grants passes advisory

- **GIVEN** `memorizer` has `McpServerToolGrants` on at least one audience profile
- **WHEN** operator runs `netclaw doctor`
- **THEN** no tool grant advisory is reported for `memorizer`

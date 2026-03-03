## MODIFIED Requirements

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
  validation, persistence connectivity, and MCP server reachability
- **AND** reports pass/fail for each component

## ADDED Requirements

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

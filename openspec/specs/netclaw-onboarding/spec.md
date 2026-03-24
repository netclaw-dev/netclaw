# netclaw-onboarding Specification

## Purpose

Define first-run and resumable onboarding experience for Netclaw operators.

## Requirements

### Requirement: Stepwise setup wizard

The system SHALL guide operators through setup steps with validation at each
step.

#### Scenario: Step progression

- **WHEN** operator completes a step successfully
- **THEN** onboarding advances to the next step

### Requirement: Secret-safe input handling

The system SHALL avoid echoing sensitive credentials in plain text output.

#### Scenario: Entering provider key

- **WHEN** operator enters a provider API key
- **THEN** the input is masked and not logged in clear text

### Requirement: Security warnings for public modes

The system SHALL show explicit warnings before enabling public exposure modes.

#### Scenario: Enable funnel mode

- **WHEN** operator selects `tailscale-funnel`
- **THEN** onboarding requires explicit confirmation and auth policy validation

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding
wizard SHALL collect Slack credentials, provider configuration, ACL inputs,
search backend, browser automation, memory provider selection, MCP server
configuration, and exposure mode selection. On completion, the wizard SHALL
run a health check to verify the baseline configuration is functional.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, search, browser
  automation, memory, and exposure mode inputs
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
  validation, memory backend reachability (if Memorizer), and MCP server
  reachability
- **AND** reports pass/fail/degraded for each component

#### Scenario: Health check reports degraded Memorizer

- **GIVEN** the operator configured `Memory.Provider = "memorizer"`
- **WHEN** the health check runs
- **AND** the Memorizer MCP server is unreachable
- **THEN** the health check reports a warning (degraded, not failed)
- **AND** displays "Memorizer unreachable — memory will use local files"

### Requirement: Phase 2 conversational personality bootstrap

The system SHALL trigger a conversational personality bootstrap on the first
`netclaw chat` session if personality files (PERSONALITY.md, INSTRUCTIONS.md,
USER.md) do not exist. The bootstrap conversation SHALL ask the operator about
communication preferences, tone, name preferences, and working style, then
write the resulting soul files to the standard config directory.

#### Scenario: First conversation triggers bootstrap

- **GIVEN** no personality files exist in the config directory
- **WHEN** the operator starts their first `netclaw chat` session
- **THEN** the agent initiates a personality bootstrap conversation
- **AND** asks about communication preferences and working style

#### Scenario: Bootstrap writes soul files

- **GIVEN** the personality bootstrap conversation is complete
- **WHEN** the operator has answered all preference questions
- **THEN** the system writes PERSONALITY.md, INSTRUCTIONS.md, and USER.md to
  the config directory

#### Scenario: Bootstrap skipped when files exist

- **GIVEN** personality files already exist in the config directory
- **WHEN** a new conversation starts
- **THEN** no personality bootstrap is triggered
- **AND** the existing personality files are loaded normally

### Requirement: Environment discovery during onboarding

The system SHALL scan for installed tools and host capabilities as part of
Phase 2 onboarding. Discovery results SHALL be persisted to the environment
inventory file for use in session context and capability self-awareness.

#### Scenario: Tool discovery during onboarding

- **WHEN** Phase 2 onboarding runs environment discovery
- **THEN** the system scans for installed tools (git, gh, claude, opencode,
  dotnet, node)
- **AND** checks git credential status
- **AND** writes results to the environment inventory file

#### Scenario: MCP server reachability check during onboarding

- **GIVEN** MCP servers are configured
- **WHEN** Phase 2 onboarding runs environment discovery
- **THEN** the system checks reachability of each configured MCP server
- **AND** records reachability status in the environment inventory

### Requirement: Project registration during onboarding

The system SHALL ask the operator about repositories to register as part of
Phase 2 onboarding. Registered projects are added to the project registry
with their paths, capabilities, and AGENTS.md locations.

#### Scenario: Register projects during onboarding

- **WHEN** Phase 2 onboarding reaches the project registration step
- **THEN** the system asks the operator about repositories to register
- **AND** scans provided paths for AGENTS.md files

#### Scenario: Skip project registration

- **WHEN** Phase 2 onboarding reaches the project registration step
- **AND** the operator indicates no projects to register
- **THEN** onboarding proceeds with an empty project registry

### Requirement: Memory provider selection during onboarding

The init wizard SHALL include a Memory step (step 6, after BrowserAutomation)
that allows operators to choose between "Local files" (default) and
"Memorizer" as the cross-session memory backend. The step SHALL always render
and SHALL NOT be conditionally skipped. `TotalSteps` SHALL be 9.

#### Scenario: Operator selects local files

- **WHEN** the wizard reaches the Memory step
- **AND** the operator selects "Local files (default)"
- **THEN** the wizard writes `"Memory": { "Provider": "files" }` to
  `netclaw.json`
- **AND** advances to the next step without further substeps

#### Scenario: Operator selects Memorizer

- **WHEN** the wizard reaches the Memory step
- **AND** the operator selects "Memorizer"
- **THEN** the wizard advances to the Memorizer connection substep

#### Scenario: Default selection is local files

- **WHEN** the wizard reaches the Memory step
- **THEN** "Local files (default)" is pre-selected

### Requirement: Memorizer MCP connection configuration

When the operator selects Memorizer, the wizard SHALL collect MCP server
connection details: transport type (stdio or http) and the corresponding
connection parameters (URL for http, command + arguments for stdio). The
wizard SHALL write both `Memory.Provider` and a `McpServers.memorizer` entry
to `netclaw.json`.

#### Scenario: Configure HTTP transport

- **GIVEN** the operator selected Memorizer
- **WHEN** the wizard reaches the connection substep
- **AND** the operator selects "HTTP" transport and enters a URL
- **THEN** the wizard writes `"McpServers": { "memorizer": { "Transport": "http", "Url": "<url>", "Enabled": true } }`

#### Scenario: Configure stdio transport

- **GIVEN** the operator selected Memorizer
- **WHEN** the wizard reaches the connection substep
- **AND** the operator selects "stdio" transport and enters command + arguments
- **THEN** the wizard writes the corresponding stdio MCP server entry

### Requirement: Memorizer connectivity validation during onboarding

After collecting Memorizer connection details, the wizard SHALL probe the
configured endpoint to validate connectivity. The probe SHALL use a 10-second
timeout. On failure, the wizard SHALL offer retry or fallback to local files.

#### Scenario: Successful connectivity probe

- **GIVEN** the operator entered Memorizer connection details
- **WHEN** the wizard probes the endpoint
- **AND** the endpoint responds within 10 seconds
- **THEN** the wizard shows a success message
- **AND** advances to the next step

#### Scenario: Failed connectivity probe with retry

- **GIVEN** the operator entered Memorizer connection details
- **WHEN** the wizard probes the endpoint
- **AND** the endpoint does not respond within 10 seconds
- **THEN** the wizard shows the error
- **AND** offers "Retry" or "Fall back to local files"

#### Scenario: Fallback to local files after probe failure

- **GIVEN** the Memorizer connectivity probe failed
- **WHEN** the operator selects "Fall back to local files"
- **THEN** the wizard sets `Memory.Provider` to `"files"`
- **AND** removes the `McpServers.memorizer` entry
- **AND** advances to the next step

### Requirement: TUI wizard delivery mechanism

The `netclaw init` onboarding wizard SHALL be delivered through Termina TUI
as an interactive 9-step wizard with progress indication, validation, and
back-navigation.

#### Scenario: Wizard renders in TUI

- **WHEN** operator runs `netclaw init`
- **THEN** a Termina TUI application launches
- **AND** the wizard displays step progress (e.g., "Step 2 of 9")
- **AND** the wizard displays a progress bar

#### Scenario: Step-specific components rendered

- **GIVEN** the wizard is on a step requiring text input
- **WHEN** the step is displayed
- **THEN** the wizard renders TextInputNode components for text/secret fields
- **AND** renders SelectionListNode components for choice fields

#### Scenario: Back navigation between steps

- **GIVEN** the wizard is on step 3
- **WHEN** the operator presses Esc
- **THEN** the wizard navigates back to step 2
- **AND** previous input values are preserved

#### Scenario: Live validation during wizard

- **GIVEN** the wizard is on the Memory step with Memorizer selected
- **WHEN** the operator enters connection details
- **THEN** the wizard validates connectivity with a SpinnerNode
- **AND** displays success or failure before allowing progression

<!-- Delta from 2026-03-24 skill platform + security TUI changes -->
## ADDED Requirements

### Requirement: Phase 2 conversational personality bootstrap

The system SHALL trigger a conversational personality bootstrap on the first
conversation if personality files (PERSONALITY.md, INSTRUCTIONS.md, USER.md)
do not exist. The bootstrap conversation SHALL ask the operator about
communication preferences, tone, name preferences, and working style, then
write the resulting soul files to the standard config directory.

#### Scenario: First conversation triggers bootstrap

- **GIVEN** no personality files exist in the config directory
- **WHEN** the operator starts their first conversation with Netclaw
- **THEN** the agent initiates a personality bootstrap conversation
- **AND** asks about communication preferences and working style

#### Scenario: Bootstrap writes soul files

- **GIVEN** the personality bootstrap conversation is complete
- **WHEN** the operator has answered all preference questions
- **THEN** the system writes PERSONALITY.md, INSTRUCTIONS.md, and USER.md to
  the config directory

#### Scenario: Bootstrap skipped when files exist

- **GIVEN** personality files already exist in the config directory
- **WHEN** a new conversation starts
- **THEN** no personality bootstrap is triggered
- **AND** the existing personality files are loaded normally

### Requirement: Environment discovery during onboarding

The system SHALL scan for installed tools and host capabilities as part of
Phase 2 onboarding. Discovery results SHALL be persisted to the environment
inventory file for use in session context and capability self-awareness.

#### Scenario: Tool discovery during onboarding

- **WHEN** Phase 2 onboarding runs environment discovery
- **THEN** the system scans for installed tools (git, gh, claude, opencode,
  dotnet, node)
- **AND** checks git credential status
- **AND** writes results to the environment inventory file

#### Scenario: MCP server reachability check during onboarding

- **GIVEN** MCP servers are configured
- **WHEN** Phase 2 onboarding runs environment discovery
- **THEN** the system checks reachability of each configured MCP server
- **AND** records reachability status in the environment inventory

### Requirement: Project registration during onboarding

The system SHALL ask the operator about repositories to register as part of
Phase 2 onboarding. Registered projects are added to the project registry
with their paths, capabilities, and AGENTS.md locations.

#### Scenario: Register projects during onboarding

- **WHEN** Phase 2 onboarding reaches the project registration step
- **THEN** the system asks the operator about repositories to register
- **AND** scans provided paths for AGENTS.md files

#### Scenario: Skip project registration

- **WHEN** Phase 2 onboarding reaches the project registration step
- **AND** the operator indicates no projects to register
- **THEN** onboarding proceeds with an empty project registry

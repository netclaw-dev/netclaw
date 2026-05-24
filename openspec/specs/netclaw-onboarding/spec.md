# netclaw-onboarding Specification

## Purpose

Define bootstrap-first, first-run, and resumable onboarding experiences for
Netclaw operators, including identity-file behavior and existing-install
branches.

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

### Requirement: Security warnings for internet-reachable modes

The system SHALL show explicit warnings before enabling internet-reachable
exposure modes. `Public` deployment posture remains a channel-audience term,
not an anonymous network-access term. Audience selection and exposure-mode
selection are independent choices: audience controls chat participants, while
exposure mode controls daemon network reachability.

#### Scenario: Enable funnel mode

- **WHEN** operator selects `tailscale-funnel`
- **THEN** onboarding requires explicit confirmation and validation that remote
  access is restricted to authenticated users

### Requirement: Guided onboarding

The CLI SHALL provide bootstrap-first guided setup through `netclaw init`.
The onboarding wizard SHALL collect provider configuration, identity, and
security posture, then write a runnable baseline configuration. On
completion, the wizard SHALL run a health check to verify the baseline
configuration is functional. If daemon startup fails because configuration
validation rejects the selected exposure mode or remote-auth topology, the
wizard SHALL surface that failure as a structured setup error with
remediation guidance.

Security Posture, Enabled Features, and Audience Profiles are distinct
concepts.

If the operator selects `Personal`, the bootstrap flow SHALL skip Enabled
Features.

If the operator selects `Team` or `Public`, the bootstrap flow SHALL
automatically continue into Enabled Features before final write.

Audience Profiles editing SHALL NOT be part of init bootstrap; it belongs to
`netclaw config`.

The wizard SHALL NOT write `AGENTS.md` to disk during identity file
generation. AGENTS.md is binary-controlled firmware loaded from embedded
resources at runtime. The wizard SHALL continue to write `SOUL.md` and
`TOOLING.md` as operator-mutable identity files. Identity remains init-owned.

For non-Personal postures, the Enabled Features step writes deployment-wide
`Enabled` switches. These switches SHALL NOT implicitly rewrite Public
audience allowlists.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, identity, and security posture
  inputs
- **AND** writes a runnable baseline configuration
- **AND** writes SOUL.md and TOOLING.md to `~/.netclaw/identity/`
- **AND** does NOT write AGENTS.md (or writes a reference-only stub)

#### Scenario: Personal posture skips enabled-features bootstrap step

- **GIVEN** the operator selected `Personal`
- **WHEN** the posture step completes
- **THEN** init does not open an Enabled Features step

#### Scenario: Team posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Team`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

#### Scenario: Public posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Public`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

#### Scenario: Identity files written on completion

- **WHEN** the wizard completes and writes config
- **THEN** `SOUL.md` is written from the embedded SOUL template
- **AND** `TOOLING.md` is written from the embedded TOOLING template
- **AND** `AGENTS.md` is NOT written from a template

#### Scenario: Public posture defaults search off without mutating Public tool allowlist

- **GIVEN** the operator selected Public posture
- **WHEN** the Feature Selection step is shown
- **THEN** Search defaults to disabled
- **AND** enabling Search there affects only the deployment-wide runtime switch
- **AND** `Tools.AudienceProfiles.Public.AllowedTools` is not implicitly widened

#### Scenario: Exposure-mode startup validation failure shown cleanly

- **GIVEN** the operator completes `netclaw init`
- **AND** the written configuration causes `ExposureModeValidationService` to reject
  daemon startup
- **WHEN** the health-check step starts the daemon
- **THEN** the wizard shows a failed health-check item containing the validation
  message
- **AND** the wizard includes remediation guidance for fixing the exposure/auth
  configuration
- **AND** the operator is not shown a raw stack trace

#### Scenario: Startup validation failure does not degrade to generic readiness timeout

- **GIVEN** daemon startup fails immediately because exposure validation rejects the
  configuration
- **WHEN** the health-check step polls daemon readiness
- **THEN** the wizard reports the actual startup validation failure
- **AND** it does NOT report only `Daemon did not become ready` unless the failure
  reason is genuinely unavailable

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

### Requirement: Onboarding bootstrap aligns with daemon-owned first-launch bootstrap

The init wizard SHALL remain compatible with daemon-owned first-launch bootstrap seeding. Wizard-written bootstrap state SHALL NOT be required for first-launch success, and wizard finalization SHALL NOT overwrite an existing daemon-owned bootstrap credential.

#### Scenario: Wizardless first boot still succeeds

- **GIVEN** the operator never ran `netclaw init`
- **AND** daemon config is otherwise valid for a remote-auth-required exposure mode
- **WHEN** the daemon starts for the first time
- **THEN** first-launch bootstrap behavior does not depend on wizard-written device state

#### Scenario: Wizard bootstrap does not overwrite existing daemon-owned state

- **GIVEN** the daemon already seeded a bootstrap paired device/token
- **WHEN** the operator later runs `netclaw init`
- **THEN** wizard finalization does not overwrite the existing bootstrap credential automatically

### Requirement: Existing-install init menu

When `netclaw init` runs on an existing install, it SHALL open an action menu
with exactly these options:

- `Redo identity setup`
- `Open configuration editor`
- `Start over from scratch`
- `Cancel`

#### Scenario: Existing install opens action menu

- **GIVEN** `netclaw.json` exists
- **WHEN** the operator runs `netclaw init`
- **THEN** init opens the existing-install menu with the documented four
  options

#### Scenario: Existing install routes to config editor

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Open configuration editor`
- **THEN** control routes to `netclaw config`

#### Scenario: Existing install routes to init-owned identity flow

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Redo identity setup`
- **THEN** control routes to the init-owned identity flow

### Requirement: Start-over flow is double-confirmed

Choosing `Start over from scratch` SHALL open a second dialog with exactly:

- `Reset setup only`
- `Full reset`
- `Cancel`

Either destructive option SHALL require double confirmation before files are
mutated.

#### Scenario: Start-over dialog presents reset choices

- **GIVEN** the existing-install menu is open
- **WHEN** the operator chooses `Start over from scratch`
- **THEN** the second dialog presents `Reset setup only`, `Full reset`, and
  `Cancel`

#### Scenario: Destructive reset requires double confirmation

- **GIVEN** the operator selected either `Reset setup only` or `Full reset`
- **WHEN** the destructive flow proceeds
- **THEN** two distinct confirmations are required before mutation

### Requirement: No init-force flag in this flow

This bootstrap flow SHALL NOT rely on a `netclaw init --force` mode.
Existing-install reset behavior SHALL be owned by the in-TUI existing-install
menu and start-over dialogs.

#### Scenario: Existing-install reset does not require hidden flag

- **GIVEN** an existing install
- **WHEN** the operator wants to restart setup
- **THEN** the path is available from the existing-install init menu
- **AND** it does not depend on `netclaw init --force`

### Requirement: Init-owned editor re-entry uses existing config state

Init-owned editor re-entry on an existing install SHALL load existing config
into `WizardContext.ExistingConfig` and prefill non-secret values from that
state. Secret-bearing fields SHALL remain masked and empty.

#### Scenario: Provider re-entry keeps credential field masked

- **GIVEN** an existing provider configuration with stored credentials
- **WHEN** an init-owned provider flow re-enters
- **THEN** provider choice and non-secret fields are prefilled
- **AND** credential inputs remain blank with configured/not-set hint text

#### Scenario: Identity re-entry prefills init-owned fields

- **GIVEN** an existing install with agent name, operator name, and
  timezone already set
- **WHEN** an init-owned identity flow re-enters
- **THEN** those non-secret fields are prefilled

### Requirement: Init-owned writes use semantic merge

Init-owned editor flows SHALL write changes through semantic merge-on-save.
Unrelated config meaning and unrelated stored secrets SHALL be preserved even
if the serialized file text changes.

#### Scenario: Identity-only edit preserves unrelated config meaning

- **GIVEN** an existing install with configured channels, search, and
  exposure settings
- **WHEN** an init-owned identity flow updates only identity-owned data
- **THEN** the unrelated config sections remain semantically unchanged

#### Scenario: Blank secret submission preserves existing secret

- **GIVEN** an init-owned flow includes a secret-bearing field with an
  existing stored value
- **WHEN** the operator leaves that field blank and saves
- **THEN** the existing secret remains stored
- **AND** no decrypted value is shown in the UI

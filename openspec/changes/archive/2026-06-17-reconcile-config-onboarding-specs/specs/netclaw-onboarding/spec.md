# netclaw-onboarding Delta Spec

This is a **delta spec** — it records only the requirements that have been
added, modified, or removed relative to
`openspec/specs/netclaw-onboarding/spec.md`. Unchanged requirements are
omitted. Apply this delta on top of the canonical spec.

---

## REMOVED Requirements

### Requirement: Memory provider selection during onboarding

**Reason**: The Memorizer-vs-local-files wizard step was a pre-build exploration
that shipped as neither. Memory is the always-on auto-memory system backed by
SQLite — no wizard step is needed or present. `HealthCheckStepViewModel` (via
`IdentityStepViewModel.ContributeHealthChecksAsync`) reports
"Memory backend (SQLite)" as a passing health-check item, confirming the fixed
backend. There is no runtime choice.

**Migration**: No operator action is required. Memory is automatic and
SQLite-backed. Remote memory MCP, if ever wanted, is configured after install
via `netclaw config`. `TotalSteps` no longer appears in the spec for this
capability — see the MODIFIED "TUI wizard delivery mechanism" and "Guided
onboarding" requirements for the correct step count.

---

### Requirement: Memorizer MCP connection configuration

**Reason**: Memorizer connection configuration was a pre-build exploration that
was never implemented. The Memorizer MCP server entry
(`McpServers.memorizer`) is not written by the init wizard and the substep that
would collect transport/URL/command details does not exist.

**Migration**: Operators who need a remote MCP memory server configure it after
install via `netclaw config → MCP Servers`.

---

## MODIFIED Requirements

### Requirement: Guided onboarding

`netclaw init` SHALL provide bootstrap-first guided setup. The flow SHALL
collect provider configuration, identity, and security posture. Security
Posture, Enabled Features, and Audience Profiles are distinct concepts.

If the operator selects `Personal`, the bootstrap flow SHALL skip Enabled
Features.

If the operator selects `Team` or `Public`, the bootstrap flow SHALL
automatically continue into Enabled Features before final write.

Audience Profiles editing SHALL NOT be part of init bootstrap; it belongs
to `netclaw config`.

The wizard SHALL continue to write `SOUL.md` and `TOOLING.md`. Identity
remains init-owned in this branch.

The bootstrap wizard SHALL consist of exactly **5 steps** in canonical order:
Provider → Identity → Security Posture → Enabled Features → Health Check.
`TotalSteps` is **5** for `Team`/`Public` postures and **4** for `Personal`
posture (Enabled Features is omitted). Step-progress indicators SHALL reflect
the dynamic count.

#### Scenario: Personal posture skips enabled-features bootstrap step

- **GIVEN** the operator selected `Personal`
- **WHEN** the posture step completes
- **THEN** init does not open an Enabled Features step
- **AND** the wizard proceeds directly to Health Check (step 4 of 4)

#### Scenario: Team posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Team`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

#### Scenario: Public posture continues into enabled-features bootstrap step

- **GIVEN** the operator selected `Public`
- **WHEN** the posture step completes
- **THEN** init automatically continues into Enabled Features

---

### Requirement: TUI wizard delivery mechanism

The `netclaw init` onboarding wizard SHALL be delivered through Termina TUI
as an interactive wizard with progress indication, validation, and
back-navigation. The wizard SHALL have **5 steps** for `Team`/`Public` posture
and **4 steps** for `Personal` posture. Step-progress indicators (e.g.,
"Step 2 of 5" or "Step 2 of 4") SHALL reflect the dynamic total. There is no
fixed 9-step wizard.

#### Scenario: Wizard renders in TUI

- **WHEN** operator runs `netclaw init`
- **THEN** a Termina TUI application launches
- **AND** the wizard displays step progress (e.g., "Step 2 of 5")
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

- **GIVEN** the wizard is on the Provider step
- **WHEN** the operator enters provider credentials
- **THEN** the wizard validates the credentials
- **AND** displays success or failure before allowing progression

---

### Requirement: Phase 2 conversational personality bootstrap

The system SHALL trigger a conversational personality bootstrap on the first
conversation if identity files (`SOUL.md`, `TOOLING.md`) do not already carry
operator-enriched content. The bootstrap is delivered as an initial chat message
injected by the init wizard's navigate callback when `LaunchChat()` fires. The
bootstrap message SHALL ask the operator about communication preferences, tone,
name preferences, and working style, then instruct the agent to update `SOUL.md`
with what it learns. `AGENTS.md` is loaded from embedded resources at runtime
and is NOT written to disk by the wizard.

#### Scenario: First conversation triggers bootstrap

- **GIVEN** the operator completed the init wizard successfully
- **WHEN** the health check step auto-launches chat via `LaunchChat()`
- **THEN** the agent receives a pre-filled onboarding trigger message
- **AND** the message instructs it to introduce itself, ask the operator about
  their primary use case, ask about background and preferences, and then update
  `SOUL.md` with the learned details

#### Scenario: Bootstrap writes soul files

- **GIVEN** the personality bootstrap conversation is complete
- **WHEN** the operator has answered the agent's preference questions
- **THEN** the agent updates `SOUL.md` in the config directory with what it
  learned
- **AND** `TOOLING.md` is already in place from the init wizard's
  `WriteIdentityFiles` call

#### Scenario: Bootstrap skipped when files exist

- **GIVEN** `SOUL.md` already exists in the config directory with enriched
  content
- **WHEN** a new conversation starts
- **THEN** no personality bootstrap trigger is injected
- **AND** the existing `SOUL.md` is loaded normally

---

### Requirement: Environment discovery during onboarding

`netclaw init` SHALL NOT perform environment discovery in the shipped first-run flow (DEFERRED — unimplemented Phase 2 work).

**[DEFERRED — not part of the shipped first-run flow.]** This requirement
describes planned Phase 2 behavior that has not been implemented. Environment
discovery does NOT run during `netclaw init` and is NOT triggered by the health
check step. When implemented, it SHALL be gated by an explicit PRD update and
SHALL NOT be silently enabled in the bootstrap wizard.

The system SHALL scan for installed tools and host capabilities as part of Phase
2 onboarding. Discovery results SHALL be persisted to the environment inventory
file for use in session context and capability self-awareness.

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

---

### Requirement: Project registration during onboarding

`netclaw init` SHALL NOT perform project registration in the shipped first-run flow (DEFERRED — unimplemented Phase 2 work).

**[DEFERRED — not part of the shipped first-run flow.]** This requirement
describes planned Phase 2 behavior that has not been implemented. Project
registration does NOT occur during `netclaw init`. When implemented, it SHALL
be gated by an explicit PRD update.

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

---

## ADDED Requirements

### Requirement: Identity step collects exactly four substeps

The Identity wizard step SHALL collect exactly **4 substeps** in order:
agent name → communication style → operator name → timezone. `SubStepCount`
SHALL equal 4. The Identity step SHALL NOT collect a workspaces directory path
or a notification-webhook URL; those are post-install settings owned by
`netclaw config`.

#### Scenario: Identity step has four substeps

- **WHEN** the wizard enters the Identity step
- **THEN** `SubStepCount` equals 4
- **AND** the substeps are agent name (0), communication style (1), operator
  name (2), and timezone (3)

#### Scenario: Workspaces directory not collected in init

- **WHEN** the operator completes the Identity step
- **THEN** no workspaces directory is written to `netclaw.json`
- **AND** `WizardConfigBuilder.Workspaces` is null after `ContributeConfig`

#### Scenario: Notification webhook not collected in init

- **WHEN** the operator completes the Identity step
- **THEN** no notification webhook is written to `netclaw.json`
- **AND** `WizardConfigBuilder.Notifications` is null after `ContributeConfig`

#### Scenario: Identity step prefills from existing config on re-entry

- **GIVEN** `netclaw.json` exists with `Identity.AgentName`, `Identity.CommunicationStyle`,
  `Identity.UserName`, and `Identity.UserTimezone`
- **WHEN** the operator re-enters the Identity step
- **THEN** all four non-secret fields are prefilled from the existing config

---

### Requirement: Health check auto-launches chat on success

The health check step SHALL launch `netclaw chat` automatically on a clean bootstrap.

On a clean bootstrap (all health check probes passing), the health check step
SHALL invoke `LaunchChat()` automatically without requiring a second Enter
keypress. `LaunchChat()` SHALL route to `/chat` via the wired `Navigate`
delegate. On warnings or failure the step SHALL remain on the summary and exit
on Enter without routing to chat.

#### Scenario: Clean bootstrap auto-launches chat

- **GIVEN** all health-check probes passed
- **WHEN** `RunHealthCheckCoreAsync` completes
- **THEN** `LaunchChat()` is called automatically
- **AND** the Navigate delegate receives `"/chat"`
- **AND** `Succeeded` is `true`

#### Scenario: Failed health check does not launch chat

- **GIVEN** one or more health-check probes failed
- **WHEN** `RunHealthCheckCoreAsync` completes
- **THEN** `LaunchChat()` is NOT called
- **AND** the step displays the failure summary
- **AND** `Succeeded` is `false`

#### Scenario: Failure summary status message

- **GIVEN** the health check completed with at least one failure
- **WHEN** the operator views the summary
- **THEN** the status message reads: "Setup complete with warnings. Run
  `netclaw daemon start`, then `netclaw chat`. Adjust settings with
  `netclaw config`."

---

### Requirement: Health check surfaces container-supervisor deferral reason on timeout

A health-check failure SHALL surface the container-supervisor deferral reason when the supervised daemon never arrives.

When the daemon is externally supervised (`NETCLAW_CONTAINER_SUPERVISOR` marker
set) but the supervisor never actually brings the daemon up within the readiness
poll window, the health-check failure item SHALL surface the actionable
container-supervisor deferral reason (including the hint that the marker may be
set without a supervisor present) rather than the generic "Daemon did not become
ready" message. When a startup-abort crash log is present, the failure message
SHALL include both the abort reason and the crash-log path.

#### Scenario: Supervisor marker set but daemon never starts — surfaces deferral reason

- **GIVEN** `NETCLAW_CONTAINER_SUPERVISOR` is set (i.e., `IsExternallySupervised` is `true`)
- **AND** no supervisor process actually starts the daemon (e.g., the image replaced
  the entrypoint)
- **AND** no `DaemonApi` is wired (poll loop is skipped)
- **WHEN** `StartIfNeededAndPollAsync` times out
- **THEN** the failing health-check item label contains "container supervisor"
- **AND** contains "marker may be set without a supervisor present"
- **AND** does NOT contain "Daemon did not become ready"
- **AND** `Succeeded` is `false`

#### Scenario: Startup-abort crash log surfaces specific failure message

- **GIVEN** the daemon binary exits immediately (bad config or fatal startup error)
- **AND** a crash log exists in the logs directory containing
  "Daemon startup aborted: …"
- **WHEN** `StartIfNeededAndPollAsync` detects the crash log
- **THEN** the failing health-check item label contains the specific abort reason
- **AND** contains the crash-log path
- **AND** does NOT contain "Daemon did not become ready"

#### Scenario: Generic not-ready message is suppressed when a diagnostic is available

- **GIVEN** either a crash log or a supervisor deferral reason is available
- **WHEN** the health-check step records the failure item
- **THEN** the generic "Daemon did not become ready" string is absent from the
  failure label

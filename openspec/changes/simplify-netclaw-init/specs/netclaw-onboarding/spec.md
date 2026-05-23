## MODIFIED Requirements

### Requirement: Guided onboarding

`netclaw init` SHALL provide a three-step guided setup collecting LLM
provider configuration, identity (agent name, operator name, timezone),
and security posture. On completion, the wizard SHALL apply the
posture-default `Tools.AudienceProfiles` mapping in-memory, write the
merged config and secrets via the merge-on-save writer, and run the
existing health check to verify the baseline configuration is
functional. If daemon startup fails because configuration validation
rejects the resulting exposure-mode or remote-auth topology, the
wizard SHALL surface that failure as a structured setup error with
remediation guidance. The wizard SHALL NOT collect Slack credentials,
ACL inputs, search backend, browser automation, memory provider,
MCP server configuration, exposure mode, channels, audience-specific
feature flags, external skill directories, skill feeds, or webhook
URLs during this flow. Those sections SHALL be configured via
`netclaw config` after first-run setup completes.

The wizard SHALL NOT write `AGENTS.md` to disk during identity file
generation. AGENTS.md is binary-controlled firmware loaded from
embedded resources at runtime. The wizard SHALL continue to write
`SOUL.md` and `TOOLING.md` as operator-mutable identity files.

For non-Personal postures, the wizard SHALL apply the posture-default
feature-flag mapping non-interactively (memory, search, skills,
scheduling, sub-agents, webhooks) per the posture's documented
defaults. The wizard SHALL NOT present a separate feature-selection
step. Operators wanting to override these defaults per-audience SHALL
use `netclaw config → Audience Profiles`.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** the wizard collects provider, identity (agent name, user
  name, timezone), and security posture inputs
- **AND** writes a runnable baseline configuration via the merge-on-save
  writer
- **AND** writes SOUL.md and TOOLING.md to `~/.netclaw/identity/`
- **AND** does NOT write AGENTS.md (or writes a reference-only stub)
- **AND** does NOT prompt for Slack, ACL, search, browser automation,
  exposure mode, channels, audience-feature flags, external skills,
  skill feeds, or webhook URLs

#### Scenario: Identity files written on completion

- **WHEN** the wizard completes and writes config
- **THEN** `SOUL.md` is written from the embedded SOUL template
- **AND** `TOOLING.md` is written from the embedded TOOLING template
- **AND** `AGENTS.md` is NOT written from a template

#### Scenario: Posture cascade applied non-interactively

- **GIVEN** the operator selected `Team` posture
- **WHEN** the wizard completes its terminal write
- **THEN** `Tools.AudienceProfiles.Team` is populated with the
  posture-default mapping (memory, search, skills, scheduling,
  sub-agents enabled; webhooks disabled per posture rule)
- **AND** the wizard does not show a separate feature-selection step
- **AND** the operator can edit per-audience features via
  `netclaw config → Audience Profiles`

#### Scenario: Exposure-mode startup validation failure shown cleanly

- **GIVEN** the operator completes `netclaw init`
- **AND** the written configuration causes `ExposureModeValidationService`
  to reject daemon startup
- **WHEN** the health-check step starts the daemon
- **THEN** the wizard shows a failed health-check item containing the
  validation message
- **AND** the wizard includes remediation guidance for fixing the
  exposure/auth configuration
- **AND** the operator is not shown a raw stack trace

#### Scenario: Startup validation failure does not degrade to generic readiness timeout

- **GIVEN** daemon startup fails immediately because exposure validation
  rejects the configuration
- **WHEN** the health-check step polls daemon readiness
- **THEN** the wizard reports the actual startup validation failure
- **AND** it does NOT report only `Daemon did not become ready` unless
  the failure reason is genuinely unavailable

#### Scenario: Post-flight nudge points to netclaw config

- **GIVEN** the wizard completes its terminal write successfully
- **WHEN** the health check passes
- **THEN** Termina displays a post-flight screen confirming what was
  set
- **AND** Termina displays a line directing the operator at
  `netclaw config` for further configuration
- **AND** after Termina teardown the same one-line nudge prints to
  stderr so it remains visible after the TUI clears

## ADDED Requirements

### Requirement: Existing-config detection at init entry

`netclaw init` SHALL detect the presence of a previously-written
`netclaw.json` at startup. When detected and `--force` was not passed,
the command SHALL refuse to proceed: in a TTY it renders a refusal
screen pointing operators at `netclaw config` for live edits or
`netclaw init --force` to reset; in non-TTY usage it prints the
refusal to stderr. The TTY path SHALL exit with status 0 after the
operator acknowledges; the non-TTY path SHALL exit with non-zero
status so CI catches the surprise.

#### Scenario: TTY refusal shows actionable guidance and exits zero

- **GIVEN** `netclaw.json` exists on disk
- **AND** `netclaw init` is run in an interactive TTY without `--force`
- **WHEN** the command starts
- **THEN** Termina renders a refusal screen that names both alternative
  commands: `netclaw config` and `netclaw init --force`
- **AND** the operator presses Enter to acknowledge
- **AND** the command exits with status 0
- **AND** `netclaw.json` and `secrets.json` are unchanged

#### Scenario: Non-TTY refusal exits non-zero

- **GIVEN** `netclaw.json` exists on disk
- **AND** `netclaw init` is run with stdout/stderr redirected (not a TTY)
- **AND** `--force` was not passed
- **WHEN** the command starts
- **THEN** the refusal text prints to stderr
- **AND** the command exits with non-zero status
- **AND** `netclaw.json` and `secrets.json` are unchanged

#### Scenario: No existing config proceeds normally

- **GIVEN** no `netclaw.json` exists on disk
- **WHEN** `netclaw init` is run
- **THEN** the wizard proceeds to Step 1 (Provider) without showing the
  refusal screen

### Requirement: Force-reset backup flow

`netclaw init --force` SHALL detect existing config and require an
explicit type-to-confirm before proceeding. On confirm, the command
SHALL rename `~/.netclaw/config/netclaw.json` to
`netclaw.json.bak.<unix-timestamp>` and
`~/.netclaw/config/secrets.json` to `secrets.json.bak.<unix-timestamp>`.
The wizard SHALL then proceed as a fresh first-run. The .bak files
SHALL be preserved on disk so operators retain a manual recovery
path. The command SHALL print the .bak file paths to the post-flight
screen so operators know where the prior config went.

#### Scenario: Force without confirm leaves config unchanged

- **GIVEN** `netclaw.json` exists on disk
- **AND** `netclaw init --force` is run in an interactive TTY
- **WHEN** the confirm screen renders and the operator cancels
- **THEN** the command exits with status 0
- **AND** `netclaw.json` and `secrets.json` are unchanged

#### Scenario: Force with confirm backs up and proceeds

- **GIVEN** `netclaw.json` and `secrets.json` exist on disk
- **AND** `netclaw init --force` is run in an interactive TTY
- **WHEN** the operator types "reset" and confirms
- **THEN** the original `netclaw.json` is renamed to
  `netclaw.json.bak.<unix-timestamp>`
- **AND** the original `secrets.json` is renamed to
  `secrets.json.bak.<unix-timestamp>`
- **AND** the wizard proceeds to Step 1 (Provider) with
  `WizardContext.ExistingConfig` set to `null`
- **AND** on successful completion the post-flight screen lists the
  .bak file paths

#### Scenario: Force on a fresh install behaves as plain init

- **GIVEN** no `netclaw.json` exists on disk
- **AND** `netclaw init --force` is run
- **WHEN** the command starts
- **THEN** no backup screen is shown (nothing to back up)
- **AND** the wizard proceeds to Step 1 (Provider) normally

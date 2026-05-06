## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding
wizard SHALL collect Slack credentials, provider configuration, ACL inputs,
search backend, browser automation, memory provider selection, MCP server
configuration, and exposure mode selection. On completion, the wizard SHALL
run a health check to verify the baseline configuration is functional. If
daemon startup fails because configuration validation rejects the selected
exposure mode or remote-auth topology, the wizard SHALL surface that failure
as a structured setup error with remediation guidance.

The wizard SHALL NOT write `AGENTS.md` to disk during identity file
generation. AGENTS.md is binary-controlled firmware loaded from embedded
resources at runtime. The wizard SHALL continue to write `SOUL.md` and
`TOOLING.md` as operator-mutable identity files.

For non-Personal postures, the wizard SHALL also present a Feature Selection
step that writes deployment-wide `Enabled` switches. These switches SHALL NOT
implicitly rewrite Public audience allowlists.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, search, browser
  automation, memory, and exposure mode inputs
- **AND** writes a runnable baseline configuration
- **AND** writes SOUL.md and TOOLING.md to `~/.netclaw/identity/`
- **AND** does NOT write AGENTS.md (or writes a reference-only stub)

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

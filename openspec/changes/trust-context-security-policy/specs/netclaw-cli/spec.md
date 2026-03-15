## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding wizard SHALL collect Slack credentials, provider configuration, ACL inputs, MCP server configuration, exposure mode selection, and a recommended security posture. On completion, the wizard SHALL run a health check to verify the baseline configuration is functional.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, MCP, exposure mode, and security posture inputs
- **AND** writes a runnable baseline configuration

#### Scenario: MCP server configured during init

- **WHEN** onboarding reaches the MCP step
- **THEN** the wizard prompts for at least one MCP server profile (Memorizer recommended)
- **AND** validates server handshake before proceeding

#### Scenario: Exposure mode selected during init

- **WHEN** onboarding reaches the exposure step
- **THEN** the wizard presents available exposure modes (local, tailscale-serve, tailscale-funnel, cloudflare-tunnel)
- **AND** applies security warnings for public modes

#### Scenario: Health check on completion

- **WHEN** onboarding completes all steps
- **THEN** the wizard runs a health check covering Slack connectivity, provider validation, and MCP server reachability
- **AND** reports pass/fail for each component

#### Scenario: Missing explicit policy falls back to strict posture

- **WHEN** the operator skips advanced security customization during onboarding
- **THEN** the generated configuration uses strict-default policy behavior
- **AND** diagnostics explain which capabilities remain disabled until explicitly enabled

### Requirement: Config and ACL validation

The CLI SHALL validate configuration and return actionable errors. Validation SHALL treat missing or ambiguous security policy as a strict-default configuration state and SHALL warn or fail on unsafe combinations.

#### Scenario: Validation failure

- **WHEN** config validation fails
- **THEN** command exits non-zero
- **AND** output includes remediation guidance

#### Scenario: Public exposure with host shell is rejected

- **WHEN** validation detects a public or mixed-trust deployment with host shell enabled
- **THEN** validation fails or emits a blocking doctor issue
- **AND** output explains that public-safe policy requires shell to be off until isolated execution exists

### Requirement: Security diagnostics

The CLI SHALL report exposure mode, policy health, and effective trust-context diagnostics.

#### Scenario: Doctor output

- **WHEN** operator runs `netclaw gateway doctor`
- **THEN** output includes exposure mode, policy status, and prioritized issues

#### Scenario: Doctor reports implicit strict defaults

- **WHEN** no explicit trust-context policy is configured
- **THEN** doctor output reports that strict defaults are active
- **AND** lists the capabilities that were reduced because policy was missing or incomplete

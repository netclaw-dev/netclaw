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

#### Scenario: Onboarding writes recommended audience profiles

- **WHEN** onboarding generates a baseline configuration
- **THEN** the configuration includes recommended resolved audience profiles for `public`, `team`, and `personal`
- **AND** the recommended `public` profile limits filesystem access to `{session_dir}` and disables shell
- **AND** the recommended `team` profile remains conservative unless the operator opts into broader scopes

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

#### Scenario: Unrestricted personal profile is warned

- **WHEN** validation detects a `personal` audience profile with `tools = all` and unrestricted filesystem scope
- **THEN** validation emits a high-severity warning
- **AND** output explains that the operator intentionally granted full local authority to the personal profile

#### Scenario: Non-personal profile cannot use unrestricted all mode

- **WHEN** validation detects `tools = all` or unrestricted filesystem mode on a `public` or `team` audience profile
- **THEN** validation fails or emits a blocking doctor issue
- **AND** output explains that unrestricted profile modes are only supported for `personal`

### Requirement: Security diagnostics

The CLI SHALL report exposure mode, policy health, and effective trust-context diagnostics.

#### Scenario: Doctor output

- **WHEN** operator runs `netclaw doctor`
- **THEN** output includes exposure mode, policy status, and prioritized issues

#### Scenario: Doctor reports implicit strict defaults

- **WHEN** no explicit trust-context policy is configured
- **THEN** doctor output reports that strict defaults are active
- **AND** lists the capabilities that were reduced because policy was missing or incomplete

#### Scenario: Doctor explains effective audience profile

- **WHEN** operator runs `netclaw doctor`
- **THEN** output includes the resolved tool/resource scopes for each audience profile
- **AND** output highlights where stricter fallback values were applied because configuration was omitted or partial

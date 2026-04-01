## MODIFIED Requirements

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
- **THEN** the wizard presents available exposure modes: local (default),
  tailscale-serve, tailscale-funnel, cloudflare-tunnel
- **AND** local is pre-selected as the default choice

#### Scenario: Exposure mode writes Daemon config section

- **GIVEN** the operator selects `tailscale-serve` as the exposure mode
- **WHEN** the wizard writes configuration
- **THEN** the config file includes `"Daemon": { "ExposureMode": "tailscale-serve" }`

#### Scenario: Local mode omits Daemon section

- **GIVEN** the operator selects `local` as the exposure mode (or accepts the
  default)
- **WHEN** the wizard writes configuration
- **THEN** the config file does not include a `Daemon` section (defaults apply)

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

### Requirement: Security warnings for public modes

The system SHALL show explicit warnings before enabling public exposure modes.
The wizard SHALL distinguish between tailnet-only modes (lower risk) and
public-facing modes (higher risk) with appropriately scaled warnings.

#### Scenario: Enable funnel mode

- **WHEN** operator selects `tailscale-funnel`
- **THEN** onboarding displays a high-risk warning explaining the daemon will
  be accessible from the public internet
- **AND** requires explicit confirmation before proceeding

#### Scenario: Enable cloudflare-tunnel mode

- **WHEN** operator selects `cloudflare-tunnel`
- **THEN** onboarding displays a high-risk warning explaining the daemon will
  be accessible from the public internet
- **AND** requires explicit confirmation before proceeding

#### Scenario: Enable tailscale-serve mode

- **WHEN** operator selects `tailscale-serve`
- **THEN** onboarding displays an informational notice that the daemon will be
  accessible to devices on the operator's tailnet
- **AND** does not require additional confirmation beyond standard step
  progression

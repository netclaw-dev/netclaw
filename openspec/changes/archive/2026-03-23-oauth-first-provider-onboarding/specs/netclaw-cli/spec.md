## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding
wizard SHALL collect Slack credentials, provider configuration, ACL inputs,
MCP server configuration, and exposure mode selection. Provider setup SHALL
use explicit Termina decision trees for
provider selection, auth method branching, OAuth device flow progression (when
applicable), and model discovery fallback paths. On completion, the wizard
SHALL run a health check to verify the baseline configuration is functional.

#### Scenario: First-time setup with provider decision tree
- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, MCP, and exposure mode
  inputs
- **AND** provider setup executes explicit branches for provider choice, auth
  method, and model resolution path before writing config

#### Scenario: OAuth device flow shown in onboarding
- **GIVEN** selected provider branch uses `oauth-device`
- **WHEN** onboarding reaches auth execution
- **THEN** Termina shows verification URI and user code, then token polling
  progress and final status
- **AND** onboarding provides retry or branch-change actions when authorization
  does not complete

#### Scenario: Model discovery fallback shown in onboarding
- **GIVEN** provider auth branch is completed
- **WHEN** model catalog lookup fails
- **THEN** onboarding follows fallback path (cache -> defaults -> manual)
- **AND** selected fallback path is shown in completion summary

### Requirement: Doctor command

The CLI SHALL provide `netclaw doctor` as a plain CLI command that runs startup
checks and reports results with remediation guidance. The doctor command SHALL
include provider onboarding follow-up checks and SHALL exit with code 0 (all
pass), 1 (errors), or 2 (warnings only).

#### Scenario: Doctor provider follow-up checks
- **WHEN** operator runs `netclaw doctor`
- **THEN** checks include effective provider profile, resolved auth method,
  authorization artifact presence, primary/fallback model validity, and model
  provenance status
- **AND** output includes targeted remediation commands for each failed or
  degraded check

#### Scenario: Degraded onboarding outcome surfaces warning
- **GIVEN** provider setup succeeded using non-live model fallback source
- **WHEN** operator runs `netclaw doctor`
- **THEN** doctor reports warnings with model provenance details
- **AND** exit code is 2 unless a required provider/auth check fails

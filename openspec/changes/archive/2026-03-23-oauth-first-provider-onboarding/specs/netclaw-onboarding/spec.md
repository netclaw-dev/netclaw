## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init` using explicit Termina
decision trees for provider onboarding. The onboarding wizard SHALL collect
Slack credentials, provider configuration, ACL inputs, MCP server
configuration, and exposure mode selection. Provider setup
SHALL branch by selected provider and auth method, and SHALL include model
discovery fallback behavior when live provider catalogs are unavailable. On
completion, the wizard SHALL run a health check to verify the baseline
configuration is functional.

#### Scenario: Provider selection decision tree in Termina
- **WHEN** onboarding reaches provider configuration
- **THEN** Termina presents provider choices with OpenRouter as default
- **AND** selecting a provider advances through an explicit branch path:
  provider selection -> auth method branch -> model selection path -> validation

#### Scenario: Auth method branch selection
- **GIVEN** a selected provider supports multiple auth methods
- **WHEN** onboarding reaches auth configuration for that provider
- **THEN** Termina shows explicit auth branches (`oauth-device` or `api-key`)
- **AND** the selected branch determines subsequent required inputs and
  validation rules

#### Scenario: Model discovery fallback branch
- **GIVEN** provider and auth branch are selected
- **WHEN** live model catalog discovery fails or returns no usable models
- **THEN** onboarding executes fallback in order: curated defaults, manual
  model entry
- **AND** the selected model source provenance is recorded for diagnostics

#### Scenario: Health check on completion
- **WHEN** onboarding completes all steps
- **THEN** the wizard runs a health check covering Slack connectivity, provider
  validation, and MCP server reachability
- **AND** reports pass/fail for each component

### Requirement: TUI wizard delivery mechanism

The `netclaw init` onboarding wizard SHALL be delivered through Termina TUI as
an interactive 6-step wizard with progress indication, validation,
back-navigation, and branch-context display for provider onboarding decisions.

#### Scenario: Wizard renders branch context in TUI
- **WHEN** operator runs `netclaw init`
- **THEN** Termina displays wizard progress and current branch context (provider,
  auth method, model source)
- **AND** context updates immediately when branch decisions change

#### Scenario: Back navigation preserves branch state
- **GIVEN** operator has selected provider and auth method branches
- **WHEN** operator navigates backward and changes a prior branch decision
- **THEN** downstream branch-dependent fields are recalculated
- **AND** invalidated values are cleared before progression is allowed

## ADDED Requirements

### Requirement: OAuth device flow onboarding steps

When the selected provider auth branch is `oauth-device`, onboarding SHALL
execute an explicit device flow state sequence and SHALL expose each step in
Termina so the operator can recover from partial or failed authorization.

#### Scenario: OAuth device flow success path
- **GIVEN** operator selected `oauth-device` auth
- **WHEN** onboarding starts device authorization
- **THEN** Termina executes and displays states in order: start request -> show
  verification URI and user code -> poll token endpoint -> token received
- **AND** onboarding stores resulting auth artifacts in secret-safe config

#### Scenario: OAuth device flow denied or expired
- **GIVEN** onboarding is polling for OAuth device authorization
- **WHEN** provider returns `access_denied` or `expired_token`
- **THEN** Termina shows the failure state with remediation
- **AND** operator can choose retry, switch auth method, switch provider, or
  cancel onboarding step

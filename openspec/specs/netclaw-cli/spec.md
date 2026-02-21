# netclaw-cli Specification

## Purpose

Define command-line management behavior for onboarding, validation, and
diagnostics.

## Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects Slack, provider, and ACL inputs
- **AND** writes a runnable baseline configuration

### Requirement: Resumable onboarding

The CLI SHALL support resuming incomplete onboarding.

#### Scenario: Resume setup

- **GIVEN** onboarding is incomplete
- **WHEN** operator runs `netclaw init --resume`
- **THEN** setup continues from first incomplete step

### Requirement: Config and ACL validation

The CLI SHALL validate configuration and return actionable errors.

#### Scenario: Validation failure

- **WHEN** config validation fails
- **THEN** command exits non-zero
- **AND** output includes remediation guidance

### Requirement: Security diagnostics

The CLI SHALL report exposure mode and policy health.

#### Scenario: Doctor output

- **WHEN** operator runs `netclaw gateway doctor`
- **THEN** output includes exposure mode, policy status, and prioritized issues

### Requirement: Optional smoke test command

The CLI SHALL expose an explicit smoke-test command for live provider checks.

#### Scenario: Run Ollama smoke test

- **WHEN** operator runs `netclaw test smoke --provider ollama`
- **THEN** CLI executes provider connectivity smoke checks
- **AND** outputs a concise pass/fail report

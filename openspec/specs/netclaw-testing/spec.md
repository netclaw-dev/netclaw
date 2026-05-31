# netclaw-testing Specification

## Purpose

Define test categorization and CI requirements for provider-independent
verification.

## Requirements

### Requirement: CI-required tests are provider-independent

The required CI suite SHALL not depend on live model providers.

Required CI coverage for channel adapters SHALL also not depend on live external
chat platforms (including Discord). Channel behavior SHALL be verifiable using
offline fakes, fixtures, or deterministic simulators.

#### Scenario: CI execution without provider secrets

- **WHEN** CI executes required tests without provider credentials
- **THEN** all required tests pass using fakes/mocks/stubs

#### Scenario: CI execution without live Discord instance

- **GIVEN** CI has no Discord token and no live Discord connectivity
- **WHEN** required test suites run
- **THEN** Discord adapter and approval fallback behavior are validated offline
- **AND** required suites pass without external Discord dependencies

### Requirement: Optional live smoke tests

The system SHALL support optional smoke tests against live endpoints.

#### Scenario: Developer runs live smoke test

- **WHEN** a developer invokes smoke tests explicitly
- **THEN** live provider checks execute and report actionable diagnostics

#### Scenario: Tailscale-only Ollama server not reachable in CI

- **GIVEN** Ollama server is only reachable on Tailscale
- **WHEN** CI runs without Tailscale connectivity
- **THEN** CI-required test suites still pass because live smoke tests are not required

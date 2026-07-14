# netclaw-testing Specification

## Purpose

Define test categorization and CI requirements for provider-independent
verification.
## Requirements
### Requirement: CI-required tests are provider-independent

The required CI suite SHALL not depend on live model providers.

Required CI coverage for channel adapters SHALL also not depend on live external
chat platforms (including Discord and Mattermost). Channel behavior SHALL be
verifiable using offline fakes, fixtures, or deterministic simulators. Tests
that require a live external chat platform (such as Testcontainers-based
Mattermost integration tests) SHALL be kept out of the required CI suite.

#### Scenario: CI execution without provider secrets

- **WHEN** CI executes required tests without provider credentials
- **THEN** all required tests pass using fakes/mocks/stubs

#### Scenario: CI execution without live Discord instance

- **GIVEN** CI has no Discord token and no live Discord connectivity
- **WHEN** required test suites run
- **THEN** Discord adapter and approval fallback behavior are validated offline
- **AND** required suites pass without external Discord dependencies

#### Scenario: CI execution without live Mattermost instance

- **GIVEN** CI has no Mattermost token and no live Mattermost connectivity
- **WHEN** required test suites run
- **THEN** Mattermost adapter, conformance contract suites, and approval
  fallback behavior are validated offline
- **AND** required suites pass without external Mattermost dependencies
- **AND** Testcontainers-based Mattermost integration tests are not part of the
  required suite

### Requirement: Optional live smoke tests

The system SHALL support optional smoke tests against live endpoints.

#### Scenario: Developer runs live smoke test

- **WHEN** a developer invokes smoke tests explicitly
- **THEN** live provider checks execute and report actionable diagnostics

#### Scenario: Tailscale-only Ollama server not reachable in CI

- **GIVEN** Ollama server is only reachable on Tailscale
- **WHEN** CI runs without Tailscale connectivity
- **THEN** CI-required test suites still pass because live smoke tests are not required

### Requirement: Coding-context evals use isolated deterministic fixtures
The behavioral eval suite SHALL support focused multi-turn coding-context cases where every scored run receives a fresh Git repository, linked worktree, unique named session, deterministic file state, and independent filesystem assertions.

#### Scenario: Main and child context lifecycle is evaluated across turns
- **GIVEN** a fresh linked-worktree fixture and unique resumed session
- **WHEN** one turn establishes file context, a later turn delegates coding, and a final turn reports resulting context
- **THEN** assertions inspect JSON tool behavior, structured child metadata, and direct Git/filesystem state

#### Scenario: Baseline and treatment results are comparable
- **GIVEN** baseline and treatment images use the same model settings and prompt variants
- **WHEN** the focused coding-context category is run repeatedly
- **THEN** results retain correctness, orientation-call, clarification, token, cache, and latency metrics for comparison


## ADDED Requirements

### Requirement: CI-required tests are provider-independent

The required CI suite SHALL not depend on live model providers.

#### Scenario: CI execution without provider secrets

- **WHEN** CI executes required tests without provider credentials
- **THEN** all required tests pass using fakes/mocks/stubs

### Requirement: Optional live smoke tests

The system SHALL support optional smoke tests against live endpoints.

#### Scenario: Developer runs live smoke test

- **WHEN** a developer invokes smoke tests explicitly
- **THEN** live provider checks execute and report actionable diagnostics

#### Scenario: Tailscale-only Ollama server not reachable in CI

- **GIVEN** Ollama server is only reachable on Tailscale
- **WHEN** CI runs without Tailscale connectivity
- **THEN** CI-required test suites still pass because live smoke tests are not required

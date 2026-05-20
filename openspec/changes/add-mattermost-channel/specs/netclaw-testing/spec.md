## MODIFIED Requirements

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

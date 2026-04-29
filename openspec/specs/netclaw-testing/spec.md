## MODIFIED Requirements

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

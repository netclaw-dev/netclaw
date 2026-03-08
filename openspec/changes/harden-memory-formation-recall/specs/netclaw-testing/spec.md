## MODIFIED Requirements

### Requirement: Optional live smoke tests

The system SHALL support optional smoke tests against live endpoints. Memory
hygiene rollout gates SHALL distinguish smoke suites from realistic suites: both
SHALL be deterministic and SHALL use synthetic/sanitized fixtures only (no real
PII/secrets), while live smoke tests remain optional diagnostics rather than CI
required gates.

#### Scenario: Developer runs live smoke test

- **WHEN** a developer invokes smoke tests explicitly
- **THEN** live provider checks execute and report actionable diagnostics

#### Scenario: Tailscale-only Ollama server not reachable in CI

- **GIVEN** Ollama server is only reachable on Tailscale
- **WHEN** CI runs without Tailscale connectivity
- **THEN** CI-required test suites still pass because live smoke tests are not required

#### Scenario: Memory hygiene suites use sanitized fixtures

- **GIVEN** memory hygiene smoke or realistic suites are executed
- **WHEN** fixture data is loaded for recall/noise/privacy evaluation
- **THEN** fixtures contain synthetic/sanitized identities and content only
- **AND** suites fail validation if unsanitized PII/secrets are detected in fixtures

### Requirement: CI-required tests are provider-independent

The required CI suite SHALL not depend on live model providers. Phase A memory
hygiene CI gates SHALL run provider-independent deterministic smoke coverage,
while realistic suites MAY run in required pre-merge/nightly gates with the
same sanitized deterministic fixtures and defined stability thresholds.

#### Scenario: CI execution without provider secrets

- **WHEN** CI executes required tests without provider credentials
- **THEN** all required tests pass using fakes/mocks/stubs

#### Scenario: Memory hygiene smoke gate is CI-required

- **GIVEN** Phase A memory hygiene changes are present
- **WHEN** required CI checks run
- **THEN** deterministic provider-independent smoke suite results are reported
- **AND** failing smoke thresholds block merge readiness

#### Scenario: Realistic suite enforces stability threshold

- **GIVEN** the realistic memory hygiene suite is configured as a rollout gate
- **WHEN** threshold metrics are evaluated across repeated runs
- **THEN** rollout requires the configured consecutive passing run count
- **AND** one failing run resets the passing streak requirement

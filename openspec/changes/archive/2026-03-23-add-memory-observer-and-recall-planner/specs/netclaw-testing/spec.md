## MODIFIED Requirements

### Requirement: CI-required tests are provider-independent

The required CI suite SHALL not depend on live model providers. Memory-sidecar
and memory-recall CI gates SHALL run against deterministic provider-independent
fixtures and stubs, and SHALL verify the full formation-then-recall pipeline,
evidence-vs-durable separation, and deterministic gate rejection behavior.

#### Scenario: CI execution without provider secrets

- **WHEN** CI executes required tests without provider credentials
- **THEN** all required tests pass using fakes/mocks/stubs

#### Scenario: Memory formation then recall is CI covered

- **GIVEN** memory-sidecar changes are present
- **WHEN** required CI checks run
- **THEN** CI executes deterministic formation-then-auto-recall fixtures without live providers
- **AND** failing thresholds block merge readiness

#### Scenario: Evidence separation is CI covered

- **GIVEN** a fixture produces both `durable_fact` and `evidence`
- **WHEN** CI evaluates automatic recall and intentional search behavior
- **THEN** automatic recall excludes `evidence`
- **AND** intentional search still surfaces the `evidence` when the fixture expects it

### Requirement: Optional live smoke tests

The system SHALL support optional smoke tests against live endpoints. Live model
checks MAY validate sidecar prompt realism or local-Ollama rollout readiness,
but required gating SHALL remain based on synthetic/sanitized formation and
recall fixtures rather than pre-seeded-memory-only scenarios.

#### Scenario: Developer runs live smoke test

- **WHEN** a developer invokes smoke tests explicitly
- **THEN** live provider checks execute and report actionable diagnostics

#### Scenario: Tailscale-only Ollama server not reachable in CI

- **GIVEN** Ollama server is only reachable on Tailscale
- **WHEN** CI runs without Tailscale connectivity
- **THEN** CI-required test suites still pass because live smoke tests are not required

#### Scenario: Sidecar rollout gate requires stability streak

- **GIVEN** smoke and realistic sanitized memory suites are used for rollout gating
- **WHEN** the sidecar-assisted memory feature is evaluated for default enablement
- **THEN** smoke thresholds must pass for the configured consecutive CI run count
- **AND** realistic thresholds must pass for the configured consecutive local-Ollama gate count before rollout

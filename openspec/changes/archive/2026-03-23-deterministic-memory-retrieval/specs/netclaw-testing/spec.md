## ADDED Requirements

### Requirement: Deterministic memory retrieval eval gates

The test suite SHALL include deterministic memory retrieval evals that validate
request planning, candidate selection, ranking, bundle assembly, policy-safe
scope handling, and degraded fallback behavior. These evals SHALL include both
fast smoke fixtures and a larger sanitized realistic suite, and rollout SHALL
be blocked until the configured stability thresholds pass.

#### Scenario: Smoke suite catches deterministic retrieval regressions
- **WHEN** CI evaluates deterministic memory retrieval on the smoke fixture suite
- **THEN** regressions in request planning, recall precision, noise suppression, or degraded fallback are detected without requiring live providers
- **AND** failures block the required test run

#### Scenario: Realistic suite validates rollout readiness
- **WHEN** the larger sanitized retrieval suite runs on the default evaluation profile
- **THEN** the measured recall quality, policy safety, and latency meet the configured thresholds for consecutive runs
- **AND** deterministic retrieval is not considered rollout-ready if the stability gate fails

#### Scenario: Diagnostics fixtures remain sanitized
- **WHEN** deterministic retrieval scenarios are added to the eval corpus
- **THEN** the fixtures use synthetic or sanitized memory content only
- **AND** no real secrets, credentials, or operator-private data are required for validation

### Requirement: Deterministic extractor contract tests

The test suite SHALL validate the write-time deterministic retrieval metadata
contract independently from read-time ranking so regressions in aliases,
anchors, facets, slots, relations, policy fields, or expiry metadata are caught
before they poison recall behavior.

#### Scenario: Extractor contract validates stable metadata
- **WHEN** a memory proposal is generated for a supported durable-memory scenario
- **THEN** tests verify that the required retrieval metadata fields are present and well-formed
- **AND** malformed or incomplete proposals fail validation before retrieval evals depend on them

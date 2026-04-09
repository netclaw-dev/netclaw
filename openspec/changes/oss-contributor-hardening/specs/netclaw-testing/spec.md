# netclaw-testing Delta Spec

## ADDED Requirements

### Requirement: Phase 0 compatibility safety nets
Before provider, channel, auth, or notification seam extraction changes are merged, the system SHALL maintain Phase 0 compatibility safety nets for the protected OpenAI and Slack paths.

#### Scenario: Protected compatibility suite gates seam refactor
- **WHEN** a change modifies provider, channel, auth, or notification seam code during the hardening program
- **THEN** required validation includes compatibility coverage for the OpenAI API-key path, the OpenAI OAuth/subscription path, and Slack runtime behavior
- **AND** the seam refactor does not merge unless those protected-path checks pass

#### Scenario: Compatibility suite remains contributor-safe
- **WHEN** a contributor runs the required hardening validation suite from a fresh clone without live secrets
- **THEN** the required suite uses fakes, stubs, or contract fixtures rather than private credentials
- **AND** protected-path regressions remain detectable without requiring private operator infrastructure

### Requirement: Contract and scenario oriented seam coverage
The hardening program SHALL prefer a smaller number of contract tests and broader scenario tests over many seam-local narrow tests when validating provider, channel, auth, and notification seams.

#### Scenario: New seam behavior is covered by contract tests
- **WHEN** a new provider, channel, auth, or notification seam contract is introduced or changed
- **THEN** the required test suite includes contract tests that verify the seam invariants and fail-closed behavior
- **AND** the suite does not rely only on narrow implementation-local tests

#### Scenario: End-to-end seam scenario covers compatibility-critical flow
- **WHEN** a protected OpenAI or Slack path is changed by a seam extraction phase
- **THEN** the required test suite includes at least one broader scenario that exercises the compatibility-critical path through the relevant seam boundary
- **AND** the scenario asserts the expected user-visible behavior rather than only internal method calls

### Requirement: No silent fallback in seam validation tests
Required seam validation tests SHALL assert fail-closed behavior for invalid provider, channel, auth, and notification configurations.

#### Scenario: Invalid seam configuration fails loudly in tests
- **WHEN** a required test exercises an unknown provider kind, unknown channel kind, invalid auth configuration, or invalid notification target
- **THEN** the test asserts an explicit validation failure
- **AND** the test asserts that no silent fallback runtime activation occurs

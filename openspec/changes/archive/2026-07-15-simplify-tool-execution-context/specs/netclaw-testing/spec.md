## ADDED Requirements

### Requirement: Execution-context isolation has automated proof

The test suite SHALL prove that admitted authority is required, parallel calls do not share mutable call state, unavailable requested capabilities fail without fallback, child deltas merge only after success, and asynchronous Git enrichment respects audience and turn-generation gates.

#### Scenario: Parallel execution regression test

- **GIVEN** a deterministic test pipeline with two concurrent tool calls
- **WHEN** each call records different file activity
- **THEN** each result contains only its own activity
- **AND** both retain the same immutable admitted-turn authority

#### Scenario: Public Git gate regression test

- **GIVEN** a fake Git inspector that records invocations
- **WHEN** a Public working-context snapshot is composed
- **THEN** the inspector records no invocation
- **AND** no internal path is rendered

#### Scenario: Stale continuation regression test

- **GIVEN** controllable asynchronous Git inspection results for consecutive turns
- **WHEN** the earlier result completes after the later turn becomes active
- **THEN** the earlier result is discarded without sleeps
- **AND** only the correlated result can affect the active prompt

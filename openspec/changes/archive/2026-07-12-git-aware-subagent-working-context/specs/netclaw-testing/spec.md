## ADDED Requirements

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

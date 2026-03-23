## MODIFIED Requirements

### Requirement: Automatic pre-turn memory recall

The session system SHALL run automatic durable-memory recall before each
user-facing model turn using a deterministic retrieval pipeline. The recall
pipeline SHALL resolve runtime-owned security boundary and subject scope, derive conversation-owned soft
scope, build a deterministic request plan, execute bounded candidate selection
against SQLite, and inject a bounded ranked or bundle-shaped recall set before
the model call. If planning, query, or ranking exceeds its latency budget or
the memory substrate is unhealthy, the turn SHALL continue in degraded mode
without blocking on recall.

#### Scenario: User-facing turn receives automatic recall bundle
- **GIVEN** a session receives a new user message
- **WHEN** the turn pipeline prepares the model request
- **THEN** the session queries durable memory through the deterministic recall pipeline before the model call
- **AND** injects a bounded recall bundle when eligible memories are found

#### Scenario: Recall timeout degrades safely
- **GIVEN** the memory recall pipeline exceeds its configured time budget
- **WHEN** the session is preparing the next model call
- **THEN** the session continues without the recall bundle
- **AND** records degraded memory status for diagnostics and observability

#### Scenario: Hard scope is resolved before memory search
- **GIVEN** the session has channel, thread, direct-message, project-binding, or operator runtime context
- **WHEN** automatic recall begins
- **THEN** the session resolves the legal memory boundary from runtime-owned security boundary, subject bindings, and policy before searching
- **AND** later planning and ranking stages do not widen that boundary

#### Scenario: Planner failure falls back to minimal deterministic recall
- **GIVEN** deterministic request planning cannot derive a full ranked or bundle plan
- **WHEN** the session still has a valid hard scope and memory health is otherwise acceptable
- **THEN** the session may use a minimal deterministic lexical-and-anchor fallback inside that scope
- **AND** it does not invoke an LLM planner in the hot path as the recovery mechanism

## ADDED Requirements

### Requirement: Recall pipeline observability

The session system SHALL emit structured observability for deterministic recall
stages so operators can distinguish scope-resolution, planning,
candidate-selection, ranking, and degradation failures without inspecting model
output alone.

#### Scenario: Degraded recall reports the failing stage
- **WHEN** automatic recall degrades during a user-facing turn
- **THEN** the session records which deterministic stage failed or timed out
- **AND** operators can distinguish retrieval degradation from provider or tool failures

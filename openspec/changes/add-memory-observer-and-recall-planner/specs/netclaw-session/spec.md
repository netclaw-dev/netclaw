## MODIFIED Requirements

### Requirement: Automatic pre-turn memory recall

The session system SHALL run automatic durable-memory recall before each
user-facing model turn. The recall pipeline SHALL use the incoming user
message, recent turn state, active project/session context, and policy scope to
assemble a bounded recall bundle. Before repository execution, the session
SHALL build a sanitized `RecallPlanningRequest` and invoke `RecallPlanningSidecar`
to obtain a structured `RecallQueryPlan`. Deterministic gating SHALL validate
and clamp that plan before execution. If recall planning or recall execution
exceeds its latency budget, returns invalid structured output, or the memory
substrate is unhealthy, the turn SHALL continue in degraded mode without
blocking on recall. Automatic recall SHALL only inject `durable_fact` items.

#### Scenario: User-facing turn receives automatic recall bundle

- **GIVEN** a session receives a new user message
- **WHEN** the turn pipeline prepares the model request
- **THEN** the session queries durable memory before the model call
- **AND** injects a bounded recall bundle when eligible memories are found

#### Scenario: Recall timeout degrades safely

- **GIVEN** the memory recall pipeline exceeds its configured time budget
- **WHEN** the session is preparing the next model call
- **THEN** the session continues without the recall bundle
- **AND** records degraded memory status for diagnostics and observability

#### Scenario: Invalid recall plan falls back safely

- **GIVEN** `RecallPlanningSidecar` returns invalid JSON, an unknown memory class,
  or a plan that exceeds configured clamps
- **WHEN** deterministic recall-plan gating evaluates the plan
- **THEN** the invalid plan is rejected
- **AND** the session falls back to degraded deterministic recall behavior rather than blocking the turn

#### Scenario: Automatic recall excludes evidence by contract

- **GIVEN** recall planning identifies both durable facts and supporting evidence as relevant
- **WHEN** the session executes automatic pre-turn recall
- **THEN** deterministic gating limits the plan to `durable_fact`
- **AND** the injected recall bundle does not contain `evidence` items

### Requirement: Durable memory checkpoint scheduling

The session system SHALL emit durable memory checkpoints on eligible events
including explicit memory requests, stable user facts, verified tool findings,
compaction boundaries, and accepted subagent findings. For automatic memory
formation, the session SHALL first build a sanitized `MemoryObservationRequest`
and invoke `MemoryObservationSidecar` to obtain structured `MemoryProposal`
results. Deterministic proposal gating SHALL validate and normalize proposals
before any checkpoint enqueue occurs. Checkpoint enqueue SHALL be durable before
the turn reports a successful explicit save, and pending checkpoints SHALL
survive daemon restart.

#### Scenario: Explicit remember request is durably queued

- **GIVEN** the operator explicitly tells Netclaw to remember a fact
- **WHEN** the session handles that request
- **THEN** the session durably enqueues a high-priority checkpoint before
  reporting success
- **AND** background curation may complete after the user-facing turn finishes

#### Scenario: Pending checkpoints recover after restart

- **GIVEN** one or more memory checkpoints were queued before daemon shutdown
- **WHEN** the daemon restarts
- **THEN** the memory worker reloads the pending checkpoints
- **AND** resumes curation without losing the queued work

#### Scenario: Observation proposal becomes checkpoint after deterministic review

- **GIVEN** a turn summary produces a valid `MemoryProposal` from `MemoryObservationSidecar`
- **WHEN** deterministic proposal gating accepts the proposal
- **THEN** the session enqueues a durable checkpoint derived from that accepted proposal
- **AND** the sidecar does not write durable memory directly

#### Scenario: Observation sidecar failure preserves turn progress

- **GIVEN** `MemoryObservationSidecar` times out or returns invalid structured output
- **WHEN** the session evaluates post-turn memory observation
- **THEN** the session records degraded observation diagnostics
- **AND** the turn continues without a sidecar-derived checkpoint unless another deterministic checkpoint source applies

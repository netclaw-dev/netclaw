## MODIFIED Requirements

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach informed by cross-SDK research. Before and after compaction boundaries, the session SHALL emit high-priority memory checkpoints into the durable memory queue instead of performing a synchronous one-off memory flush that depends on the turn path completing all curation work inline.

#### Scenario: Compaction threshold reached
- **GIVEN** `UsageDetails.InputTokenCount` exceeds `SessionConfig.CompactionTokenLimit`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` behavior state
- **AND** incoming messages are buffered during compaction

#### Scenario: Compaction boundary emits memory checkpoint
- **GIVEN** compaction is about to run or has just completed a summary reduction
- **WHEN** the compaction boundary is reached
- **THEN** the session enqueues a high-priority memory checkpoint for durable curation
- **AND** the user-facing session does not wait for background curation to finish

#### Scenario: Tiered compaction preserves tool/result integrity
- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** compaction runs
- **THEN** tool call/result pairs are never orphaned
- **AND** older tool interactions remain representable for checkpoint extraction and summarization

## ADDED Requirements

### Requirement: Automatic pre-turn memory recall

The session system SHALL run automatic durable-memory recall before each user-facing model turn. The recall pipeline SHALL use the incoming user message, recent turn state, active project/session context, and policy scope to assemble a bounded recall bundle. If recall exceeds its latency budget or the memory substrate is unhealthy, the turn SHALL continue in degraded mode without blocking on recall.

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

### Requirement: Durable memory checkpoint scheduling

The session system SHALL emit durable memory checkpoints on eligible events including explicit memory requests, stable user facts, verified tool findings, compaction boundaries, and accepted subagent findings. Checkpoint enqueue SHALL be durable before the turn reports a successful explicit save, and pending checkpoints SHALL survive daemon restart.

#### Scenario: Explicit remember request is durably queued
- **GIVEN** the operator explicitly tells Netclaw to remember a fact
- **WHEN** the session handles that request
- **THEN** the session durably enqueues a high-priority checkpoint before reporting success
- **AND** background curation may complete after the user-facing turn finishes

#### Scenario: Pending checkpoints recover after restart
- **GIVEN** one or more memory checkpoints were queued before daemon shutdown
- **WHEN** the daemon restarts
- **THEN** the memory worker reloads the pending checkpoints
- **AND** resumes curation without losing the queued work

## MODIFIED Requirements

### Requirement: Durable memory checkpoint scheduling

The session system SHALL emit durable memory checkpoints on eligible events
including explicit memory requests, stable user facts, verified tool findings,
compaction boundaries, and accepted subagent findings. Verified tool findings
MAY continue using the simpler existing checkpoint heuristic. Subagent findings
SHALL be eligible only after deterministic parent-session review marks them as
`accept`; findings marked `defer` or `reject` SHALL NOT be enqueued as durable
checkpoints in MVP-now. Checkpoint enqueue SHALL be durable before the turn
reports a successful explicit save, and pending checkpoints SHALL survive daemon
restart.

#### Scenario: Explicit remember request is durably queued

- **GIVEN** the operator explicitly tells Netclaw to remember a fact
- **WHEN** the session handles that request
- **THEN** the session durably enqueues a high-priority checkpoint before
  reporting success
- **AND** background curation may complete after the user-facing turn finishes

#### Scenario: Accepted subagent finding is enqueued as a checkpoint

- **GIVEN** a findings-capable subagent returns a conclusion-level finding with
  allowed policy metadata and strong enough confidence, durability, and
  reusability
- **WHEN** the parent session reviews the finding
- **THEN** the session marks the finding `accept` and enqueues a durable
  checkpoint derived from it
- **AND** the checkpoint remains owned by the parent session

#### Scenario: Deferred subagent finding is not enqueued

- **GIVEN** a findings-capable subagent returns a candidate whose metadata is
  incomplete or too ambiguous for conservative acceptance
- **WHEN** the parent session reviews the finding
- **THEN** the session marks the finding `defer`
- **AND** no durable checkpoint is enqueued from that candidate

#### Scenario: Pending checkpoints recover after restart

- **GIVEN** one or more memory checkpoints were queued before daemon shutdown
- **WHEN** the daemon restarts
- **THEN** the memory worker reloads the pending checkpoints
- **AND** resumes curation without losing the queued work

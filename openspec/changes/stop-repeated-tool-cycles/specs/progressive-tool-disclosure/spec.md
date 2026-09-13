## MODIFIED Requirements

### Requirement: Deferred exposure is actor-local and recoverable

Loaded deferred tools SHALL be transient actor state. Main-session leases SHALL
use the configured limits. Subagent-loaded tools SHALL exist only for that child
run. Recovery SHALL reseed the core and SHALL NOT require a durable migration.

A successful normal compaction SHALL preserve the loaded set and its current
leases. An LLM failure SHALL evict the loaded set. A context-overflow failure
SHALL evict the set before Netclaw starts its recovery compaction.

#### Scenario: Main model failure evicts deferred first-party tool

- **GIVEN** a main session loaded a deferred first-party tool
- **WHEN** an LLM call fails and resets the cache
- **THEN** the next model request omits that tool
- **AND** the core remains available

#### Scenario: Context overflow evicts before compaction

- **GIVEN** a main session loaded a deferred tool
- **WHEN** the model call fails because its context overflowed
- **THEN** Netclaw evicts the loaded tool before recovery compaction
- **AND** the retried model call receives only the policy-exposed core

#### Scenario: Normal compaction preserves deferred tool exposure

- **GIVEN** a main session loaded a deferred tool during an active turn
- **WHEN** a successful normal compaction resumes that turn
- **THEN** the next model request still includes the loaded tool
- **AND** compaction does not refresh or decrement its lease

#### Scenario: Session recovery reseeds only the core

- **GIVEN** a main session had loaded deferred tools before a process stop
- **WHEN** the session actor recovers
- **THEN** the recovered actor starts with its policy-exposed core
- **AND** it restores no loaded schema from durable state

#### Scenario: Child completion discards loaded tools

- **GIVEN** a subagent loads a deferred tool
- **WHEN** that child stops and another child starts
- **THEN** the new child receives only its policy-exposed core
- **AND** it does not inherit the prior child lease

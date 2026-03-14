## MODIFIED Requirements

### Requirement: Rules-first candidate extraction

The system SHALL run deterministic rules before any curator LLM call when
converting checkpoints into durable memory. These rules SHALL reject ephemeral
chatter, duplicates, policy-violating content, and low-confidence candidates
before invoking the curator. Verified tool findings MAY continue using a simpler
verified-source admission heuristic. Subagent findings SHALL use a stricter
review path: the source subagent MUST be findings-capable, the findings
candidate MUST be conclusion-shaped rather than a work log, and the parent
session MUST have enough metadata to evaluate `domain`, `sensitivity`,
`confidence`, `durability`, and `reusability` conservatively before checkpoint
enqueue.

#### Scenario: Trivial chatter is filtered before curation

- **GIVEN** a checkpoint contains both stable project facts and casual
  acknowledgments
- **WHEN** rules-first extraction runs
- **THEN** the stable facts survive as candidates
- **AND** the casual acknowledgments are dropped without calling the curator for
  them

#### Scenario: Verified tool finding keeps simpler admission path

- **GIVEN** a verified tool result contains a stable project fact with allowed
  policy metadata
- **WHEN** checkpoint admission evaluates that tool-derived finding
- **THEN** the system MAY accept it using the existing simpler verified-tool
  heuristic
- **AND** it does not require the stricter subagent findings envelope review

#### Scenario: Subagent finding with incomplete metadata is deferred before enqueue

- **GIVEN** a subagent findings candidate is missing durability or reusability
  metadata
- **WHEN** the parent session and rules-first pipeline evaluate that candidate
- **THEN** the candidate is not accepted for checkpoint enqueue
- **AND** no durable write occurs in MVP-now

#### Scenario: Raw work-log candidate is rejected before curation

- **GIVEN** a subagent findings candidate contains raw execution trace instead of
  a durable conclusion
- **WHEN** rules-first extraction evaluates the candidate
- **THEN** the candidate is rejected before any curator call
- **AND** no durable checkpoint is created from it

### Requirement: Main session owns durable memory persistence

The main user-facing session SHALL be the default owner of durable memory
writes. Subagents and other helper workflows SHALL return findings to the owning
session, and the owning session SHALL decide whether those findings become
checkpoints and durable writes. For subagent findings, the owning session SHALL
apply deterministic `accept`, `defer`, and `reject` outcomes using policy scope,
confidence, durability, reusability, and sensitivity as conservative gates. The
default outcome for ambiguous or weakly specified subagent findings SHALL be
`defer`, not durable write.

#### Scenario: Subagent findings flow through the parent session

- **GIVEN** a subagent returns structured findings from research work
- **WHEN** the parent session accepts those findings
- **THEN** the parent session turns them into checkpoints for durable memory
  review
- **AND** the subagent does not write durable memory directly

#### Scenario: Deferred finding remains transient

- **GIVEN** a subagent returns a plausible finding with medium confidence or weak
  reusability
- **WHEN** the parent session evaluates the finding conservatively
- **THEN** the session marks the finding as deferred
- **AND** the finding does not become a durable checkpoint in MVP-now

#### Scenario: Sensitive finding is rejected by parent policy

- **GIVEN** a subagent returns a finding marked for a disallowed domain or high
  sensitivity class
- **WHEN** the parent session evaluates the finding
- **THEN** the session rejects the finding
- **AND** no durable memory write occurs

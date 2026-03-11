## MODIFIED Requirements

### Requirement: Two-phase memory retrieval

Memory retrieval SHALL run in two modes: automatic pre-turn recall and explicit
two-phase retrieval. Automatic recall SHALL use a deterministic retrieval plan
derived from runtime-owned hard scope, conversation-owned soft scope, and
write-time memory metadata stored in the SQLite memory graph. Explicit
retrieval SHALL continue to use `find_memories` for lightweight search and
`get_memories` for full hydration when manual follow-up is needed. Explicit
retrieval MAY reuse the same deterministic planner with broader allowed memory
classes where policy permits. Automatic recall is the primary retrieval path;
explicit retrieval is a deliberate manual-control path.

#### Scenario: Automatic recall runs before a user-facing turn
- **GIVEN** a user sends a new message into an existing or new session
- **WHEN** the session prepares the next model call
- **THEN** the system builds a deterministic, policy-aware recall plan against durable memory
- **AND** injects a bounded recall bundle before the model sees the turn

#### Scenario: Explicit two-phase retrieval remains available
- **GIVEN** the automatic recall bundle was insufficient or the user explicitly asks what Netclaw remembers
- **WHEN** the frontline model calls `find_memories`
- **THEN** it receives lightweight results suitable for selection
- **AND** can call `get_memories` to fetch full memory bodies only for the selected items

#### Scenario: Routine turn relies on automatic recall first
- **GIVEN** a normal user-facing turn begins
- **WHEN** the automatic recall bundle already provides the relevant durable context
- **THEN** the frontline model does not need to call explicit retrieval tools by default
- **AND** proceeds using the system-managed recall bundle

#### Scenario: Intentional search can search broader classes than automatic recall
- **GIVEN** policy allows searchable supporting material beyond automatic recall defaults
- **WHEN** the user intentionally asks Netclaw to search memory
- **THEN** the explicit retrieval path may include additional allowed memory classes
- **AND** automatic recall still remains bounded to its stricter policy envelope

### Requirement: Automatic pre-turn recall

The system SHALL execute automatic recall before each user-facing model turn
using a deterministic retrieval pipeline over the latest user message, recent
session context, active anchors, runtime-owned hard scope, and policy scope.
Automatic recall SHALL resolve legal scope before search, build a deterministic
request plan, perform cheap candidate selection in SQLite, and rerank or bundle
the resulting candidates without requiring an LLM planner on the hot path.
Automatic recall SHALL be bounded by a latency budget and SHALL degrade safely
when the memory substrate is unavailable.

#### Scenario: Recall completes within budget
- **GIVEN** the memory substrate is healthy
- **WHEN** a new turn begins
- **THEN** the session retrieves and injects a bounded recall bundle before the model call
- **AND** the recall operation completes within the configured time budget or degrades safely

#### Scenario: Recall failure degrades without blocking the turn
- **GIVEN** the memory database is temporarily unavailable
- **WHEN** the session starts automatic recall for a turn
- **THEN** the user-facing turn continues without durable recall injection
- **AND** the session records degraded memory status for diagnostics

#### Scenario: Runtime metadata owns the hard retrieval boundary
- **GIVEN** the current session is bound to a specific Slack or operator context
- **WHEN** automatic recall plans a retrieval request
- **THEN** the legal memory scope comes from runtime metadata and policy configuration
- **AND** prompt semantics only influence soft narrowing within that boundary

#### Scenario: Automatic recall uses write-time retrieval metadata
- **GIVEN** durable memory entries contain anchors, aliases, facets, or bundle slots from write-time extraction
- **WHEN** deterministic recall builds candidates and ranking signals
- **THEN** it uses that stored metadata rather than relying only on raw body-text matches
- **AND** the resulting recall set remains explainable to operators

### Requirement: Memory evaluation and operational criteria

The redesigned memory subsystem SHALL ship with an eval suite and operational
SLOs covering deterministic request planning, recall quality, noise
suppression, privacy behavior, and latency. The implementation SHALL NOT be
considered complete until the seeded eval suite demonstrates the configured
thresholds.

#### Scenario: Seeded memory eval suite passes
- **GIVEN** the seeded recall/privacy fixture suite is executed against the redesigned subsystem
- **WHEN** the results are reported
- **THEN** relevant recall coverage, noise suppression, privacy leakage, and latency metrics meet the thresholds defined by the change design
- **AND** a failing metric blocks rollout from being treated as complete

#### Scenario: Local Ollama eval profile is the primary gate
- **GIVEN** the seeded memory eval suite supports multiple model profiles
- **WHEN** Netclaw validates the redesigned memory subsystem before rollout
- **THEN** it runs the default gate against smaller local Ollama-hosted models
- **AND** passing larger hosted models does not waive a failing local Ollama eval result

#### Scenario: Deterministic retrieval gates pass before default enablement
- **GIVEN** deterministic retrieval is behind a rollout flag
- **WHEN** smoke and realistic retrieval suites run on the default evaluation profiles
- **THEN** request-planning quality, recall precision, noise suppression, and latency meet the configured thresholds for consecutive runs
- **AND** deterministic retrieval is not treated as rollout-ready until those stability gates pass

## ADDED Requirements

### Requirement: Write-time deterministic retrieval metadata contract

The system SHALL persist enough write-time retrieval metadata for deterministic
automatic recall and intentional search to operate without an LLM planner on
the hot path. Each accepted durable memory proposal SHALL include stable memory
class, subject identity, anchor information, aliases, coarse facets, recall
mode, sensitivity, confidence, freshness data, and optional bundle slots or
sparse relations when confidence is high enough.

#### Scenario: Durable fact stores retrieval metadata
- **WHEN** a stable preference or project fact is accepted for durable persistence
- **THEN** the stored memory includes anchor and alias data suitable for future deterministic retrieval
- **AND** the memory also carries policy and freshness metadata required for filtering

#### Scenario: Sparse bundle slots are only stored when meaningful
- **WHEN** a memory item is useful as part of a composite answer bundle
- **THEN** the write path may persist a small number of purposeful bundle slots
- **AND** it does not generate arbitrary or low-confidence slots for every memory item

#### Scenario: Weak memory proposals fail closed
- **WHEN** a memory proposal lacks required retrieval metadata or violates policy classification rules
- **THEN** the deterministic gate rejects or downgrades the proposal before persistence
- **AND** automatic recall does not depend on partially formed metadata being silently accepted

### Requirement: Deterministic retrieval explainability

The memory subsystem SHALL expose explainable retrieval artifacts for operator
diagnostics, including the resolved retrieval scope, request plan, candidate
selection basis, ranking reasons, selected retrieval mode, and degraded reason
codes. These diagnostics SHALL obey the same policy and sensitivity boundaries
as normal memory recall.

#### Scenario: Operator can inspect why a memory was recalled
- **WHEN** an operator reviews a deterministic recall decision through diagnostics
- **THEN** the system can show the relevant request-plan and ranking reasons for the recalled item
- **AND** those reasons do not require replaying an LLM planner response

#### Scenario: Sensitive data is not leaked through retrieval diagnostics
- **WHEN** deterministic retrieval diagnostics are emitted for a memory item with restricted sensitivity
- **THEN** the diagnostics honor the same policy envelope and redaction rules as recall itself
- **AND** unauthorized observers do not receive raw sensitive memory content

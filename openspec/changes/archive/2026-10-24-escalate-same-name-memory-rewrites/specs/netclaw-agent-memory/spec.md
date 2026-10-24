## ADDED Requirements

### Requirement: Same-name document rewrites require a resolved curation decision

Source PRDs: [PRD-007](../../../../../../docs/prd/PRD-007-agent-personality-and-local-memory.md), product outcome 2.
Terms: [engineering glossary](../../../../../../docs/spec/GLOSSARY.md).

The curator SHALL preserve the guard that blocks a direct document overwrite when the proposal omits existing content.
After guard rejection, an exact canonical name SHALL remain evidence of an existing document.
When safe deterministic evidence cannot resolve that proposal, the rules tier SHALL return `Ambiguous`.
The curator SHALL obtain a valid decision from the existing LLM tier before it permits `Create` or a deduplication append.
An unavailable LLM or invalid response SHALL leave the proposal unresolved and block its write through the existing failure path.

A valid LLM `UPDATE` SHALL target the selected document through its canonical `MemoryId`.
The curator SHALL validate the merged content before an overwrite and SHALL use `merge-document` for a valid merge.
A valid LLM `SKIP` SHALL preserve the stored document without a write.
The change SHALL preserve safe deterministic decisions, different-name low-overlap `Create`, audience and boundary scope, and immutable-record admission.

#### Scenario: Same-name rewrite reaches the existing LLM tier

- **GIVEN** one synthetic document and a proposal with the same canonical name
- **AND** the direct overwrite guard rejects the proposal
- **AND** no safe deterministic decision resolves the proposal
- **WHEN** the curator evaluates the proposal
- **THEN** the rules tier returns `Ambiguous`
- **AND** the existing LLM tier receives the proposal and the existing document
- **AND** no `Create` or deduplication append occurs before a valid LLM decision

#### Scenario: Valid LLM update replaces the canonical document body

- **GIVEN** one unresolved same-name proposal and one existing synthetic document
- **WHEN** the existing LLM returns `UPDATE` with a valid merged body for that document
- **THEN** the curator validates the merged body against the source content
- **AND** the write operation contains the existing document's canonical `MemoryId` and `merge-document`
- **AND** the store contains the validated body without a second full copy

#### Scenario: LLM skip preserves the document

- **GIVEN** one unresolved same-name proposal and one existing synthetic document
- **WHEN** the existing LLM returns a valid `SKIP`
- **THEN** the curator emits no write operation
- **AND** the document body and update semantics remain unchanged

#### Scenario: Lossy direct overwrite remains blocked

- **GIVEN** one synthetic document with three independent facts
- **AND** a newer same-name proposal contains only one of those facts
- **WHEN** the deterministic tier proposes a direct update
- **THEN** the overwrite guard rejects that update
- **AND** the curator does not overwrite the document with that narrower body

#### Scenario: Different-name low-overlap proposal still creates a document

- **GIVEN** one synthetic candidate with a different canonical name and low content overlap
- **AND** no other evidence requires an LLM decision
- **WHEN** the rules tier evaluates the proposal
- **THEN** it returns `Create` through the existing deterministic path

#### Scenario: Safe deterministic decisions remain available

- **GIVEN** a proposal that satisfies an existing safe deterministic update or duplicate-skip rule
- **WHEN** the curator evaluates that proposal
- **THEN** it preserves that deterministic decision and its overwrite guard
- **AND** it does not require an extra LLM call solely because the names match

#### Scenario: Missing LLM decision cannot authorize an append

- **GIVEN** an unresolved same-name proposal after overwrite guard rejection
- **WHEN** the LLM is unavailable, fails, or returns an invalid response
- **THEN** the proposal remains unresolved through the existing failure path
- **AND** the curator emits no create, append, or overwrite operation for that proposal

#### Scenario: Immutable records retain their admission rules

- **GIVEN** one admissible synthetic immutable record
- **WHEN** the memory write path evaluates it
- **THEN** the existing record admission rules apply
- **AND** this document rewrite rule does not change its audience or boundary scope

#### Scenario: Unknown decision targets remain unresolved

- **GIVEN** a same-name rewrite that needs an LLM decision
- **WHEN** an `UPDATE` names an unknown target or a `CONSOLIDATE` includes any unknown target
- **THEN** the curator rejects the whole decision before any write or anchor change
- **AND** a stored document outside the evaluated candidate set also counts as unknown

#### Scenario: A malformed keyword prefix cannot resolve a rewrite

- **GIVEN** an unresolved same-name rewrite
- **WHEN** the model returns `CREATE_EXTRA`, `SKIPPER`, or another malformed keyword prefix
- **THEN** the curator rejects the response and reports the unresolved failure
- **AND** the curator preserves the stored memory

#### Scenario: A valid target retains the lossless append fallback

- **GIVEN** an unresolved rewrite and an `UPDATE` that names a valid candidate
- **WHEN** the proposed merged body fails the content preservation check
- **THEN** the curator preserves the existing body and appends the proposal at the existing document identity

#### Scenario: A rejected batch preserves all prior memory

- **GIVEN** a batch with consolidation followed by an unresolved same-name rewrite
- **WHEN** either curation consumer rejects the batch
- **THEN** the consumer reports failure before any persistent effect
- **AND** document bodies, anchor identities, and anchor status stay unchanged
- **AND** the actor accepts a subsequent safe proposal

#### Scenario: Another candidate cannot resolve the rejected same-name document

- **GIVEN** a rejected same-name candidate with low overlap
- **AND** a different-name candidate has higher confidence and content identical to the proposal
- **WHEN** the curator evaluates the proposal
- **THEN** the high-overlap candidate cannot authorize consolidation of the unresolved same-name document
- **AND** the proposal requires the existing LLM decision tier

#### Scenario: An unresolved explicit checkpoint exhausts retries without completion

- **GIVEN** one explicit-request checkpoint for an unresolved same-name rewrite
- **WHEN** the worker exhausts its existing retry policy
- **THEN** durable checkpoint state has retry count 5 and status `failed`
- **AND** the memory stays unchanged and no completion result occurs

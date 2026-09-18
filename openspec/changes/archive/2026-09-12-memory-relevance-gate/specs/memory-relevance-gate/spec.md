# Spec: memory-relevance-gate (new capability)

## ADDED Requirements

### Requirement: In-process cross-encoder relevance scoring

The system SHALL score floor-surviving recall candidates against the query
with an in-process CPU ONNX cross-encoder — no sidecar processes, no network
inference hop, mirroring the embedding runtime's execution model. The scorer
SHALL sit behind a narrow interface owned by the memory subsystem so actor
code carries no ONNX dependency, SHALL preserve candidate order across a
batch call, and SHALL encode each `(query, candidate)` pair jointly (not as
two independently embedded vectors) so the score reflects usefulness for
answering the query rather than topical similarity alone.

#### Scenario: Candidates score without external services

- **GIVEN** a healthy daemon with the relevance model provisioned
- **WHEN** automatic recall has floor-surviving candidates to gate
- **THEN** each candidate is scored in-process against the query
- **AND** no network call or child process is involved in scoring

#### Scenario: Query text is never truncated to fit a candidate

- **GIVEN** a floor-surviving candidate whose combined length with the query
  exceeds the model's maximum sequence length
- **WHEN** the pair is encoded for scoring
- **THEN** the candidate side is truncated to fit
- **AND** the query side is preserved in full

### Requirement: Relevance model provisioning carries a calibrated operating point

The relevance model SHALL be provisioned through the same pinned-allowlist
mechanism as other memory-subsystem models (id → download URL, byte size,
SHA-256, arbitrary URLs rejected), and its manifest entry SHALL additionally
carry a calibrated similarity threshold that travels with the model id. A
relevance model SHALL NOT be usable with a threshold calibrated for a
different model id.

#### Scenario: Calibrated threshold ships with the model id

- **GIVEN** the relevance model manifest entry for the active model id
- **WHEN** the recall coordinator applies the gate
- **THEN** it uses the threshold carried by that manifest entry unless the
  operator has configured an explicit override
- **AND** no separate operator calibration step is required to get a
  working default

#### Scenario: Hash mismatch refuses the relevance model

- **GIVEN** a downloaded relevance model artifact whose SHA-256 does not
  match the allowlist entry
- **WHEN** provisioning verifies the artifact
- **THEN** the artifact is discarded and not loaded
- **AND** the gate reports unavailable rather than scoring with an unverified
  artifact

### Requirement: Post-floor relevance gate on automatic recall

After the existing absolute cosine floor admits candidates, the system SHALL
score each surviving candidate (bounded to the automatic recall item limit)
against the query and SHALL drop any candidate whose score falls below the
active threshold. When every floor-surviving candidate is dropped by the
gate, the turn SHALL inject nothing, identical in kind to the existing
zero-survivors-at-the-floor outcome. The gate SHALL run under its own latency
sub-budget nested inside the overall recall timeout.

#### Scenario: Topically-adjacent but unhelpful candidate is rejected

- **GIVEN** a floor-surviving candidate whose cosine similarity to the query
  clears the absolute floor but whose content does not help answer the query
- **WHEN** the relevance gate scores the candidate
- **THEN** the candidate scores below the active threshold
- **AND** the candidate is dropped before injection

#### Scenario: Genuinely relevant candidate survives the gate

- **GIVEN** a floor-surviving candidate that directly answers the query
- **WHEN** the relevance gate scores the candidate
- **THEN** the candidate scores above the active threshold
- **AND** the candidate remains eligible for injection

#### Scenario: All candidates gated out means nothing injected

- **GIVEN** every floor-surviving candidate for a turn scores below the
  active threshold
- **WHEN** automatic recall completes for that turn
- **THEN** no memory items are injected
- **AND** the recall context block is omitted entirely from the prompt

### Requirement: Loud degradation without silent fallback

Automatic recall SHALL degrade to the floor-only result, unfiltered by the
relevance gate, when the relevance model is unavailable (not provisioned,
hash verification failed, runtime load error) or the gate exceeds its
per-turn sub-budget. The degraded state SHALL be loud: a doctor check
reports the cause, and a rate-limited structured log event records the
degradation reason. The system SHALL NOT silently apply or silently skip
gating without one of these signals.

#### Scenario: Missing relevance model degrades to floor-only, loudly

- **GIVEN** the relevance model is not provisioned
- **WHEN** a turn triggers automatic recall with floor-surviving candidates
- **THEN** recall injects the floor's own result unfiltered by any gate
- **AND** a rate-limited degradation event is logged
- **AND** `netclaw doctor` reports the missing relevance model with
  remediation

#### Scenario: Gate sub-budget timeout degrades to floor-only

- **GIVEN** the relevance model is available but scoring exceeds its
  configured sub-budget for a turn
- **WHEN** the sub-budget elapses
- **THEN** the gate stops waiting and recall injects the floor's own result
  unfiltered for that turn
- **AND** the degradation is logged at a rate-limited interval, not on every
  occurrence

### Requirement: Gate activation follows embedding enablement

The relevance gate SHALL be active whenever automatic embeddings are
enabled, without requiring a separate operator decision, while still
allowing an explicit override in either direction. The active similarity
threshold SHALL default to the value carried by the provisioned model's
manifest entry, while allowing an explicit operator override.

#### Scenario: Enabling embeddings enables the gate with no extra configuration

- **GIVEN** an operator enables automatic memory embeddings with no gate
  configuration present
- **WHEN** the daemon starts
- **THEN** the relevance gate is active using the manifest-provided
  threshold for the provisioned relevance model

#### Scenario: Explicit override disables the gate independent of embeddings

- **GIVEN** automatic memory embeddings are enabled
- **AND** the operator has explicitly disabled the relevance gate
- **WHEN** automatic recall runs
- **THEN** hybrid recall with the absolute cosine floor still applies
- **AND** no candidate is scored or dropped by the relevance gate

### Requirement: Gate decisions are observable in retrieval logging and evals

The final retrieval log record for a turn SHALL include the relevance score
computed for each gated candidate and the count of candidates dropped by the
gate. The eval suite SHALL include a case that seeds a memory corpus, poses
an off-topic query, and asserts both that no recall context block is added
to the prompt and that a gate-degradation-or-decision marker is present in
the logs for that turn.

#### Scenario: Retrieval log records gate scores and drop count

- **GIVEN** a turn where the relevance gate scored and dropped at least one
  floor-surviving candidate
- **WHEN** the final retrieval log line is written
- **THEN** it includes the score computed for each gated candidate
- **AND** it includes the count of candidates the gate dropped

#### Scenario: Zero-injection eval case passes on an off-topic query

- **GIVEN** a seeded memory corpus with no content relevant to a specific
  off-topic question
- **WHEN** the eval case asks that question
- **THEN** the assembled prompt contains no `[memory-recall]` block
- **AND** the turn's logs contain a relevance-gate marker for the decision

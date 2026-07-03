# Spec: memory-embeddings (new capability)

## ADDED Requirements

### Requirement: In-process embedding runtime

The system SHALL compute memory embeddings in-process with a CPU ONNX runtime
and a managed tokenizer — no sidecar processes, no network inference hop.
Embedding components SHALL sit behind a narrow interface owned by the memory
subsystem so actor code carries no ONNX dependency, and the runtime SHALL
support both linux-x64 and linux-arm64.

#### Scenario: Embeddings compute without external services

- **GIVEN** a healthy daemon with the embedding model provisioned
- **WHEN** a memory document is written
- **THEN** its embedding is computed in-process
- **AND** no network call or child process is involved in inference

### Requirement: Pinned model provisioning

The embedding model SHALL be selected by id from a pinned in-code allowlist
mapping model id to download URL, byte size, and SHA-256. Arbitrary model URLs
SHALL be rejected. Provisioning SHALL download atomically (temporary file then
rename), verify the hash before load, and run at daemon initialization when
auto-download is enabled or on explicit operator command. The model artifact
SHALL NOT be embedded in the application binary.

#### Scenario: Hash mismatch refuses the model

- **GIVEN** a downloaded model artifact whose SHA-256 does not match the
  allowlist entry
- **WHEN** provisioning verifies the artifact
- **THEN** the artifact is discarded and not loaded
- **AND** the failure is surfaced as a doctor-visible error

#### Scenario: Unknown model id is rejected

- **GIVEN** configuration naming a model id absent from the allowlist
- **WHEN** the daemon initializes embeddings
- **THEN** provisioning refuses with a configuration error identifying the
  allowlisted ids

### Requirement: Embed-on-write with derived backfill state

Every recallable memory document SHALL receive an embedding keyed by
`(item id, model id)` with a content hash of its normalized text. Writes SHALL
embed after commit; a startup gap-repair sweep SHALL embed any item missing a
current-model embedding. Re-embedding SHALL be skipped when the content hash
is unchanged. Backfill progress SHALL be derived from the store (items lacking
a current-model embedding), never tracked in separate mutable state. Vectors
are derived data: loss or deletion of embeddings SHALL be recoverable by
re-embedding without any loss of memory content.

#### Scenario: Crash between write and embed self-heals

- **GIVEN** a document committed whose embedding upsert was interrupted
- **WHEN** the daemon next starts and the gap-repair sweep runs
- **THEN** the missing embedding is computed and stored
- **AND** the embedding doctor check reports full coverage afterward

#### Scenario: Model change re-embeds without data loss

- **GIVEN** a corpus embedded under model A
- **WHEN** the operator switches configuration to allowlisted model B and runs
  a forced backfill
- **THEN** embeddings for model B are created alongside or replacing model A's
- **AND** memory content is unmodified

### Requirement: Loud degradation without silent fallback

Memory recall and curation SHALL continue on their lexical paths when the
embedding model is missing, corrupt, or the runtime fails, and the degraded
state SHALL be loud: a doctor check reports the cause, the daemon runtime
status reports embeddings as degraded, and recall/curation log structured
degradation events. The system SHALL NOT silently revert to lexical behavior
without these signals.

#### Scenario: Missing model degrades loudly

- **GIVEN** auto-download is disabled and no model artifact is present
- **WHEN** the daemon starts and a turn triggers recall
- **THEN** recall serves lexical-only results
- **AND** daemon status reports embeddings degraded
- **AND** `netclaw doctor` reports the missing model as an error with
  remediation

### Requirement: Embedding coverage diagnostics

A doctor check SHALL report embedding provisioning state and corpus coverage:
model present and hash-valid, count of items lacking current-model embeddings,
and a warning when embeddings exist under multiple model ids (mixed-model
corpus invalidates similarity thresholds).

#### Scenario: Mixed-model corpus warns

- **GIVEN** embeddings stored under two different model ids
- **WHEN** the embedding doctor check runs
- **THEN** it warns that similarity thresholds are calibrated per model
- **AND** recommends a forced backfill under the active model

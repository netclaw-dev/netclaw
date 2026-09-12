# memory-embeddings Specification

## Purpose

Define the in-process embedding runtime, pinned model provisioning, derived
embedding storage, and loud degradation behavior for local memory retrieval.

## Requirements

### Requirement: In-process embedding runtime

The system SHALL compute memory embeddings in-process with a CPU ONNX runtime
and a managed tokenizer. It SHALL use no sidecar process or network inference
hop. Embedding components SHALL sit behind a narrow interface owned by the
memory subsystem so actor code carries no ONNX dependency. The runtime SHALL
support linux-x64 and linux-arm64.

#### Scenario: Embeddings compute without external services

- **GIVEN** a healthy daemon with the embedding model provisioned
- **WHEN** a memory document is written
- **THEN** its embedding is computed in-process
- **AND** no network call or child process is involved in inference

### Requirement: Pinned model provisioning

Memory-subsystem models SHALL be selected by id from a pinned in-code
allowlist mapping model id to download URL, byte size, and SHA-256, covering
more than one kind of model artifact (embedding models and relevance-scoring
models share the same allowlist mechanism). A relevance-model manifest entry
SHALL additionally carry a calibrated similarity threshold alongside its
download and verification fields, so a model's operating point travels with
its id rather than living as a disconnected configuration default. Arbitrary
model URLs SHALL be rejected for every manifest kind. Provisioning SHALL
download atomically (temporary file then rename), verify the hash before
load, and run at daemon initialization when auto-download is enabled or on
explicit operator command. No model artifact SHALL be embedded in the
application binary.

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

#### Scenario: Relevance manifest entry's threshold travels with its model id

- **GIVEN** an allowlisted relevance-model manifest entry carrying a
  calibrated threshold
- **WHEN** that model id is provisioned and becomes active
- **THEN** the calibrated threshold from that same manifest entry is what
  governs gating, not a threshold associated with any other model id
- **AND** switching to a different allowlisted relevance-model id switches
  the effective threshold to that id's own calibrated value

### Requirement: Embed-on-write with derived backfill state

Every recallable memory document SHALL receive an embedding keyed by
`(item id, model id)` with a content hash of its normalized text. Writes SHALL
embed after commit. A startup gap-repair sweep SHALL embed any item missing a
current-model embedding. Re-embedding SHALL be skipped when the content hash is
unchanged. Backfill progress SHALL be derived from the store. Vectors SHALL be
recoverable derived data, so their loss or deletion SHALL not lose memory content.

#### Scenario: Crash between write and embed self-heals

- **GIVEN** a document was committed and its embedding upsert was interrupted
- **WHEN** the daemon next starts and the gap-repair sweep runs
- **THEN** the missing embedding is computed and stored
- **AND** the embedding doctor check reports full coverage afterward

#### Scenario: Model change re-embeds without data loss

- **GIVEN** a corpus was embedded under model A
- **WHEN** the operator switches to allowlisted model B and runs a forced backfill
- **THEN** embeddings for model B are created alongside or replace model A's embeddings
- **AND** memory content is unmodified

### Requirement: Loud degradation without silent fallback

Memory recall and curation SHALL continue on lexical paths when the embedding
model is missing, corrupt, or unavailable. A doctor check SHALL report the
cause, daemon status SHALL report embeddings as degraded, and recall or curation
SHALL log structured degradation events. The system SHALL NOT silently revert to
lexical behavior without these signals.

#### Scenario: Missing model degrades loudly

- **GIVEN** auto-download is disabled and no model artifact is present
- **WHEN** the daemon starts and a turn triggers recall
- **THEN** recall serves lexical-only results
- **AND** daemon status reports embeddings degraded
- **AND** `netclaw doctor` reports the missing model as an error with remediation

### Requirement: Embedding coverage diagnostics

A doctor check SHALL report embedding provisioning state and corpus coverage.
It SHALL report whether the model is present and hash-valid, the count of items
that lack current-model embeddings, and a warning when embeddings exist under
multiple model ids.

#### Scenario: Mixed-model corpus warns

- **GIVEN** embeddings are stored under two different model ids
- **WHEN** the embedding doctor check runs
- **THEN** it warns that similarity thresholds are calibrated per model
- **AND** it recommends a forced backfill under the active model

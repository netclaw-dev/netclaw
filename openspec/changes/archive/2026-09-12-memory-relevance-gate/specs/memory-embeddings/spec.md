# Delta: memory-embeddings (memory-relevance-gate)

## MODIFIED Requirements

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

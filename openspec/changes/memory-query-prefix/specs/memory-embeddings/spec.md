# Delta: memory-embeddings (memory-query-prefix)

Base: this capability's base spec is the pending delta in
`openspec/changes/memory-core-redesign/specs/memory-embeddings/spec.md`
(not yet archived to main specs); the requirements below are ADDED on top
of it.

## ADDED Requirements

### Requirement: Purpose-distinguished embedding

The embedding runtime SHALL distinguish retrieval-query embedding from
passage (document) embedding at its interface, and SHALL apply the active
model's documented retrieval query encoding — including any model-documented
query prefix — only to retrieval-query inputs. Document-side embedding
(embed-on-write, backfill, gap repair, and duplicate nomination) SHALL remain
in the model's document mode, byte-compatible with vectors already stored, so
adopting a query prefix SHALL NOT require re-embedding any stored content.

#### Scenario: Recall query is embedded with the model's documented prefix

- **GIVEN** the active embedding model's manifest documents a retrieval query
  prefix
- **WHEN** the recall pipeline embeds a turn query
- **THEN** the embedded text is the documented prefix followed by the query
- **AND** the resulting vector is produced by the same session, pooling, and
  normalization as document embeddings

#### Scenario: Document-side embedding is unaffected by the prefix

- **GIVEN** a corpus embedded before query-prefix support existed
- **WHEN** embed-on-write, backfill, or duplicate nomination embeds a
  document or proposal
- **THEN** no prefix is applied
- **AND** the produced vectors are interchangeable with the pre-existing
  stored vectors (no re-embed required)

### Requirement: Manifest-carried retrieval calibration

Each allowlisted embedding model entry SHALL carry its retrieval-mode
metadata: the documented query prefix (empty when the model documents none)
and the retrieval floor calibrated for that model in that encoding mode.
Runtime floor resolution SHALL prefer an explicit configuration override and
otherwise use the manifest calibration; components SHALL NOT hardcode floors
calibrated for a specific model or encoding mode outside the manifest.

#### Scenario: Calibration travels with the model entry

- **GIVEN** two allowlisted model variants with different calibrated floors
- **WHEN** the configured model id switches between them
- **THEN** the effective default floor changes to the newly active entry's
  calibration without any code or configuration change

#### Scenario: Prefix and floor cannot be recombined incorrectly by default

- **GIVEN** a model entry whose calibration was measured with its documented
  query prefix
- **WHEN** the runtime activates that entry with no explicit floor override
- **THEN** the prefixed encoding and its matching calibrated floor are applied
  together

# Spec: memory-maintenance (new capability)

## ADDED Requirements

### Requirement: Embedding backfill command

The CLI SHALL provide `netclaw memory backfill-embeddings` to provision the
model if needed and embed every item lacking a current-model embedding, with a
`--force` mode that re-embeds everything under the active model. Backfill
SHALL be safe against a live daemon (small batched writes under WAL) and SHALL
report progress and a final coverage summary.

#### Scenario: Backfill completes coverage

- **GIVEN** a corpus with items lacking current-model embeddings
- **WHEN** the operator runs the backfill command
- **THEN** all recallable items receive embeddings under the active model
- **AND** the command reports counts embedded, skipped (hash-unchanged), and
  failed

### Requirement: Ratified consolidation with dry-run plan files

Corpus consolidation SHALL be two-phase and operator-gated. A dry-run SHALL
build near-duplicate clusters by embedding similarity, synthesize a proposed
lossless merge per cluster, and write a human-editable plan file plus a
readable report — with no database mutation. An apply run SHALL execute a
previously written plan file verbatim (operators veto by editing or deleting
plan entries), SHALL refuse to run against a live daemon by default, SHALL
take a database backup before mutating, and SHALL re-embed merged results and
rebuild affected search rows. Every apply SHALL be recorded in a maintenance
ledger.

#### Scenario: Dry-run mutates nothing

- **GIVEN** a corpus containing near-duplicate clusters
- **WHEN** the operator runs consolidation in dry-run mode
- **THEN** a plan file and report are produced
- **AND** the database bytes are unchanged

#### Scenario: Apply executes only the ratified plan

- **GIVEN** a reviewed plan file with one cluster entry deleted by the
  operator
- **WHEN** the operator runs apply with that plan
- **THEN** a backup of the database is created first
- **AND** the deleted entry's cluster is left untouched
- **AND** the remaining entries are applied and recorded in the maintenance
  ledger

#### Scenario: Apply refuses a live daemon

- **GIVEN** the daemon is running
- **WHEN** the operator runs consolidation apply without the explicit
  live-override flag
- **THEN** the command refuses and names the running daemon

### Requirement: Expiry sweep deletes expired rows

The system SHALL periodically delete memory rows whose expiry has passed
beyond a grace window. Expired rows are already excluded from every recall and
search surface, so deletion SHALL be behavior-neutral for reads; each sweep
SHALL log the number of rows removed per class.

#### Scenario: Expired evidence is physically removed

- **GIVEN** evidence records whose expiry passed beyond the grace window
- **WHEN** the maintenance sweep runs
- **THEN** those rows are deleted from the store
- **AND** the sweep logs the per-class deletion counts

### Requirement: Memory status surface

The CLI SHALL provide `netclaw memory status` reporting corpus composition
(counts by class and recall mode), embedding coverage for the active model,
pending checkpoints, expired-row counts awaiting sweep, and the most recent
maintenance-ledger entries.

#### Scenario: Operator inspects corpus health

- **GIVEN** a daemon with a populated memory store
- **WHEN** the operator runs the status command
- **THEN** it reports class/recall-mode counts, embedding coverage, pending
  checkpoints, and recent maintenance runs

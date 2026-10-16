## MODIFIED Requirements

### Requirement: Job deduplication

The session SHALL maintain dedup state for job deliveries to prevent double-
processing if a delivery is retried. The dedup key SHALL be the job ID. The
ledger SHALL be an in-memory set that journal replay rebuilds.

#### Scenario: Duplicate delivery ignored

- **GIVEN** a job result has already been delivered and processed
- **WHEN** the same delivery is retried (e.g., after a crash recovery)
- **THEN** the session recognizes the job ID as already processed
- **AND** the duplicate delivery is acknowledged without re-processing

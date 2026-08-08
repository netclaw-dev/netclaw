## ADDED Requirements

### Requirement: Soft deletion retains reminder history

Netclaw SHALL retain execution history when it soft-deletes a completed or failed one-shot. Only an explicit delete command SHALL remove the history file.

#### Scenario: Completed one-shot retains history

- **GIVEN** a one-shot has a successful execution record
- **WHEN** Netclaw disables it with outcome `Completed`
- **THEN** the definition and history file remain present

#### Scenario: Failed one-shot retains history

- **GIVEN** a one-shot reaches its poison threshold
- **WHEN** Netclaw disables it with outcome `Failed`
- **THEN** all failure records remain available through reminder history

#### Scenario: Execution actor stops before it reports an outcome

- **GIVEN** a reminder execution actor stops before manager acceptance
- **WHEN** DeathWatch reports the stop
- **THEN** the manager appends a failed execution record
- **AND** the failure record identifies the unexpected stop

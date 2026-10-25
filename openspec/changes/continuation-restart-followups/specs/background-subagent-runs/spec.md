## ADDED Requirements

### Requirement: A refused park or resume ends with the true reason

When the session refuses the resume of a parked run after it read the answers of the park, no parked call SHALL run, and the session SHALL treat each answer as unused. A later click on a prompt of that park SHALL get the withdrawal reason of the run end, and SHALL NOT get "already answered". A refused resume includes a damaged park, a start that the session cannot prepare, and answers that do not match the requests of the park. When the journal rejects the park record of a drain park, the session SHALL end the run as a restart loss: `Partial` with the reason `owner_restarted` and the restart-loss text that names the effects as uncertain. The run end SHALL NOT say that the parked calls did not run, because the batch of a drain park ran.

#### Scenario: A late click after a damaged park gets the withdrawal reason

- **GIVEN** a parked child whose recorded loop-guard state has an equal-round count of 7
- **WHEN** the requester approves the parked request, and the run ends with the reason `park_damaged`
- **AND** the requester clicks the same prompt again
- **THEN** the session replies with the withdrawal reason and not "already answered"
- **AND** the parked call does not run

#### Scenario: A rejected drain park ends as a restart loss

- **GIVEN** a child run whose tool batch runs when a graceful drain starts
- **WHEN** the batch ends, and the journal rejects the park record of the drain park
- **THEN** the run ends `Partial` with the reason `owner_restarted` and the restart-loss text
- **AND** the run result does not say "The parked calls did not run."

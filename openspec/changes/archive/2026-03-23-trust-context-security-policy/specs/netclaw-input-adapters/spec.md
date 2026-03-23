## MODIFIED Requirements

### Requirement: Source metadata on all commands

All inbound `SendUserMessage` commands SHALL carry source metadata sufficient for ACL evaluation, trust-context derivation, and audit logging. Source metadata SHALL include adapter type, sender identity, channel identifier, timestamp, source audience, principal classification, and provenance fields needed to distinguish verified transport from tainted payload content.

#### Scenario: Slack source metadata populated

- **GIVEN** a Slack message event is received
- **WHEN** the Slack adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `slack`
- **AND** includes the Slack user ID as sender identity
- **AND** includes the Slack channel ID
- **AND** includes the event timestamp
- **AND** includes the resolved source audience for the channel/principal combination

#### Scenario: Timer source metadata populated

- **GIVEN** an Akka timer fires for a scheduled task
- **WHEN** the timer adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `timer`
- **AND** includes the task creator as sender identity
- **AND** includes the task ID as the channel equivalent
- **AND** includes the timer fire timestamp

#### Scenario: ACL uses source metadata for evaluation

- **GIVEN** a `SendUserMessage` command arrives with source metadata
- **WHEN** the ACL gate evaluates the command
- **THEN** the evaluation uses the sender identity from source metadata
- **AND** the evaluation uses the channel identifier from source metadata

#### Scenario: Verified webhook records payload taint separately

- **GIVEN** a future webhook adapter receives a signed event from a public repository
- **WHEN** the adapter creates the `SendUserMessage` command
- **THEN** the source metadata records transport authenticity as verified
- **AND** the payload provenance marks public user-controlled text as tainted for trust-context derivation

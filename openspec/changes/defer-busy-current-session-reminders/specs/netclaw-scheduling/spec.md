## ADDED Requirements

### Requirement: Transient CurrentSession admission deferral

The CurrentSession delivery path SHALL defer a trusted reminder before queue admission when the target session cannot start a distinct turn. Deferral SHALL use the original Akka.Reminders occurrence and SHALL consume one Akka delivery attempt.

#### Scenario: Busy session defers before queue admission

- **GIVEN** a supported CurrentSession binding has an active turn
- **WHEN** the binding receives `DeliverTrustedSessionTurn`
- **THEN** it replies with `CommandDeferred`
- **AND** it does not register a delivery observer
- **AND** it does not write the reminder to the session queue

#### Scenario: Successful admission marks the binding busy

- **GIVEN** a supported CurrentSession binding has no active turn
- **WHEN** it admits a trusted reminder to the session queue
- **THEN** it marks the turn as active before it handles another admission
- **AND** it clears the active state after `TurnCompleted` or pipeline reset

#### Scenario: Supported gateway has not registered

- **GIVEN** a CurrentSession reminder has a supported origin channel type
- **AND** that channel gateway has not registered after daemon startup
- **WHEN** the execution actor resolves the gateway
- **THEN** it reports a transient deferral
- **AND** it does not report an unsupported channel error

#### Scenario: Unsupported origin remains a failure

- **GIVEN** a CurrentSession reminder has an unsupported origin channel type
- **WHEN** the execution actor validates the origin
- **THEN** it reports a permanent execution failure

### Requirement: Deferred occurrence settlement

The reminder manager SHALL settle a transient admission deferral through `IReminderClient.NackAsync`. It SHALL separate an available scheduler retry from a terminal retry result.

#### Scenario: Scheduler accepts the deferral

- **GIVEN** a CurrentSession execution reports a transient deferral
- **WHEN** `NackAsync` returns `RetryScheduled`
- **THEN** the manager releases the active execution
- **AND** it does not append a failed history record
- **AND** it does not increment `ConsecutiveFailures`
- **AND** it does not emit a reminder failure alert
- **AND** reminder status exposes the scheduler's next attempt

#### Scenario: Deferral exhausts the retry budget

- **GIVEN** a CurrentSession execution reports a transient deferral
- **WHEN** `NackAsync` returns `Failed` or `Expired`
- **THEN** the manager records one failed history entry
- **AND** it increments `ConsecutiveFailures` once
- **AND** it applies the existing terminal occurrence policy

#### Scenario: Retry enters an idle session

- **GIVEN** Akka.Reminders retries a deferred CurrentSession occurrence
- **AND** the target session is idle
- **WHEN** the binding admits the reminder
- **THEN** the reminder runs as a distinct turn
- **AND** `TurnCompleted.SourceReminderId` contains the stable occurrence key

#### Scenario: Accepted-turn delivery fails

- **GIVEN** a CurrentSession reminder was admitted as a distinct turn
- **WHEN** its channel reports `ReminderDeliveryResult.Delivered` as false
- **THEN** the manager records the result through the existing failure path

### Requirement: CurrentSession deferral channel parity

Slack, Discord, Mattermost, SignalR, and TUI session delivery SHALL apply the same transient deferral contract.

#### Scenario: Each supported binding rejects concurrent reminder admission

- **GIVEN** any supported CurrentSession channel binding has an active turn
- **WHEN** another CurrentSession reminder targets that binding
- **THEN** the binding replies with `CommandDeferred`
- **AND** the scheduler owns the next attempt

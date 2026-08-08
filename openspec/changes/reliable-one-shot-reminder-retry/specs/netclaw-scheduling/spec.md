## MODIFIED Requirements

### Requirement: Failure handling and guardrails

The reminder manager SHALL store consecutive failures in each reminder definition. A successful execution SHALL reset the count.

The manager SHALL disable a reminder when the count reaches `FailurePauseThreshold`. The disabled definition SHALL remain available for status and diagnosis.

The manager SHALL enforce `MaxConcurrentExecutions`. It SHALL apply the bounded capacity policy when no execution slot is available.

Each execution SHALL have a one-hour absolute limit. A known timeout SHALL count as a failed attempt.

#### Scenario: Consecutive failures disable a reminder

- **GIVEN** a reminder has one fewer failure than `FailurePauseThreshold`
- **WHEN** its next execution fails
- **THEN** the manager saves the threshold failure count
- **AND** the manager disables the reminder
- **AND** the definition remains available

#### Scenario: A successful execution resets the failure count

- **GIVEN** a reminder has one or more consecutive failures
- **WHEN** its next execution succeeds
- **THEN** the manager saves a zero failure count

#### Scenario: The execution limit is full

- **GIVEN** `MaxConcurrentExecutions` reminder attempts are active
- **WHEN** another occurrence arrives
- **THEN** the manager does not retain the envelope in a queue
- **AND** the manager applies the one-shot or reminder-series capacity policy

### Requirement: Envelope-ack-gated at-least-once delivery for Mode B

The reminder manager SHALL retain each Akka.Reminders envelope until the attempt has a known outcome. This rule SHALL apply to every delivery kind.

The execution actor SHALL report its outcome to the manager. It SHALL wait for `ReminderExecutionAccepted` before it stops.

The manager SHALL acknowledge only a successful execution with all required delivery evidence. It SHALL negatively acknowledge a known failure.

A `CurrentSession` reminder SHALL still use the origin gateway and `Ask<CommandAck>`. Required delivery SHALL also wait for `ReminderDeliveryResult`.

The target session SHALL keep its best-effort reminder key check. The key SHALL use the stable occurrence due time.

#### Scenario: CurrentSession requires observed delivery

- **GIVEN** a `CurrentSession` reminder has `DeliveryRequired = true`
- **WHEN** the target session returns `CommandAck`
- **THEN** the execution remains incomplete
- **WHEN** a matching successful `ReminderDeliveryResult` arrives
- **THEN** the child reports success to the manager
- **AND** the manager acknowledges the occurrence

#### Scenario: CurrentSession delivery fails

- **GIVEN** a `CurrentSession` reminder awaits required delivery
- **WHEN** the gateway rejects the turn or delivery fails
- **THEN** the child reports a descriptive failure
- **AND** the manager sends a negative acknowledgement

#### Scenario: Channel execution fails

- **GIVEN** a `Channel` reminder starts an isolated execution
- **WHEN** the execution or notification fails
- **THEN** the manager does not acknowledge success
- **AND** the manager sends a negative acknowledgement

#### Scenario: None delivery succeeds

- **GIVEN** a reminder uses `Delivery.Kind = None`
- **WHEN** its execution completes successfully
- **THEN** the manager acknowledges the occurrence

#### Scenario: The child reports success before it stops

- **GIVEN** an execution child reports success
- **WHEN** the manager saves local state and acknowledges the occurrence
- **THEN** the manager sends `ReminderExecutionAccepted`
- **AND** the child stops after that message

### Requirement: Reminder delivery guarantees

The reminder pipeline SHALL provide at-least-once attempt delivery until the manager confirms execution and required delivery success.

A crash before acknowledgement SHALL leave the occurrence eligible for retry. A crash after acknowledgement SHALL not lose successful work.

The stable occurrence identity and the session reminder key SHALL reduce duplicate work. Netclaw SHALL not claim exactly-once delivery.

#### Scenario: The daemon stops during execution

- **GIVEN** a reminder attempt has not reached manager acknowledgement
- **WHEN** the daemon stops
- **THEN** the acknowledgement lease expires
- **AND** Akka.Reminders can retry the occurrence

#### Scenario: The daemon stops after acknowledgement

- **GIVEN** execution and required delivery succeeded
- **AND** the manager acknowledged the occurrence
- **WHEN** the daemon stops before one-shot terminal state is saved
- **THEN** durable occurrence status remains `Delivered`
- **AND** reconciliation repairs the one-shot terminal state

## ADDED Requirements

### Requirement: Execution outcome controls occurrence acknowledgement

Netclaw SHALL pass the Akka.Reminders envelope to every reminder execution. Netclaw SHALL acknowledge an occurrence only after successful execution and required delivery.

Netclaw SHALL send a negative acknowledgement after a known execution or delivery failure. The negative acknowledgement SHALL use the library retry budget.

The reminder manager SHALL accept the execution result before the child stops. DeathWatch SHALL report failure only before result acceptance.

#### Scenario: Channel execution fails before delivery

- **GIVEN** an enabled channel reminder occurrence is awaiting acknowledgement
- **WHEN** its session fails before required delivery succeeds
- **THEN** Netclaw sends a negative acknowledgement with the failure reason
- **AND** Netclaw does not send a successful acknowledgement
- **AND** Akka.Reminders persists the next attempt or a terminal state

#### Scenario: Execution and required delivery succeed

- **GIVEN** an enabled reminder occurrence is awaiting acknowledgement
- **WHEN** execution and required delivery succeed
- **THEN** Netclaw acknowledges the exact occurrence
- **AND** Akka.Reminders records `Delivered`

### Requirement: Reminder-level poison state is durable

Netclaw SHALL persist a consecutive execution failure count in the reminder definition. Each failed attempt SHALL increment the count, and a successful attempt SHALL reset it.

Netclaw SHALL disable the complete reminder when the count reaches `FailurePauseThreshold`. This count SHALL remain separate from the Akka.Reminders per-occurrence attempt count.

#### Scenario: Restart preserves the poison count

- **GIVEN** a reminder has three consecutive failed attempts
- **WHEN** the daemon restarts
- **THEN** reminder status reports three consecutive failures
- **AND** the next failed attempt increments the count to four

#### Scenario: Success resets the poison count

- **GIVEN** a reminder has one or more consecutive failed attempts
- **WHEN** a later attempt succeeds
- **THEN** Netclaw persists a zero consecutive failure count

#### Scenario: Fifth failure disables the complete reminder

- **GIVEN** a reminder has four consecutive failed attempts
- **WHEN** the next attempt fails
- **THEN** Netclaw disables the reminder
- **AND** Netclaw records a failed terminal outcome
- **AND** Netclaw cancels future occurrences for the complete reminder

### Requirement: One-shot reminders use soft deletion

Netclaw SHALL retain a one-shot definition after success or terminal failure. Netclaw SHALL disable the definition and record its terminal outcome.

Only an explicit delete command SHALL remove the definition and history.

#### Scenario: Successful one-shot remains inspectable

- **GIVEN** a one-shot reminder succeeds
- **WHEN** Netclaw completes its acknowledgement
- **THEN** Netclaw disables the definition with outcome `Completed`
- **AND** an all-reminders query returns the definition

#### Scenario: Failed one-shot remains enabled for retry

- **GIVEN** a one-shot attempt fails below the poison threshold
- **WHEN** Akka.Reminders schedules another attempt
- **THEN** Netclaw keeps the definition enabled
- **AND** reminder status shows the durable attempt state

#### Scenario: Reconciliation retains a past one-shot

- **GIVEN** a one-shot has a past fire time
- **WHEN** reconciliation finds no active schedule
- **THEN** reconciliation does not delete the definition or history
- **AND** reconciliation uses durable occurrence state to select restoration or a terminal soft delete

### Requirement: Reminder attempts have bounded acknowledgement leases

Netclaw SHALL use a one-hour absolute execution limit and a 70-minute Akka.Reminders acknowledgment timeout. It SHALL retain the 20-minute inactivity limit.

#### Scenario: Valid long execution completes within the lease

- **GIVEN** a reminder execution produces activity and completes within one hour
- **WHEN** required delivery succeeds
- **THEN** Netclaw acknowledges the occurrence before its 70-minute deadline

#### Scenario: Execution reaches the absolute limit

- **GIVEN** a reminder execution remains active for one hour
- **WHEN** the absolute limit expires
- **THEN** Netclaw stops the attempt
- **AND** Netclaw sends a negative acknowledgement

#### Scenario: The remaining lease cannot contain an attempt

- **GIVEN** an occurrence has less than the maximum attempt duration plus the settlement margin remaining
- **WHEN** Netclaw considers the occurrence for execution
- **THEN** Netclaw does not start the execution
- **AND** Netclaw settles the occurrence by its one-shot or reminder-series capacity policy

### Requirement: Capacity settlement remains bounded

Netclaw SHALL NOT retain blocked Akka.Reminders envelopes in an in-memory catch-up queue.

Netclaw SHALL negatively acknowledge a blocked one-shot occurrence. Netclaw SHALL acknowledge and skip a blocked reminder-series occurrence.

Netclaw SHALL ignore an exact duplicate of the active occurrence. The active execution SHALL remain the sole settlement owner.

#### Scenario: One-shot execution capacity is unavailable

- **GIVEN** a one-shot occurrence cannot start because execution capacity is full
- **WHEN** the manager handles the occurrence
- **THEN** the manager sends a negative acknowledgement
- **AND** Akka.Reminders owns the retry delay

#### Scenario: Reminder-series execution capacity is unavailable

- **GIVEN** a reminder-series occurrence cannot start because execution capacity is full
- **WHEN** the manager handles the occurrence
- **THEN** the manager acknowledges the occurrence without execution
- **AND** Netclaw does not retain the occurrence for catch-up work

#### Scenario: Exact active occurrence arrives again

- **GIVEN** an occurrence already has an active execution
- **WHEN** the same key, due time, and acknowledgement deadline arrive again
- **THEN** Netclaw does not start or settle the duplicate envelope
- **AND** the active execution remains the sole settlement owner

### Requirement: Settlement write order supports recovery

Netclaw SHALL save a failed run and its poison count before it sends a negative acknowledgement. Netclaw SHALL not advance Akka state after a local save failure.

Netclaw SHALL save a successful run and reset the poison count before it sends an acknowledgement. Reconciliation SHALL repair one-shot terminal state after a post-acknowledgement process failure.

#### Scenario: Local failure state cannot be saved

- **GIVEN** an execution attempt fails
- **WHEN** Netclaw cannot save its poison state
- **THEN** Netclaw does not send a negative acknowledgement
- **AND** the Akka.Reminders acknowledgement timeout remains the recovery path

#### Scenario: Process stops after successful acknowledgement

- **GIVEN** Netclaw acknowledges a successful one-shot
- **WHEN** the process stops before it saves the terminal outcome
- **THEN** reconciliation reads the durable delivered state
- **AND** reconciliation records the completed soft delete

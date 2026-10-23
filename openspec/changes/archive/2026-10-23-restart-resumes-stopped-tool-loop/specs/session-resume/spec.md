## MODIFIED Requirements

### Requirement: Graceful drain creates only a safe restart reminder

The session SHALL create a restart reminder only after an eligible model task or tool batch stops. It SHALL use the existing reminder definition and `current_session` delivery contract. The reminder SHALL carry exactly one recorded authority, in this order: the pending inputs, then the stopped turn, then the oldest unreported child run.

A turn that stopped after a tool batch is eligible when each tool call of the turn has a recorded result, no text of the stopped model call reached a subscriber, and no admitted input stays pending (#2632). The drain never cancels a running tool, so an eligible stopped turn has no tool of unknown effect. The final passivation snapshot SHALL carry the recorded authority of that turn, and the drain SHALL acknowledge only after the snapshot store confirms the write. When the write fails, the session SHALL post the notice and create no reminder for the turn.

#### Scenario: An interrupted model call creates a reminder

- **GIVEN** a model call has pending admitted input
- **AND** no tool batch or partial reply exists
- **WHEN** graceful drain cancels the call and confirms its task stopped
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder expires ten minutes after interruption

#### Scenario: A turn stopped after its tool batch creates a reminder

- **GIVEN** a person turn ran three tool batches, and each tool call has a recorded result
- **AND** the next model call streams no text
- **WHEN** graceful drain stops the model call
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder carries the recorded audience, boundary, and channel of the turn

#### Scenario: A completed turn stays quiet

- **GIVEN** a model call completes during drain
- **AND** no admitted input remains pending
- **WHEN** the daemon starts again
- **THEN** it registers no restart reminder for that session

#### Scenario: A possible effect blocks the reminder

- **GIVEN** a tool still runs at the coordinator deadline, or partial text of the stopped model call reached a subscriber
- **WHEN** graceful drain stops the session
- **THEN** the manifest contains no restart reminder for that turn
- **AND** the daemon reports the blocked session

#### Scenario: A failed final snapshot blocks the resume

- **GIVEN** graceful drain stopped an eligible turn after its tool batch
- **WHEN** the snapshot store rejects the final snapshot
- **THEN** the session posts the restart notice
- **AND** the restart manifest holds no reminder that resumes the turn

### Requirement: Graceful drain stops a tool loop

The session SHALL apply the 2-second model grace period to each live model call of a turn. This rule applies also after a tool batch closed the inputs of the turn. After a drain request, the session SHALL start no model call and no tool batch. It SHALL keep the reminder rule of the requirement above. When the drain stops a turn and no restart reminder resumes that turn, the session SHALL post one text notice in its channel before it stops. A reminder resumes the stopped turn when it replays an input of the turn, or when it carries the recorded authority of a turn that stopped after its tool batch. A reminder that only reports a child run, or only runs a waiting input, does not resume the stopped turn. The shutdown alert SHALL name each session whose drain timed out.

#### Scenario: The model call after a tool batch stops after the grace period

- **GIVEN** a tool batch completed
- **AND** the next model call of the turn streams
- **WHEN** graceful drain starts
- **THEN** the session cancels the model call after the grace period
- **AND** it acknowledges drain with a restart reminder that resumes the turn

#### Scenario: A tool batch boundary stops the turn

- **GIVEN** a tool batch runs
- **WHEN** graceful drain starts, and the batch then completes
- **THEN** the session starts no model call
- **AND** it acknowledges drain with a restart reminder that resumes the turn

#### Scenario: Tool calls inside the grace period do not run

- **GIVEN** graceful drain started during a model call
- **WHEN** the model returns tool calls before the grace period ends
- **THEN** the session starts no tool batch
- **AND** it stops the turn as a model stop for restart does

#### Scenario: A waiting input keeps its reminder

- **GIVEN** a tool loop runs
- **AND** an accepted input waits in the journal
- **WHEN** graceful drain stops the turn
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder runs the waiting input, and the stopped turn gets the notice

#### Scenario: A stopped turn without a reminder posts a notice

- **GIVEN** graceful drain stops a turn after a tool batch
- **AND** the turn has no recorded channel type
- **WHEN** the session passivates
- **THEN** the session posts one text notice in its channel before it stops
- **AND** the notice tells the person to send a message to continue
- **AND** the log names the reason that no reminder resumes the turn

#### Scenario: A resumable turn posts no notice

- **GIVEN** graceful drain stops a model call that has pending admitted input, or a turn that is eligible after its tool batch
- **AND** no partial reply exists
- **WHEN** the session passivates
- **THEN** the session posts no restart notice

#### Scenario: The shutdown alert names a timed-out session

- **GIVEN** a session does not acknowledge drain before the drain deadline
- **WHEN** the daemon emits the shutdown alert
- **THEN** the alert context `timedOutSessionIds` names that session
- **AND** the drain log line does not claim that the session recovers

### Requirement: A fresh restart reminder resumes stored work

The reminder manager SHALL deliver a fresh restart reminder through its existing `current_session` path. The session SHALL restore pending input under its recorded authority. With no pending input, the session SHALL resume the turn that the drain stopped after its tool batch. That turn SHALL continue with the next model call over the stored history, under the recorded authority of the turn. The restart reminder is only the signal: an automation turn resumes under its own recorded automation authority, and a person turn under the recorded person authority.

#### Scenario: A fresh reminder resumes the pending input

- **GIVEN** the restart manifest contains a reminder that has not expired
- **WHEN** the daemon starts
- **THEN** startup registers the reminder through `SaveReminderCommand`
- **AND** the session resumes the stored input without a user prompt

#### Scenario: A fresh reminder resumes a turn stopped after its tool batch

- **GIVEN** the drain stopped a turn after its tool batch and stored its authority
- **WHEN** the restart reminder arrives after the restart
- **THEN** the session replies `CommandAck` and starts the next model call of the turn
- **AND** the model request pairs each tool call with its result and holds the turn input at most once

#### Scenario: The original authority remains in force

- **GIVEN** a restart reminder wakes a session with pending input or with a stopped turn
- **WHEN** the session starts the model call
- **THEN** it restores the recorded requester, audience, and trust boundary
- **AND** reminder automation authority does not replace that context

#### Scenario: An expired reminder stays quiet

- **GIVEN** the reminder expiration is in the past
- **WHEN** startup reads the restart manifest
- **THEN** it does not register or deliver that reminder
- **AND** it logs one warning

#### Scenario: Stored authority has no channel type

- **GIVEN** an interrupted session has no stored channel type
- **WHEN** graceful drain classifies the session
- **THEN** the actor creates no restart reminder
- **AND** the actor does not contain a channel-specific route list

### Requirement: A restart-resume message is never an ordinary input

The session SHALL treat the restart reminder as a signal to resume stored work. The session SHALL NOT admit it as an input, and its text SHALL NOT reach the model as a user message.

- In `Ready` phase with pending input and no parked approval, the session SHALL resume that input.
- In `Ready` phase with no pending input, no parked approval, and a stored stopped turn, the session SHALL resume that turn and reply `CommandAck`. It SHALL record no input for the message. The resume and a recorded turn clear the stored turn, so a redelivery resumes nothing.
- In `Processing` or `Compacting` phase, the session SHALL queue each pending input that no turn selected and that the queue does not hold yet. It SHALL log `restart_resume_queued` and reply `RestartResumeNotNeeded`, also when it queued nothing. No turn of this message runs: each queued input runs later under its own recorded source. The normal drain SHALL run those inputs after the running turn. They run in admission order among themselves, after the inputs that the queue holds already. The child consent pause and the parent approval waits still apply.
- In `Ready` phase with nothing to resume, or in `Ready` phase when a parked approval owns the next turn, the session SHALL reply `RestartResumeNotNeeded` (a `CommandAck`), make no change, and log `restart_resume_noop`. This includes a redelivery after the resumed turn ended.
- `RestartResumeNotNeeded` SHALL tell the caller that this message resumes nothing and that no turn will run for it. A caller on the Reminder-origin route (#2491) SHALL then settle its run as success at once and SHALL NOT wait for a terminal turn.
- A reply that resumes a turn in `Ready` phase SHALL be an ordinary `CommandAck`, and the caller SHALL wait for the resumed turn. A reply that only queues work SHALL be `RestartResumeNotNeeded`, and the caller SHALL settle at once.
- A refusal (`CommandNack`) SHALL remain only for three causes: in `Ready` phase, a first pending input whose stored authority does not parse; in `Ready` phase, a stored stopped turn whose authority does not parse; and a restart reminder that arrives in `Processing`, `Compacting`, or `Passivating` phase while a restart drain is pending.
- A damaged pending input is an input whose stored authority does not parse. In each other case, the session SHALL log each damaged pending input, keep it pending, and queue the valid inputs (#2576). In `Ready` phase, a damaged input after the first input ends the leading inputs that the session resumes. The session SHALL NOT throw for a damaged input.
- In `Ready` phase, the session SHALL check for a parked approval before it checks the stored authority. In `Processing` or `Compacting` phase, the session does not check for a parked approval. It checks for a pending restart drain, and then it checks the stored authority.

#### Scenario: A redelivered restart message during the resumed turn

- **GIVEN** a restart reminder resumed the pending input and the turn runs
- **WHEN** the same restart reminder arrives again
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it stores no input

#### Scenario: A restart message with nothing pending

- **GIVEN** the resumed turn ended and the daemon restarted
- **WHEN** a redelivery of the restart reminder arrives
- **THEN** the session replies `RestartResumeNotNeeded`, not `CommandNack`
- **AND** it stores no input and makes no model call
- **AND** the resume text never reaches the model

#### Scenario: A restart message resumes a stopped turn

- **GIVEN** an idle session with no pending input and a stored turn that the drain stopped after its tool batch
- **WHEN** the restart reminder arrives
- **THEN** the session replies `CommandAck` and starts the next model call of that turn
- **AND** it stores no input, and the resume text never reaches the model

#### Scenario: A restart message during another turn queues the pending input

- **GIVEN** recovery started the turn of an orphan child result notice
- **AND** Alice's input P stays pending from before the restart
- **WHEN** the restart reminder arrives while the notice turn runs
- **THEN** the session queues P and replies `RestartResumeNotNeeded`
- **AND** a caller on the Reminder-origin route settles its run as success at once
- **AND** P runs in its own turn after the notice turn ends

#### Scenario: A restart message during another turn with nothing to queue

- **GIVEN** a turn runs, and every pending input is selected by that turn or is in the queue already
- **WHEN** the restart reminder arrives
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it queues no input a second time

#### Scenario: A restart message with a damaged pending input during another turn

- **GIVEN** a turn runs, and a pending input from before the restart has a stored authority that does not parse
- **WHEN** the restart reminder arrives
- **THEN** the session logs the damaged input and keeps it pending
- **AND** it queues the valid inputs and replies `RestartResumeNotNeeded`

#### Scenario: A damaged pending input after the first input in Ready phase

- **GIVEN** an idle session with pending inputs `[A, damaged C, B]`
- **WHEN** the restart reminder arrives
- **THEN** the session resumes A and replies `CommandAck`
- **AND** it logs C, keeps C pending, and queues B

#### Scenario: A damaged first pending input in Ready phase

- **GIVEN** an idle session whose first pending input has a stored authority that does not parse
- **WHEN** the restart reminder arrives
- **THEN** the session replies `CommandNack`
- **AND** it starts no turn

#### Scenario: A no-op acknowledgement on the Reminder-origin route ends the run

- **GIVEN** a Reminder-origin turn resumed after a restart and its turn ended
- **WHEN** the restart reminder arrives again and the session replies `RestartResumeNotNeeded`
- **THEN** the reminder run settles as success at once
- **AND** it does not wait for the 20-minute stall timeout

#### Scenario: An acknowledgement that resumes a turn does not end the run

- **GIVEN** a Reminder-origin restart message with a pending input or a stored stopped turn
- **WHEN** the session resumes the work and replies `CommandAck`
- **THEN** the run stays open until the resumed turn ends

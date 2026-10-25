## MODIFIED Requirements

### Requirement: A fresh restart reminder resumes stored work

The reminder manager SHALL deliver a fresh restart reminder through its existing `current_session` path. The session SHALL restore pending input under its recorded authority. The continuation input of a stopped turn is a pending input. The session SHALL resume it as one, and it SHALL add no user message to the model history for it. The turn SHALL continue with the next model call over the stored history, under the recorded authority of the turn. The continuation input SHALL NOT start at recovery, and it SHALL NOT join the turn of another requester. When a later input is admitted while no turn selected the continuation input, the session SHALL close the continuation input in the same journal write and post the restart notice once. The stopped turn never runs after that. Every admission path SHALL apply this close: an ordinary input, a reminder fire that replaces a waiting fire, an approval notice that replaces a waiting notice or is the first notice, and the result notice of a child run that recovery records as lost because the daemon restarted. Only a fresh restart reminder SHALL let a continuation input run. Recovery SHALL NOT queue a continuation input behind the child consent pause, also when the pause holds it. When the child consent pause holds the continuation input of an automation turn, the restart reminder SHALL NOT start that turn. The restart reminder SHALL queue the continuation input and SHALL journal that authority (`ContinuationAuthorized`), so a recovery after a passivation queues it again. It SHALL run once after the last open consent request closes, as any input that the pause holds. For a continuation input that the restart reminder queued, a person input or a fire SHALL close it, and the approval notice and a child result notice SHALL keep it. A continuation input that no restart reminder queued is not kept: the approval notice and a child result notice close it as any other admission does. The restart reminder is only the signal: an automation turn resumes under its own recorded automation authority, and a person turn under the recorded person authority.

#### Scenario: A fresh reminder resumes the pending input

- **GIVEN** the restart manifest contains a reminder that has not expired
- **WHEN** the daemon starts
- **THEN** startup registers the reminder through `SaveReminderCommand`
- **AND** the session resumes the stored input without a user prompt

#### Scenario: A fresh reminder resumes a turn stopped after its tool batch

- **GIVEN** the drain stopped a turn after its tool batch and journaled its continuation input
- **WHEN** the restart reminder arrives after the restart
- **THEN** the session replies `CommandAck` and starts the next model call of the turn
- **AND** the model request pairs each tool call with its result, holds the turn input once, and holds no empty user message

#### Scenario: A later input closes the continuation input

- **GIVEN** the drain journaled a continuation input for Alice's turn
- **WHEN** Bob writes after the restart and before the restart reminder arrives
- **THEN** the session closes the continuation input and posts the restart notice once
- **AND** Bob's input runs in its own turn under Bob's authority only
- **AND** the restart reminder that arrives later replies `RestartResumeNotNeeded`

#### Scenario: An approval notice closes the continuation input

- **GIVEN** the drain journaled a continuation input for Alice's turn, and a parked child request of the session is open
- **WHEN** the session admits an approval notice after the restart and before the restart reminder arrives
- **THEN** the journal closes the continuation input in the same write that admits the notice
- **AND** the session posts the restart notice once
- **AND** the restart reminder that arrives later replies `RestartResumeNotNeeded`

#### Scenario: A replacing reminder fire closes the continuation input

- **GIVEN** the drain journaled a continuation input for Alice's turn, and the pause holds a waiting fire of reminder M
- **WHEN** a new fire of M replaces the waiting fire after the restart and before the restart reminder arrives
- **THEN** the journal closes the waiting fire and the continuation input in the same write that admits the new fire
- **AND** the session posts the restart notice once
- **AND** the restart reminder that arrives later replies `RestartResumeNotNeeded`

#### Scenario: The notice of a lost child closes the continuation input

- **GIVEN** the drain stopped Alice's turn after its tool batch while a child run of the session was running, and journaled the continuation input
- **WHEN** recovery records the running child as lost with the reason `owner_restarted` and admits its result notice before the restart reminder arrives
- **THEN** the journal closes the continuation input in the same write that admits the notice
- **AND** the session posts the restart notice once and the stopped turn does not run

#### Scenario: The pause holds an automation continuation until the last answer

- **GIVEN** the drain journaled a continuation input for a reminder turn, and a parked child request of the session is open
- **WHEN** the restart reminder arrives after the restart
- **THEN** no turn runs for the continuation input
- **WHEN** the requester answers the last open request
- **THEN** the reminder turn resumes once under its recorded automation authority

#### Scenario: A held continuation with no restart reminder does not run

- **GIVEN** the drain journaled a continuation input for a reminder turn, and a parked child request of the session is open
- **AND** no restart reminder arrives after the restart
- **WHEN** the requester answers the last open request
- **THEN** the journal closes the continuation input in the same write that admits the approval notice
- **AND** the session posts the restart notice once, and the reminder turn does not run

#### Scenario: The original authority remains in force

- **GIVEN** a restart reminder wakes a session with pending input
- **WHEN** the session starts the model call
- **THEN** it restores the recorded requester, audience, and trust boundary
- **AND** reminder automation authority does not replace that context

#### Scenario: An expired reminder stays quiet

- **GIVEN** the reminder expiration is in the past
- **WHEN** startup reads the restart manifest
- **THEN** it does not register that reminder with the resume instruction, and no turn resumes the stored work
- **AND** it registers the expiry signal, which closes the stored work with one notice (see "An expired restart reminder closes the stopped turn with a notice")

#### Scenario: Stored authority has no channel type

- **GIVEN** an interrupted session has no stored channel type
- **WHEN** graceful drain classifies the session
- **THEN** the actor creates no restart reminder
- **AND** the actor does not contain a channel-specific route list

## ADDED Requirements

### Requirement: An expired restart reminder closes the stopped turn with a notice

Startup SHALL NOT drop a restart reminder that expired before startup recovery. It SHALL register that reminder as an expiry signal through the same `current_session` path, with the same identity, audience, boundary, and delivery, and with the expiry instruction in place of the resume instruction. Startup SHALL register a reminder with the resume instruction only when the reminder stays valid at least one minute after its fire time. Otherwise startup SHALL register the expiry signal. The reminder manager SHALL reject a reminder whose ID has the restart reminder prefix when its creator is not the system. The expiry signal SHALL resume nothing. The session SHALL close every input that was pending at recovery and that no turn selected, in one journal write, also the inputs of other requesters, and SHALL post the stopped-turn notice once after that write commits. The signal SHALL NOT close an approval notice, a child result notice, an input that arrived after recovery, or automated input that the child consent pause holds, except a continuation input. While a parent approval is open, the signal SHALL close nothing. The session SHALL reply `RestartResumeNotNeeded`, and it SHALL start no model call for the signal. The signal SHALL NOT become an input. A second delivery of the signal SHALL close nothing and post no notice. An expired parent continuation SHALL NOT end, cancel, or withdraw a parked child run or its consent requests.

#### Scenario: Startup registers an expired reminder as an expiry signal

- **GIVEN** the restart manifest contains one reminder that has not expired and one that expired
- **WHEN** the daemon starts
- **THEN** startup registers the fresh reminder with the resume instruction
- **AND** startup registers the expired reminder with the expiry instruction, a fire time after startup, and an expiry after that fire time
- **AND** startup deletes the manifest

#### Scenario: The expiry signal closes a continuation with one notice

- **GIVEN** the drain stopped Alice's turn after its tool batch and journaled its continuation input
- **WHEN** the expiry signal arrives after the restart
- **THEN** the session replies `RestartResumeNotNeeded`, closes the continuation input, and posts the restart notice once
- **AND** no model call starts, and no input holds the signal text
- **AND** a second delivery of the signal posts no notice

#### Scenario: The expiry signal closes an interrupted input with one notice

- **GIVEN** the drain stopped Alice's first model call and her input stays pending
- **WHEN** the expiry signal arrives after the restart
- **THEN** the session closes Alice's input, posts the restart notice once, and starts no model call

#### Scenario: The expiry signal closes the inputs of two requesters in one write

- **GIVEN** the drain stopped Alice's first model call, and Bob's input waits too
- **WHEN** the expiry signal arrives after the restart
- **THEN** the session closes both inputs in one write and posts the restart notice once

#### Scenario: A late expiry signal keeps new work

- **GIVEN** Bob writes after the restart, and his admission closes Alice's continuation with one notice
- **WHEN** Bob sends a follow-up, and then the expiry signal arrives
- **THEN** the session does not close the follow-up and posts no second notice

#### Scenario: The expiry signal closes a held continuation and keeps the child park

- **GIVEN** the drain journaled a continuation input for a reminder turn, and a parked child request of the session is open
- **WHEN** the expiry signal arrives after the restart
- **THEN** the session closes the continuation input and posts the restart notice once
- **AND** the consent prompt stays open, and the approval of the requester resumes the child once

### Requirement: The continuation input has an explicit marker

The session SHALL mark the continuation input of a stopped turn with an explicit durable marker when it journals that input. Each rule that skips a continuation input in the model history, or that closes a continuation input at a later admission, SHALL read that marker and SHALL NOT read the content of the input. An admitted person input with no text and no media SHALL be an ordinary input: the session SHALL add it to the model history as a user message, and no admission SHALL close it as a continuation input. The journal and the snapshot SHALL keep the marker. A record that a binary without this change wrote has no marker, so the reader SHALL treat a record with no marker as a continuation input exactly when it has no text, no media, no executable text, and no child notice.

#### Scenario: An empty person input reaches the model after a restart

- **GIVEN** Alice sends a message with no text and no media, and the drain stops her first model call
- **WHEN** the restart reminder resumes her input
- **THEN** the model request holds one user message for her input

#### Scenario: An empty person input joins the tool loop

- **GIVEN** Alice's turn runs a model call, and she sends a message with no text and no media
- **WHEN** the tool batch of the turn ends and her message joins the turn
- **THEN** the next model request ends with a user message for her message

#### Scenario: A later input keeps an empty person input

- **GIVEN** Alice sends a message with no text and no media while her turn runs, and the session buffers it
- **WHEN** Alice sends a second message
- **THEN** the session closes neither message and posts no restart notice

#### Scenario: A record with no marker keeps the old rule

- **GIVEN** a journal record of an admitted input that a 0.28.0-beta.3 binary wrote with no text, no media, no executable text, and no child notice
- **WHEN** the session recovers
- **THEN** the session treats the record as a continuation input
- **AND** a record of the same binary with text stays an ordinary input

## MODIFIED Requirements

### Requirement: A fresh restart reminder resumes stored work

The reminder manager SHALL deliver a fresh restart reminder through its existing `current_session` path. The session SHALL restore pending input under its recorded authority. The continuation input of a stopped turn is a pending input. The session SHALL resume it as one, and it SHALL add no user message to the model history for it. The turn SHALL continue with the next model call over the stored history, under the recorded authority of the turn. The continuation input SHALL NOT start at recovery, and it SHALL NOT join the turn of another requester. When a later input is admitted while no turn selected the continuation input, the session SHALL close the continuation input in the same journal write and post the restart notice once. The stopped turn never runs after that. Every admission path SHALL apply this close: an ordinary input, a reminder fire that replaces a waiting fire, an approval notice that replaces a waiting notice or is the first notice, and the result notice of a child run that recovery records as lost because the daemon restarted. When the child consent pause holds the continuation input of an automation turn, the restart reminder SHALL NOT start that turn. The continuation input SHALL wait, and it SHALL run once after the last open consent request closes, as any input that the pause holds. The restart reminder is only the signal: an automation turn resumes under its own recorded automation authority, and a person turn under the recorded person authority.

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

#### Scenario: The original authority remains in force

- **GIVEN** a restart reminder wakes a session with pending input
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

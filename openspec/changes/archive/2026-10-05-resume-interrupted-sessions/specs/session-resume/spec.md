## ADDED Requirements

### Requirement: Accepted input survives a graceful stop

The session SHALL store each accepted input before acknowledgment. The record SHALL retain its identity, order, content, media, source identity, and original authority.

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Input acknowledgment follows its journal record

- **GIVEN** a session receives user input
- **WHEN** the journal stores its admission record
- **THEN** the session acknowledges the input
- **AND** cold recovery restores the pending input and its authority

#### Scenario: Journal failure rejects input

- **GIVEN** the journal cannot store an admission record
- **WHEN** the session receives input
- **THEN** the session rejects that input
- **AND** it starts no model call for that input

#### Scenario: A lost acknowledgment does not duplicate input

- **GIVEN** the journal stores input with a stable source ID
- **WHEN** the source retries that input
- **THEN** the session acknowledges the stored input
- **AND** it does not add a second pending record

### Requirement: Graceful drain creates only a safe restart reminder

The session SHALL create a restart reminder only after an eligible model task stops. It SHALL use the existing reminder definition and `current_session` delivery contract.

#### Scenario: An interrupted model call creates a reminder

- **GIVEN** a model call has pending admitted input
- **AND** no tool batch or partial reply exists
- **WHEN** graceful drain cancels the call and confirms its task stopped
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder expires ten minutes after interruption

#### Scenario: A completed turn stays quiet

- **GIVEN** a model call completes during drain
- **AND** no admitted input remains pending
- **WHEN** the daemon starts again
- **THEN** it registers no restart reminder for that session

#### Scenario: A possible effect blocks the reminder

- **GIVEN** a tool batch started or partial text reached a subscriber
- **WHEN** graceful drain stops the session
- **THEN** the manifest contains no restart reminder for that turn
- **AND** the daemon reports the blocked session

### Requirement: A fresh restart reminder resumes stored work

The reminder manager SHALL deliver a fresh restart reminder through its existing `current_session` path. The session SHALL restore pending input under its recorded authority.

#### Scenario: A fresh reminder resumes the pending input

- **GIVEN** the restart manifest contains a reminder that has not expired
- **WHEN** the daemon starts
- **THEN** startup registers the reminder through `SaveReminderCommand`
- **AND** the session resumes the stored input without a user prompt

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

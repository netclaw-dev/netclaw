## ADDED Requirements

### Requirement: Authorized threaded turns adopt unsynced context

When a threaded adapter receives an authorized inbound message, it SHALL hydrate
the unsynced thread gap before that message and construct a single authorized
turn envelope containing:

- a canonical adopted-context projection for the adopted window; and
- the current authorized executable message.

The adopted-context portion SHALL be quoted context only. The current
authorized message SHALL be the only executable user instruction in that turn.

When adopted context is present, the threaded adapter MAY construct the
canonical adopted-context projection before handoff. The session SHALL durably
persist that exact projection together with the adopted-message metadata before
execution continues. Retries or recovery for the same authorized message id
SHALL reuse the persisted adopted-context record rather than re-derive a
different projection from raw thread history.

If the unsynced gap is empty, the adapter SHALL omit adopted-context
persistence and adopted-context framing and SHALL send only the current
authorized message as an ordinary authorized turn.

#### Scenario: Authorized message carries adopted window plus executable message

- **GIVEN** a thread has unsynced prior messages
- **AND** an authorized user sends the next inbound message
- **WHEN** the adapter constructs the session input
- **THEN** exactly one `SendUserMessage` is created
- **AND** it contains the adopted-context projection first
- **AND** it contains the current authorized message second
- **AND** only the current authorized message is executable

#### Scenario: Zero-gap authorized message omits adopted-context framing

- **GIVEN** the watermark already covers all prior thread messages before the
  current authorized inbound
- **WHEN** the adapter constructs the session input
- **THEN** no adopted-context projection is prepended
- **AND** the session receives only the current authorized message text

### Requirement: Unauthorized live threaded messages stay off the turn path

Threaded adapters SHALL NOT map unauthorized live inbound messages to
`SendUserMessage` commands. Those messages SHALL remain pending source-thread
context until a later authorized message adopts them.

#### Scenario: Unauthorized live message does not become a turn

- **GIVEN** a threaded Slack message from a non-allowed user
- **WHEN** no authorized user is speaking on that inbound event
- **THEN** no `SendUserMessage` command is created
- **AND** the message does not enter slash-command dispatch or model execution

### Requirement: Canonical framing and reserved-marker escaping

The channel pipeline SHALL use the following canonical framing for authorized
threaded turns:

```text
[adopted-context]
[adopted-message id={messageId} author={senderId} authority-at-inclusion={authorized|pending} ts={timestamp}]
{escaped adopted text}
[/adopted-message]
[/adopted-context]
[current-authorized-message author={senderId} ts={timestamp}]
{escaped current text}
[/current-authorized-message]
```

Any user-originated line beginning with a reserved marker prefix SHALL be
escaped by prefixing that line with `\` before inclusion in the canonical
projection.

The adapter owns source-thread gap fetch and watermark bookkeeping. After the
authorized turn is accepted for enqueue, it SHALL persist a pending cursor for
that authorized message. The adapter SHALL advance the durable
authorized-sync watermark only after `TurnCompleted` or other durable turn
completion confirms that the turn was durably recorded. This sequencing SHALL
remain fail-closed for crash recovery.

#### Scenario: Adopted message text with reserved marker is escaped

- **GIVEN** an adopted source message begins with `[adopted-context]`
- **WHEN** the projection is built
- **THEN** the emitted line begins with `\[adopted-context]`
- **AND** the model-visible framing remains unambiguous

#### Scenario: Current authorized message with reserved marker is escaped

- **GIVEN** the authorized sender's text begins with `[/adopted-message]`
- **WHEN** the projection is built
- **THEN** the line is escaped before inclusion under
  `[current-authorized-message ...]`

## MODIFIED Requirements

### Requirement: Source metadata on all commands

All inbound `SendUserMessage` commands SHALL carry source metadata sufficient
for ACL evaluation and audit logging. For threaded authorized turns that adopt
prior context, source metadata SHALL identify the current authorized sender as
the executable-turn source, while adopted prior messages are represented only in
the adopted-context audit record and canonical projection. That projection SHALL
continue to name adopted speakers by stable sender id even though they are not
treated as executable-turn sources.

#### Scenario: Authorized threaded turn source metadata points at authorizer

- **GIVEN** a thread where unauthorized messages were adopted
- **WHEN** the authorized turn is created
- **THEN** the command source metadata identifies the authorized current sender
- **AND** adopted prior senders are not treated as independent live turn sources

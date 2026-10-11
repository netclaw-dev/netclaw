## ADDED Requirements

### Requirement: SignalR text success confirms durable admission

The daemon SHALL return success for a text SendMessage request only after its journal stores the input admission record.
The daemon SHALL preserve the input's source identity and original authority.
The success response SHALL NOT wait for model completion.
Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Journal admission precedes the hub response

- **GIVEN** an authenticated connection is attached to its target session
- **AND** the journal holds the text admission write
- **WHEN** the connection sends text
- **THEN** the hub response remains incomplete until the journal stores that record
- **AND** success does not require a model response

#### Scenario: Buffered text retains the same contract

- **GIVEN** the session already processes or compacts an earlier turn
- **WHEN** an attached connection sends later text
- **THEN** success confirms that the journal stores the later input and its authority
- **AND** success does not require the earlier turn to finish

#### Scenario: An admission fault fails explicitly

- **GIVEN** session initialization, input enqueue, or the journal write fails
- **WHEN** a connection sends text
- **THEN** the daemon returns a failure or timeout
- **AND** the daemon does not report successful admission

#### Scenario: Attachment or ingress denial preserves authority

- **GIVEN** the connection lacks target attachment or ingress is closed
- **WHEN** the connection sends text
- **THEN** the daemon rejects the request before admission
- **AND** it starts no model call for that text

### Requirement: Client disconnect preserves admitted text work

The daemon SHALL retain admitted text after its SignalR client disconnects.
The disconnect SHALL NOT cancel the session's model task or discard the pending input.

#### Scenario: Disconnect precedes the model request

- **GIVEN** the daemon confirms text admission
- **AND** the model request has not started
- **WHEN** the client disconnects
- **THEN** the daemon retains that input
- **AND** the session can complete its turn or store an explicit terminal failure

#### Scenario: Disconnect occurs during the model response

- **GIVEN** the daemon confirms text admission and starts a model request
- **WHEN** the client disconnects before the model response
- **THEN** the session continues that turn independently of its subscriber
- **AND** the durable result does not require the client's connection

### Requirement: Text admission support is explicit

The daemon SHALL expose explicit support for durable text admission through its compatibility contract.
A client that depends on that support SHALL reject an unsupported daemon before it promises confirmed admission.

#### Scenario: A compatible daemon permits reliable admission

- **GIVEN** the daemon explicitly supports durable text admission
- **WHEN** the client attaches before text dispatch
- **THEN** the client can use the admission response contract

#### Scenario: An old daemon cannot imply support

- **GIVEN** the daemon lacks explicit durable text admission support
- **WHEN** the new client attaches before text dispatch
- **THEN** the client reports the unsupported daemon
- **AND** it does not silently treat the old early response as durable admission

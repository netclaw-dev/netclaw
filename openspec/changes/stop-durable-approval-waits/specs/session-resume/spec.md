## ADDED Requirements

### Requirement: Graceful stop of durable approval waits

During a graceful daemon stop, a session SHALL finish drain when every unfinished tool call waits on a durable approval. The session SHALL wait for the tool task to stop before it acknowledges drain. The journal SHALL retain approval requests and completed tool results for cold recovery.

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: One durable approval stops promptly

- **GIVEN** a session has one unfinished tool call with a journaled approval request
- **WHEN** the daemon requests a graceful drain
- **THEN** the session stops the tool task and acknowledges drain without an approval response
- **AND** the original requester can approve the call after cold recovery
- **AND** the recovered turn keeps its original authority

#### Scenario: Completed sibling retains its result

- **GIVEN** one tool result has a journal record and another tool call waits on a journaled approval
- **WHEN** the daemon requests a graceful drain
- **THEN** the session acknowledges drain after the tool task stops
- **AND** recovery does not execute the completed sibling again

#### Scenario: Active sibling prevents the fast path

- **GIVEN** one tool call waits on a journaled approval and another tool call has no journaled result or approval
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path
- **AND** the current bounded drain path remains in effect

#### Scenario: Accepted buffered input prevents the fast path

- **GIVEN** a session has accepted user input in its actor buffer
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Non-durable or resolved approval prevents the fast path

- **GIVEN** an unfinished call has a non-durable approval or a resolved approval without a result
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Deferred approval response prevents the fast path

- **GIVEN** the session has a deferred approval response
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

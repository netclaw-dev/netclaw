## ADDED Requirements

### Requirement: Sub-agent spawn carries an explicit audience

A `RunSubAgent` spawn message SHALL carry the spawning session's audience as a
parsed `TrustAudience`. The sub-agent actor SHALL NOT default a missing
audience to `TrustAudience.Personal`; a sub-agent spawned from a live session
always has a parent audience, so an absent audience is a programming error and
SHALL raise an explicit exception.

#### Scenario: Sub-agent inherits the parent session audience

- **GIVEN** a sub-agent spawned from a Public-audience session
- **WHEN** the sub-agent actor initializes its tool execution context
- **THEN** the context carries `TrustAudience.Public`
- **AND** the audience is not elevated to `Personal`

#### Scenario: Missing spawn audience fails loud

- **WHEN** a `RunSubAgent` message reaches the sub-agent actor without an
  audience
- **THEN** the actor throws an explicit exception
- **AND** no `Personal` audience is substituted

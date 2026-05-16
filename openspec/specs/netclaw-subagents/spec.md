## MODIFIED Requirements

### Requirement: Context layer subagent awareness

Subagent discovery and `spawn_agent` exposure SHALL honor the same effective
audience and feature gates as the rest of the session surface. Public sessions
and deployments with `SubAgents.Enabled = false` SHALL not be able to discover
or spawn subagents through prompt layers or tool calls.

#### Scenario: Public session receives no spawn_agent surface

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** the session prompt and tool definitions are built
- **THEN** subagent discovery is absent
- **AND** `spawn_agent` is absent or denied

#### Scenario: Runtime-disabled subagents unavailable to Team

- **GIVEN** `SubAgents.Enabled` is `false` in config
- **WHEN** a Team session starts
- **THEN** subagent discovery is absent
- **AND** `spawn_agent` is absent or denied

#### Scenario: Public cannot recover hidden subagents through discovery text

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** context layers are assembled
- **THEN** no discovery text names hidden subagents or instructs the model to
  delegate through `spawn_agent`

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

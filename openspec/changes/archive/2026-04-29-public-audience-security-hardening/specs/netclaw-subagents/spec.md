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

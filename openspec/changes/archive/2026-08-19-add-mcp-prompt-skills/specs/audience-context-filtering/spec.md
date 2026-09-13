## MODIFIED Requirements

### Requirement: Context layer audience filtering

The context layer system SHALL accept a `TrustAudience` parameter on `IContextLayerProvider.GetContextLayer()`. Each context layer implementation SHALL use the audience to determine what content to return. The `ContextAssemblyInput` record SHALL include a `TrustAudience Audience` field. When a feature is disabled deployment-wide, the corresponding context layer SHALL also return empty even for non-Public audiences. The skill context layer SHALL use separate Team and Personal index values when source permissions differ.

#### Scenario: Public audience receives no skill index

- **WHEN** a Public-audience session assembles context
- **THEN** `SkillIndexContextLayer.GetContextLayer(Public)` returns empty string
- **AND** no skill index appears in the session's system messages

#### Scenario: Public audience receives no memory index

- **WHEN** a Public-audience session assembles context
- **THEN** `MemoryIndexContextLayer.GetContextLayer(Public)` returns empty string
- **AND** no memory tool hints appear in the session's system messages

#### Scenario: Public audience receives no subagent discovery

- **WHEN** a Public-audience session assembles context
- **THEN** `SubAgentDiscoveryContextLayer.GetContextLayer(Public)` returns empty string
- **AND** no subagent index appears in the session's system messages

#### Scenario: Disabled skills feature suppresses skill index for Team

- **GIVEN** `SkillSync.Enabled` is `false` in config
- **WHEN** a Team-audience session assembles context
- **THEN** `SkillIndexContextLayer.GetContextLayer(Team)` returns empty string
- **AND** no skill index appears in the session's system messages

#### Scenario: Team audience receives allowed context layers

- **WHEN** a Team-audience session assembles context
- **THEN** all enabled context layers return their allowed content

#### Scenario: Personal audience receives allowed context layers

- **WHEN** a Personal-audience session assembles context
- **THEN** all enabled context layers return their allowed content

#### Scenario: MCP prompt server differs by audience

- **GIVEN** Personal can use MCP server `gigatron`
- **AND** Team cannot use MCP server `gigatron`
- **WHEN** both audiences request the skill context layer
- **THEN** the Personal index contains `mcp__gigatron__` prompt skills
- **AND** the Team index does not reveal those skill names

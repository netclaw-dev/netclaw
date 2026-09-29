## MODIFIED Requirements

### Requirement: Routed tool authorization remains audience-governed for MVP

On routed executions, the system SHALL apply the same tool authorization as any
other subagent run (`tool-authorization` TA-3 and TA-12) and the subagent tool
registration constraints.

Skill `allowed-tools` metadata SHALL NOT be an additional runtime tool gate.

#### Scenario: Routed execution honors existing audience policy

- **GIVEN** a routed activation with `metadata.subagent`
- **WHEN** the subagent executes tool calls
- **THEN** tool authorization uses existing audience/boundary and subagent tool policy

#### Scenario: Skill allowed-tools is not an additional runtime gate

- **GIVEN** a routed activation where skill frontmatter includes `allowed-tools`
- **WHEN** runtime tool authorization is evaluated
- **THEN** authorization behavior remains unchanged by this change
- **AND** no additional skill-level tool intersection gate is applied

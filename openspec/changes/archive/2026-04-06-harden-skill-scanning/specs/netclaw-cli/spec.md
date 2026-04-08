## ADDED Requirements

### Requirement: Skill scan degradation is surfaced operationally
When skill scanning rejects one or more discovered skills during daemon startup
or rebuild, Netclaw SHALL surface that degraded state through operator-visible
diagnostics rather than silently continuing with a partial inventory.

#### Scenario: Startup rebuild logs degraded skill inventory
- **GIVEN** daemon startup scans the skills directory and rejects one or more skills
- **WHEN** the startup rebuild completes
- **THEN** the daemon logs that skill inventory is degraded
- **AND** the log includes the number of accepted skills and rejected issues

#### Scenario: Sync rebuild logs rejected system skill
- **GIVEN** system skill sync completes and a subsequent scan rejects a discovered skill
- **WHEN** the registry rebuild finishes
- **THEN** the daemon logs the rejected skill path and reason
- **AND** the registry is rebuilt from accepted skills only

# netclaw-session Delta Spec

## MODIFIED Requirements

### Requirement: Skill index context layer injection

The skill index context layer SHALL accept the session's effective trust
audience and available tool set when producing the skill index for system
prompt injection. The injected index SHALL be filtered per-audience rather
than identical for all sessions.

#### Scenario: Session prompt includes audience-filtered skill index

- **GIVEN** a session with `TrustAudience.Team` and tools `[web_search, web_fetch, file_read]`
- **WHEN** the system prompt is assembled
- **THEN** the skill index context layer injects the Team-audience compressed
  menu
- **AND** skills requiring `shell_execute` are not present in the injected index

#### Scenario: Session prompt uses pre-built menu

- **GIVEN** pre-built menus exist for each audience
- **WHEN** the system prompt is assembled for a new session
- **THEN** the context layer selects the pre-built menu matching the session's
  effective audience
- **AND** no per-turn menu generation occurs

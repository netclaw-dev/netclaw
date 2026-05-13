## ADDED Requirements

### Requirement: Spawned subagents use inherited project instructions

The system SHALL resolve project identity files from the inherited parent
`project_dir` using the same precedence as the parent session and SHALL include
the resulting project instructions in the subagent system prompt when the
inherited `project_dir` is non-null.

#### Scenario: Subagent prompt includes inherited project instructions

- **GIVEN** a parent session has project directory set to
  `/home/user/workspaces/netclaw`
- **AND** `/home/user/workspaces/netclaw/AGENTS.md` exists
- **WHEN** the parent spawns a subagent
- **THEN** the subagent system prompt includes the content of that identity file

#### Scenario: No inherited project directory means no project instructions

- **GIVEN** a parent session has no project directory set
- **WHEN** the parent spawns a subagent
- **THEN** the subagent system prompt contains no project-specific identity file
  content

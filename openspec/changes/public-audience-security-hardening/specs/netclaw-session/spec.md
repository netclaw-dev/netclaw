## MODIFIED Requirements

### Requirement: Layered system prompt assembly

The system SHALL assemble session context from ordered layers: `SOUL.md`,
`AGENTS.md`, `TOOLING.md`, dynamic context layers (tool index, skill index,
memory index), and session-specific context. Later layers SHALL augment earlier
layers. Identity files SHALL be loaded at session start and cached for the
session lifetime. Missing files SHALL be omitted without error.

**AGENTS.md** SHALL be loaded from an embedded assembly resource, not from
the filesystem. The system SHALL select the audience-specific variant based
on the session's `TrustAudience`:
- `Personal` or `Team`: load the full AGENTS resource
- `Public`: load the stripped public AGENTS resource

**TOOLING.md** SHALL be loaded from the filesystem for `Personal` and `Team`
audiences. For `Public` audiences, TOOLING.md SHALL be suppressed entirely.

**Project instructions** (`.netclaw/AGENTS.md`, `CLAUDE.md`, etc.) SHALL be
loaded for `Personal` and `Team` audiences. For `Public` audiences, project
instructions SHALL be suppressed.

`ISystemPromptProvider.GetSystemPrompt()` SHALL accept a `TrustAudience`
parameter in addition to the optional project directory.

Runtime placeholder substitution SHALL be performed on the embedded AGENTS
resource using `NetclawPaths` values (e.g., `{{SYSTEM_SKILLS_DIR}}`,
`{{IDENTITY_DIR}}`).

#### Scenario: Full layer assembly on session start

- **GIVEN** identity files exist at `~/.netclaw/identity/SOUL.md`,
  and `~/.netclaw/identity/TOOLING.md`
- **WHEN** a new Personal-audience session starts
- **THEN** the system prompt includes SOUL.md from disk, AGENTS.md from
  embedded resource (full variant), and TOOLING.md from disk
- **AND** dynamic context layers and session-specific context are appended

#### Scenario: Public session receives stripped AGENTS and no TOOLING

- **GIVEN** identity files exist on disk
- **WHEN** a new Public-audience session starts
- **THEN** the system prompt includes SOUL.md from disk and the public
  AGENTS variant from embedded resource
- **AND** TOOLING.md is NOT included
- **AND** project instructions are NOT included

#### Scenario: Missing identity file does not prevent session start

- **GIVEN** SOUL.md does not exist on disk
- **WHEN** a new session starts
- **THEN** the system assembles the prompt from available layers
- **AND** the missing layer is omitted without error

#### Scenario: Embedded AGENTS resource has placeholders substituted

- **WHEN** a session loads the embedded AGENTS resource
- **THEN** placeholders like `{{SYSTEM_SKILLS_DIR}}` are replaced with
  actual `NetclawPaths` values

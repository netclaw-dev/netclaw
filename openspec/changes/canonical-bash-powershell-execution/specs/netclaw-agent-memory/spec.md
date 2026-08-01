## MODIFIED Requirements

### Requirement: Layered system prompt assembly

The system SHALL assemble session context from ordered layers: `SOUL.md`, the audience-appropriate embedded operating core followed by the deployment `AGENTS.md`, `TOOLING.md`, dynamic context layers (tool index, skill index, memory index), and session-specific context. The embedded operating core SHALL be labeled as higher-priority platform guidance, while runtime ACL and tool policy SHALL remain the authoritative security boundaries. For shell-capable audiences, the embedded core SHALL instruct the agent to consult the execution environment in `[working-context]`, use its preferred grammar and path style, and never assume or mix shell grammars. The same deployment `AGENTS.md` SHALL apply to Personal, Team, and Public audiences. Identity files SHALL be read before each inbound turn so edits take effect on the next turn. Missing files SHALL be omitted without error; unexpected read failures SHALL be surfaced.

#### Scenario: Full layer assembly on an inbound turn

- **GIVEN** identity files exist at `~/.netclaw/identity/SOUL.md`, `~/.netclaw/identity/AGENTS.md`, and `~/.netclaw/identity/TOOLING.md`
- **WHEN** an inbound turn begins
- **THEN** the system prompt includes the audience-appropriate embedded operating core before the deployment `AGENTS.md`
- **AND** includes the remaining permitted identity and dynamic context layers in canonical order

#### Scenario: Shell guidance uses runtime context

- **GIVEN** the embedded operating core is assembled for a shell-capable audience
- **WHEN** the agent receives an execution environment in `[working-context]`
- **THEN** the core instructs it to use the declared grammar and path style
- **AND** does not hard-code Bash or PowerShell as universal syntax

#### Scenario: Deployment playbook applies to Public audience

- **GIVEN** a deployment `AGENTS.md` exists
- **WHEN** a Public-audience turn begins
- **THEN** the prompt contains the stripped embedded Public operating core
- **AND** contains the same deployment playbook used for Personal and Team audiences
- **AND** continues to suppress Public-ineligible tooling and project layers

#### Scenario: Identity edit takes effect on next turn

- **GIVEN** a session is active
- **WHEN** the deployment `AGENTS.md` is updated during a turn
- **THEN** the current model call is unchanged
- **AND** the next inbound turn rebuilds its prompt with the updated playbook

#### Scenario: Missing identity file does not prevent a turn

- **GIVEN** one or more optional identity files do not exist on disk
- **WHEN** an inbound turn begins
- **THEN** the system assembles the prompt from available layers
- **AND** the missing layer is omitted without error

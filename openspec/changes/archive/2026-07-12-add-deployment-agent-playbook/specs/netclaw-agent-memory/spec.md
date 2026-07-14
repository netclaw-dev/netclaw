## MODIFIED Requirements

### Requirement: Layered system prompt assembly

The system SHALL assemble session context from ordered layers: `SOUL.md`, the audience-appropriate embedded operating core followed by the deployment `AGENTS.md`, `TOOLING.md`, dynamic context layers (tool index, skill index, memory index), and session-specific context. The embedded operating core SHALL be labeled as higher-priority platform guidance, while runtime ACL and tool policy SHALL remain the authoritative security boundaries. The same deployment `AGENTS.md` SHALL apply to Personal, Team, and Public audiences. Identity files SHALL be read before each inbound turn so edits take effect on the next turn. Missing files SHALL be omitted without error; unexpected read failures SHALL be surfaced.

#### Scenario: Full layer assembly on an inbound turn

- **GIVEN** identity files exist at `~/.netclaw/identity/SOUL.md`, `~/.netclaw/identity/AGENTS.md`, and `~/.netclaw/identity/TOOLING.md`
- **WHEN** an inbound turn begins
- **THEN** the system prompt includes the audience-appropriate embedded operating core before the deployment `AGENTS.md`
- **AND** includes the remaining permitted identity and dynamic context layers in canonical order

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

### Requirement: Personality bootstrap via onboarding wizard

The system SHALL bootstrap agent personality and its deployment playbook through `netclaw init`. The wizard SHALL collect owner identity, write initial `SOUL.md` and `TOOLING.md`, and seed a minimal `AGENTS.md` playbook scaffold only when that file is absent. The post-init conversation SHALL refine personality and mission guidance using identity-file tools and the always-present embedded identity routing rules.

#### Scenario: Fresh init seeds identity files

- **GIVEN** a fresh install with no identity files
- **WHEN** the operator completes `netclaw init`
- **THEN** the wizard writes initial `SOUL.md` and `TOOLING.md`
- **AND** writes a minimal deployment mission scaffold to `AGENTS.md`

#### Scenario: Init preserves an existing playbook

- **GIVEN** `~/.netclaw/identity/AGENTS.md` already exists
- **WHEN** init or identity redo writes identity-owned files
- **THEN** the existing playbook remains byte-for-byte unchanged

### Requirement: Self-configuration through conversation

The system SHALL allow the agent to modify identity files (`SOUL.md`, `AGENTS.md`, `TOOLING.md`) and skill files (`~/.netclaw/skills/*.md`) through conversation using `file_read` and `file_write`. Always-present embedded guidance SHALL route personality and operator context to `SOUL.md`, deployment mission/workflows/skill-selection/review rules to `AGENTS.md`, and environment capabilities to `TOOLING.md`. The agent SHALL propose and obtain confirmation before changing mission guidance. The agent SHALL NOT place secrets, volatile entity data, ACL, or security policy in the deployment playbook and SHALL NOT have tools that directly modify `netclaw.json`, `secrets.json`, ACL, or security policy.

#### Scenario: Agent updates deployment mission

- **GIVEN** the operator asks to improve a recurring deployment workflow
- **WHEN** the agent has clarified the intended process and the operator confirms its proposal
- **THEN** the agent reads and updates `AGENTS.md` using identity-file tools
- **AND** reports that the change applies on the next inbound turn

#### Scenario: Agent routes operator context separately

- **GIVEN** the operator shares personal communication preferences while defining the mission
- **WHEN** the agent persists the confirmed onboarding results
- **THEN** it writes operator and personality context to `SOUL.md`
- **AND** writes mission and workflow guidance to `AGENTS.md`

#### Scenario: Agent attempts to modify ACL

- **GIVEN** the user asks the agent to update ACL rules through conversation
- **WHEN** the agent evaluates the request
- **THEN** the agent refuses the modification
- **AND** explains that ACL changes require CLI or direct operator configuration

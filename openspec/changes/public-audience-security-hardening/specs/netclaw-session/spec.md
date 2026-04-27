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
- `Public`: load the stripped Public AGENTS resource

**TOOLING.md** SHALL be loaded from the filesystem for `Personal` and `Team`
audiences. For `Public` audiences, TOOLING.md SHALL be suppressed entirely.

**Project instructions** (`.netclaw/AGENTS.md`, `CLAUDE.md`, etc.) SHALL be
loaded for `Personal` and `Team` audiences. For `Public` audiences, project
instructions SHALL be suppressed.

`ISystemPromptProvider.GetSystemPrompt()` SHALL accept a `TrustAudience`
parameter in addition to the optional project directory.

Runtime placeholder substitution SHALL be performed on the embedded AGENTS
resource using `NetclawPaths` values.

For channel-created sessions, the effective audience SHALL be resolved before
the first prompt assembly. Slack- and Discord-origin sessions SHALL therefore
select the correct AGENTS variant and startup context/tool index on the first
turn, not only after later session updates.

The assembled prompt story SHALL be internally consistent with runtime feature
gates. Prompt layers, discovery hints, and tool exposure SHALL agree on what a
session can actually access. Public sessions SHALL not be instructed to use
hidden search, skills, memory, subagent, or workspace/identity capabilities.
Public attachment guidance in AGENTS SHALL also use the same redacted/pathless
framing as the Public session block and SHALL not mention filesystem locations
that are hidden from that audience.

#### Scenario: Full layer assembly on session start

- **GIVEN** identity files exist on disk
- **WHEN** a new Personal-audience session starts
- **THEN** the system prompt includes SOUL.md from disk, AGENTS.md from
  embedded resource (full variant), and TOOLING.md from disk
- **AND** dynamic context layers and session-specific context are appended

#### Scenario: Public session receives stripped AGENTS and no TOOLING

- **GIVEN** identity files exist on disk
- **WHEN** a new Public-audience session starts
- **THEN** the system prompt includes SOUL.md from disk and the Public
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
- **THEN** placeholders are replaced with actual `NetclawPaths` values

#### Scenario: Public prompt does not advertise hidden capabilities

- **GIVEN** a new Public-audience session starts
- **AND** search, skills, memory, and subagents are not exposed to Public
- **WHEN** the system prompt is assembled
- **THEN** the prompt does not advertise those hidden capabilities through
  AGENTS, TOOLING, project instructions, startup tool/context indices, or
  context layers

#### Scenario: Slack session starts with the resolved audience-specific prompt

- **GIVEN** a new Slack-origin session resolves to audience `Public`
- **WHEN** the first system prompt is assembled for that session
- **THEN** the Public AGENTS variant is selected immediately
- **AND** the startup context/tool index omits capabilities hidden from Public

#### Scenario: Discord session starts with the resolved audience-specific prompt

- **GIVEN** a new Discord-origin session resolves to audience `Public`
- **WHEN** the first system prompt is assembled for that session
- **THEN** the Public AGENTS variant is selected immediately
- **AND** the startup context/tool index omits capabilities hidden from Public

#### Scenario: Public attachment guidance stays consistent with redacted session block

- **GIVEN** a new Public-audience session starts with uploaded attachments
- **WHEN** the system prompt is assembled
- **THEN** Public AGENTS guidance describes attachments without mentioning
  `session_dir`, `media_dir`, `inbox/`, or other filesystem paths
- **AND** the guidance is consistent with the ID-only Public session block

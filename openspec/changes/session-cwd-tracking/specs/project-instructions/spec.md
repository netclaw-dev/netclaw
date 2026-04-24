## ADDED Requirements

### Requirement: Project identity file loading from project directory

The system SHALL load project-scoped identity files from the session's
project directory. At the project root, the system SHALL check for the
following files in order: `.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`,
`CONTEXT.md`. The first match wins. When no project directory is set or
no identity file is found, the block is empty.

#### Scenario: Identity file found at project root

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** `/home/user/workspaces/akadonic/CLAUDE.md` exists
- **WHEN** project instruction loading runs
- **THEN** the content of `CLAUDE.md` is returned framed as
  `Instructions from: /home/user/workspaces/akadonic/CLAUDE.md`

#### Scenario: Netclaw-specific file takes precedence

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** both `.netclaw/AGENTS.md` and `CLAUDE.md` exist in that directory
- **WHEN** project instruction loading runs
- **THEN** `.netclaw/AGENTS.md` is used (first in check order)

#### Scenario: No identity files found

- **GIVEN** a session has project directory set to `/tmp/empty-dir`
- **AND** no identity files exist in that directory
- **WHEN** project instruction loading runs
- **THEN** no project instructions are returned
- **AND** no error is raised

#### Scenario: Identity file with I/O error skipped gracefully

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** `CLAUDE.md` exists but is unreadable (permission denied)
- **WHEN** project instruction loading runs
- **THEN** the file is skipped and the next candidate is checked

#### Scenario: No project directory produces empty result

- **GIVEN** a session with no project directory set
- **WHEN** project instruction loading runs
- **THEN** no project instructions are returned

### Requirement: Project instructions context layer

The system SHALL inject discovered project instructions as a
`[project-instructions]` block in the session's dynamic context using
`EveryTurn` timing. The content SHALL be re-read from disk on every LLM
call so edits take effect on the next turn. The block SHALL be empty when
the session has no project directory set or when no identity file is found.

#### Scenario: Project instructions injected on every turn

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** `CLAUDE.md` contains project-specific guidance
- **WHEN** an LLM call is assembled
- **THEN** a `[project-instructions]` block containing the file content is
  included in the dynamic context

#### Scenario: Project switch picks up new instructions

- **GIVEN** a session working on project-a with `CLAUDE.md`
- **WHEN** the agent switches to project-b via `set_working_directory`
- **THEN** the next LLM call includes project instructions from project-b
- **AND** project-a instructions are no longer included

#### Scenario: Project instructions survive compaction

- **GIVEN** a session with active project instructions
- **WHEN** context compaction occurs
- **THEN** the `[project-instructions]` block is re-read from disk on the
  next LLM call
- **AND** content is current (not from the compacted history)

#### Scenario: No project directory produces empty block

- **GIVEN** a session with no project directory set
- **WHEN** an LLM call is assembled
- **THEN** the `[project-instructions]` block is not included

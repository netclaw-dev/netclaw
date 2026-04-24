## ADDED Requirements

### Requirement: Project identity file loading from project directory

The system SHALL load project-scoped identity files from the session's
project directory. At the project root, the system SHALL check for the
following files in order: `.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`,
`CONTEXT.md`. The first match wins. When no project directory is set or
no identity file is found, no project content is included in the system
prompt.

#### Scenario: Identity file found at project root

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** `/home/user/workspaces/akadonic/CLAUDE.md` exists
- **WHEN** the system prompt is assembled
- **THEN** the content of `CLAUDE.md` is included in the system prompt

#### Scenario: Netclaw-specific file takes precedence

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** both `.netclaw/AGENTS.md` and `CLAUDE.md` exist in that directory
- **WHEN** the system prompt is assembled
- **THEN** `.netclaw/AGENTS.md` is used (first in check order)

#### Scenario: No identity files found

- **GIVEN** a session has project directory set to `/tmp/empty-dir`
- **AND** no identity files exist in that directory
- **WHEN** the system prompt is assembled
- **THEN** no project content is included
- **AND** no error is raised

#### Scenario: Identity file with I/O error skipped gracefully

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** `CLAUDE.md` exists but is unreadable (permission denied)
- **WHEN** the system prompt is assembled
- **THEN** the file is skipped and the next candidate is checked

#### Scenario: No project directory produces no project content

- **GIVEN** a session with no project directory set
- **WHEN** the system prompt is assembled
- **THEN** no project content is included

### Requirement: Project instructions in system prompt

The system SHALL include discovered project identity file content in the
system prompt at position [0], alongside the global SOUL/AGENTS/TOOLING
layers. This is assembled via `SystemPromptAssembler.Assemble()`. The
system prompt SHALL be re-assembled when the project directory changes
(via `set_working_directory`) by calling `SetSystemPrompt()` again.

The project content sits in the prompt-cache prefix and is stable across
consecutive turns within the same project.

#### Scenario: Project instructions included in system prompt

- **GIVEN** a session has project directory set to
  `/home/user/workspaces/akadonic`
- **AND** `CLAUDE.md` contains project-specific guidance
- **WHEN** an LLM call is assembled
- **THEN** the system prompt at position [0] includes the project content
  alongside global SOUL/AGENTS/TOOLING

#### Scenario: Project switch re-assembles system prompt

- **GIVEN** a session working on project-a with `CLAUDE.md`
- **WHEN** the agent switches to project-b via `set_working_directory`
- **THEN** `SetSystemPrompt()` is called again
- **AND** the system prompt now includes project-b's identity file
- **AND** project-a content is no longer in the system prompt

#### Scenario: Project instructions survive compaction

- **GIVEN** a session with active project instructions in the system prompt
- **WHEN** context compaction occurs
- **THEN** `SetSystemPrompt()` re-reads from disk on the next recovery
- **AND** content is current (not from the compacted history)

#### Scenario: No project directory — system prompt unchanged

- **GIVEN** a session with no project directory set
- **WHEN** the system prompt is assembled
- **THEN** the system prompt contains only global SOUL/AGENTS/TOOLING
- **AND** no project-specific content is included

## ADDED Requirements

### Requirement: Managed server-feed sub-agent discovery

The system SHALL load sub-agent definitions from user-authored top-level files under `~/.netclaw/agents/*.md` and from managed server-feed files under `~/.netclaw/agents/.server-feeds/<feed-name>/*.md`. User-authored top-level sub-agents SHALL take precedence over managed server-feed sub-agents with the same logical name. Shadowed managed sub-agents SHALL NOT be exposed through sub-agent discovery, `spawn_agent`, or routed skill execution.

#### Scenario: Managed sub-agent is loaded when no local conflict exists

- **GIVEN** `~/.netclaw/agents/.server-feeds/team/code-reviewer.md` declares `name: code-reviewer`
- **AND** no top-level local sub-agent declares `name: code-reviewer`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** `code-reviewer` is registered as an available sub-agent according to its frontmatter visibility

#### Scenario: Local sub-agent shadows managed sub-agent

- **GIVEN** `~/.netclaw/agents/code-reviewer.md` declares `name: code-reviewer`
- **AND** `~/.netclaw/agents/.server-feeds/team/code-reviewer.md` also declares `name: code-reviewer`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** the top-level local `code-reviewer` definition is registered
- **AND** the managed feed `code-reviewer` definition is skipped
- **AND** NetClaw emits a diagnostic identifying the shadowed managed definition

#### Scenario: Shadowed managed sub-agent cannot be spawned by routed skill

- **GIVEN** a top-level local sub-agent shadows a managed server-feed sub-agent with the same name
- **WHEN** a skill routes execution through `metadata.subagent` using that name
- **THEN** routed execution resolves to the registered local sub-agent definition
- **AND** the shadowed managed definition is not used

#### Scenario: Managed feed conflicts are deterministic

- **GIVEN** two configured server feeds both provide a managed sub-agent named `reviewer`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** NetClaw registers only one `reviewer` definition using deterministic configured feed order
- **AND** skips later managed duplicates with diagnostics

#### Scenario: Managed file changes refresh the registry

- **GIVEN** a managed sub-agent file changes under `~/.netclaw/agents/.server-feeds/team/`
- **WHEN** the sub-agent loader checks for changes
- **THEN** the loader detects the managed file change
- **AND** refreshes the sub-agent registry snapshot

#### Scenario: Missing managed namespace does not block local loading

- **GIVEN** `~/.netclaw/agents/.server-feeds/` does not exist
- **AND** top-level local sub-agent files exist under `~/.netclaw/agents/`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** local sub-agent loading continues without requiring the managed namespace to exist

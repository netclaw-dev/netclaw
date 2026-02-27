# netclaw-agent-memory Specification

Research: `docs/research/agent-patterns.md`,
`docs/research/dynamic-context-discovery.md` (§5 — deferred memory retrieval
decisions: keyword vs. vector search, embedding strategy, injection budgets)

## Purpose

Define agent personality (soul files), local memory (project registry,
environment inventory), capability self-discovery, self-configuration through
conversation, pre-compaction memory flush, and the standard configuration
directory structure. This capability makes Netclaw a persistent, context-aware
agent rather than a stateless chat endpoint.

## Requirements

### Requirement: Layered system prompt assembly

The system SHALL assemble session context from ordered layers: PERSONALITY.md,
INSTRUCTIONS.md, USER.md, project AGENTS.md overlay, and session-specific
context. Later layers SHALL augment earlier layers. All soul files SHALL be
loaded at session start and cached for the session lifetime.

#### Scenario: Full layer assembly on session start

- **GIVEN** soul files exist at `~/.netclaw/soul/PERSONALITY.md`,
  `~/.netclaw/soul/INSTRUCTIONS.md`, and `~/.netclaw/soul/USER.md`
- **WHEN** a new session starts
- **THEN** the system prompt includes content from all three soul files in
  layer order (personality, instructions, user)
- **AND** session-specific context is appended after the soul layers

#### Scenario: Project overlay loaded on demand

- **GIVEN** a registered project has an `agents_md` path in the project registry
- **WHEN** the session involves that project
- **THEN** the project AGENTS.md content is included as a context overlay
  between the user layer and session context layer

#### Scenario: Missing soul file does not prevent session start

- **GIVEN** one or more soul files do not exist on disk
- **WHEN** a new session starts
- **THEN** the system assembles the prompt from available layers
- **AND** the missing layer is omitted without error

### Requirement: Conversational personality bootstrap

The system SHALL run a personality bootstrap conversation on first interaction
when soul files do not exist. The bootstrap SHALL learn owner preferences,
scan the environment, and write initial soul files to disk.

#### Scenario: First-run bootstrap triggered

- **GIVEN** no soul files exist at `~/.netclaw/soul/`
- **WHEN** the first user message arrives
- **THEN** the agent initiates a personality bootstrap conversation
- **AND** the agent introduces itself and explains the setup process

#### Scenario: Bootstrap writes soul files

- **GIVEN** the personality bootstrap conversation completes
- **WHEN** the agent has gathered owner name, preferences, and communication
  style
- **THEN** the agent writes PERSONALITY.md, INSTRUCTIONS.md, and USER.md to
  `~/.netclaw/soul/`
- **AND** the agent confirms readiness to the user

#### Scenario: Bootstrap re-triggered via CLI

- **GIVEN** soul files already exist
- **WHEN** the operator runs `netclaw personality reset`
- **THEN** the existing soul files are backed up
- **AND** the next session triggers a fresh bootstrap conversation

### Requirement: Project registry persistence

The system SHALL persist registered projects as JSON on disk at
`~/.netclaw/projects/registry.json`. Projects SHALL be registered and
unregistered through conversation or CLI.

#### Scenario: Register project through conversation

- **GIVEN** the user asks the agent to register a project
- **WHEN** the agent receives a valid project path and name
- **THEN** the project is added to `registry.json`
- **AND** the agent confirms the registration with project details

#### Scenario: Project registry survives restart

- **GIVEN** projects are registered in `registry.json`
- **WHEN** the process restarts
- **THEN** the project registry is loaded from disk
- **AND** all previously registered projects are available in agent context

#### Scenario: Invalid project path rejected

- **GIVEN** the user asks to register a project
- **WHEN** the provided path does not exist on disk
- **THEN** the registration is rejected with an explanation

### Requirement: Environment capability self-discovery

The system SHALL scan for installed tools and capabilities at startup and
on-demand. Discovered capabilities SHALL be persisted to
`~/.netclaw/environment/inventory.json` and summarized in the system prompt.

#### Scenario: Automatic scan at startup

- **WHEN** the Netclaw process starts
- **THEN** the system scans for installed CLIs (`git`, `gh`, `claude`,
  `opencode`, `dotnet`, `node`)
- **AND** checks git credential availability for remote hosts
- **AND** checks MCP server reachability
- **AND** persists results to `inventory.json`

#### Scenario: On-demand rescan through conversation

- **GIVEN** the agent is in an active session
- **WHEN** the user asks the agent to rescan its environment
- **THEN** the agent re-runs the environment scan
- **AND** updates `inventory.json` with current results
- **AND** reports changes since the last scan

#### Scenario: Unavailable tool recorded accurately

- **GIVEN** a tool (e.g., `claude`) is not installed
- **WHEN** the environment scan runs
- **THEN** the tool is recorded as `available: false` in the inventory
- **AND** the agent does not attempt to use that tool in subsequent sessions

### Requirement: Self-configuration through conversation

The system SHALL allow the agent to modify its own configuration files through
conversation within safety bounds. The agent MUST NOT modify ACL, security
policy, tool grant policies, exposure mode, network configuration, or provider
credentials through conversation.

#### Scenario: Agent updates personality file

- **GIVEN** the user asks the agent to adjust its personality
- **WHEN** the agent proposes and the user confirms the change
- **THEN** the agent validates the change
- **AND** writes the updated file atomically (write to temp, rename)
- **AND** reports that the change was saved
- **AND** advises that a session reboot is needed for context refresh

#### Scenario: Agent attempts to modify ACL

- **GIVEN** the user asks the agent to update ACL rules through conversation
- **WHEN** the agent evaluates the request
- **THEN** the agent refuses the modification
- **AND** explains that ACL changes require CLI or direct file edit by the
  operator

#### Scenario: Agent registers project through self-configuration

- **GIVEN** the user describes a project to register
- **WHEN** the agent has sufficient details (name, path)
- **THEN** the agent adds the project to `registry.json`
- **AND** validates the project path exists before writing

#### Scenario: Invalid config change rejected

- **GIVEN** the agent proposes a configuration change
- **WHEN** schema validation fails on the proposed change
- **THEN** the change is not written to disk
- **AND** the agent reports the validation failure to the user

### Requirement: Pre-compaction memory flush

The system SHALL trigger a silent agentic turn before context compaction to
save durable memories. The flush SHALL complete before compaction proceeds.

#### Scenario: Flush triggered before compaction

- **GIVEN** session context approaches the compaction threshold
- **WHEN** the system detects compaction is imminent
- **THEN** the agent is prompted to save important context
- **AND** the agent writes durable memories (Memorizer, local files)
- **AND** compaction proceeds only after flush completes

#### Scenario: Flush saves to external memory

- **GIVEN** MCP Memorizer is configured and reachable
- **WHEN** the pre-compaction flush runs
- **THEN** the agent writes important findings to Memorizer
- **AND** writes current task state summary to memory

#### Scenario: Flush completes even when Memorizer unavailable

- **GIVEN** MCP Memorizer is unreachable
- **WHEN** the pre-compaction flush runs
- **THEN** the agent saves available context to local files
- **AND** compaction proceeds without blocking indefinitely

### Requirement: Standard configuration directory

The system SHALL use `~/.netclaw/` as the standard configuration directory
with the following subdirectory structure: `soul/`, `projects/`, `environment/`,
`schedules/`, and `config/`. The directory SHALL be created if it does not
exist at startup.

#### Scenario: Directory created on first startup

- **GIVEN** the `~/.netclaw/` directory does not exist
- **WHEN** the Netclaw process starts
- **THEN** the system creates `~/.netclaw/` and all required subdirectories
  (`soul/`, `projects/`, `environment/`, `schedules/`, `config/`)

#### Scenario: Existing directory preserved

- **GIVEN** `~/.netclaw/` already exists with files
- **WHEN** the Netclaw process starts
- **THEN** existing files are not overwritten or removed
- **AND** any missing subdirectories are created

#### Scenario: Configurable base path

- **GIVEN** an alternative data directory is specified in configuration
- **WHEN** the Netclaw process starts
- **THEN** the system uses the configured path instead of `~/.netclaw/`
- **AND** the same subdirectory structure is maintained

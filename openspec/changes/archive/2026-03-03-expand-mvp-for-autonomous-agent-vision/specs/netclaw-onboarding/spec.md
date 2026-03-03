## ADDED Requirements

### Requirement: Phase 2 conversational personality bootstrap

The system SHALL trigger a conversational personality bootstrap on the first
conversation if personality files (PERSONALITY.md, INSTRUCTIONS.md, USER.md)
do not exist. The bootstrap conversation SHALL ask the operator about
communication preferences, tone, name preferences, and working style, then
write the resulting soul files to the standard config directory.

#### Scenario: First conversation triggers bootstrap

- **GIVEN** no personality files exist in the config directory
- **WHEN** the operator starts their first conversation with Netclaw
- **THEN** the agent initiates a personality bootstrap conversation
- **AND** asks about communication preferences and working style

#### Scenario: Bootstrap writes soul files

- **GIVEN** the personality bootstrap conversation is complete
- **WHEN** the operator has answered all preference questions
- **THEN** the system writes PERSONALITY.md, INSTRUCTIONS.md, and USER.md to
  the config directory

#### Scenario: Bootstrap skipped when files exist

- **GIVEN** personality files already exist in the config directory
- **WHEN** a new conversation starts
- **THEN** no personality bootstrap is triggered
- **AND** the existing personality files are loaded normally

### Requirement: Environment discovery during onboarding

The system SHALL scan for installed tools and host capabilities as part of
Phase 2 onboarding. Discovery results SHALL be persisted to the environment
inventory file for use in session context and capability self-awareness.

#### Scenario: Tool discovery during onboarding

- **WHEN** Phase 2 onboarding runs environment discovery
- **THEN** the system scans for installed tools (git, gh, claude, opencode,
  dotnet, node)
- **AND** checks git credential status
- **AND** writes results to the environment inventory file

#### Scenario: MCP server reachability check during onboarding

- **GIVEN** MCP servers are configured
- **WHEN** Phase 2 onboarding runs environment discovery
- **THEN** the system checks reachability of each configured MCP server
- **AND** records reachability status in the environment inventory

### Requirement: Project registration during onboarding

The system SHALL ask the operator about repositories to register as part of
Phase 2 onboarding. Registered projects are added to the project registry
with their paths, capabilities, and AGENTS.md locations.

#### Scenario: Register projects during onboarding

- **WHEN** Phase 2 onboarding reaches the project registration step
- **THEN** the system asks the operator about repositories to register
- **AND** scans provided paths for AGENTS.md files

#### Scenario: Skip project registration

- **WHEN** Phase 2 onboarding reaches the project registration step
- **AND** the operator indicates no projects to register
- **THEN** onboarding proceeds with an empty project registry

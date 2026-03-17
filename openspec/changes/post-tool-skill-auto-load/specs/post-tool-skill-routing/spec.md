## ADDED Requirements

### Requirement: Tools can declare post-tool skill dependencies

The system SHALL allow tool registrations to declare one or more required
system skills that MUST be injected before the follow-up model call that turns
tool results into a user-facing answer.

#### Scenario: Search tool declares citation skill dependency
- **WHEN** the first-party `web_search` tool is registered
- **THEN** its metadata includes `search-citation` as a post-tool required skill

#### Scenario: Web fetch declares citation skill dependency
- **WHEN** the first-party `web_fetch` tool is registered
- **THEN** its metadata includes `search-citation` as a post-tool required skill

### Requirement: Post-tool skill routing is deterministic

The system SHALL resolve post-tool skill loads from explicit tool metadata, not
from semantic analysis of tool result content.

#### Scenario: Executed tool batch maps to required skills
- **GIVEN** a tool batch contains `web_search` and `web_fetch`
- **WHEN** post-tool routing runs
- **THEN** the required skill set contains `search-citation`
- **AND** duplicate skill names are de-duplicated before loading

#### Scenario: Tool without mapping adds no skill requirement
- **GIVEN** a tool completes with no declared post-tool skill dependency
- **WHEN** post-tool routing runs
- **THEN** that tool contributes no required skill to the follow-up call

### Requirement: Missing skill mappings degrade safely

If a tool declares a required skill that is unavailable in the current skill
registry or unreadable on disk, the system SHALL warn and continue the turn
without failing tool execution.

#### Scenario: Required skill missing from registry
- **GIVEN** an executed tool declares a post-tool skill name that is not present
  in the skill registry
- **WHEN** post-tool routing resolves required skills
- **THEN** a warning is logged with the tool name and missing skill name
- **AND** the turn proceeds without that skill overlay

#### Scenario: Required skill file unreadable
- **GIVEN** a required skill is present in the registry but its `SKILL.md` file
  cannot be read
- **WHEN** the session attempts to load it for a follow-up call
- **THEN** a warning is logged
- **AND** the turn proceeds without that skill overlay

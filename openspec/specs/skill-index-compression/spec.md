# skill-index-compression Specification

## Purpose

Define the compressed skill index format for the skill discovery system.
The index is injected into the LLM system prompt and points directly at
skill files on disk for retrieval-led reasoning (no tool invocation needed).
## Requirements
### Requirement: Compressed pipe-delimited index format

The system SHALL generate a compressed skill index using a pipe-delimited format grouped by category. The index SHALL advertise logical skill access through `skill_load` and `skill_read_resource` and SHALL NOT include physical native, system, server-feed, or external skill roots.

#### Scenario: Index generated from registered skills

- **GIVEN** skills are registered across native, system, server-feed, and external sources
- **WHEN** the index is generated
- **THEN** the output uses a pipe-delimited format with category groupings
- **AND** each skill is identified by logical name and description
- **AND** no physical skill root or `SKILL.md` path is included

#### Scenario: Index includes logical retrieval directive

- **WHEN** the compressed index is generated
- **THEN** the index instructs the model to access or activate skills through `skill_load`
- **AND** the index instructs the model to read listed resources through `skill_read_resource`
- **AND** the index does not instruct the model to use `file_read` for normal skill loading

#### Scenario: Skills grouped by category

- **GIVEN** skills in `.system` and root-level categories
- **WHEN** the index is generated
- **THEN** skills are grouped by their `Category` property
- **AND** root-level skills appear under the `user` category

### Requirement: All authorized model-invocable skills visible in index

The system SHALL include every authorized model-invocable skill in the index regardless of source. It SHALL exclude skills with `DisableModelInvocation` and MCP prompt skills whose server is not allowed for the audience.

#### Scenario: All authorized logical skills visible without physical origins

- **GIVEN** accepted skills from system, native, server-feed, external, and MCP prompt sources
- **WHEN** the index is generated for an authorized audience
- **THEN** every model-invocable skill appears by logical name
- **AND** source paths are not required to use the skill

#### Scenario: MCP prompt signature appears

- **GIVEN** an allowed MCP prompt has one required and one optional argument
- **WHEN** the index is generated
- **THEN** the prompt skill appears under its canonical logical name
- **AND** its compact argument hint distinguishes required and optional values

#### Scenario: Skill without allowed-tools is visible

- **GIVEN** an authorized skill has no `allowed-tools` metadata
- **WHEN** the index is generated
- **THEN** the skill appears in the index

### Requirement: DisableModelInvocation index exclusion

Skills with `disable-model-invocation: true` in frontmatter SHALL be excluded
from the compressed index. They remain invokable via slash commands but the
LLM does not see them in the skill list.

#### Scenario: Disable-model-invocation skill excluded from index

- **GIVEN** a skill has `disable-model-invocation: true`
- **WHEN** the compressed index is generated
- **THEN** the skill does not appear in the index
- **AND** the skill remains available via slash-command dispatch


## MODIFIED Requirements

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

### Requirement: All skills visible in index

The system SHALL include all registered skills in the index regardless of physical origin. The only exclusion is skills with `disable-model-invocation: true`.

#### Scenario: All logical skills visible without origins

- **GIVEN** accepted skills from system, native, server-feed, and external sources
- **WHEN** the index is generated
- **THEN** every model-invocable skill appears by logical name
- **AND** source names and physical paths are not required to use the skill
#### Scenario: Skill without allowed-tools is always visible

- **GIVEN** a skill has no `allowed-tools` declared in frontmatter
- **WHEN** the index is generated
- **THEN** the skill appears in the index

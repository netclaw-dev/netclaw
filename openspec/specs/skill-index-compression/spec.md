# skill-index-compression Specification

## Purpose

Define the compressed skill index format for the skill discovery system.
The index is injected into the LLM system prompt and points directly at
skill files on disk for retrieval-led reasoning (no tool invocation needed).

## Requirements

### Requirement: Compressed pipe-delimited index format

The system SHALL generate a compressed skill index using pipe-delimited format
grouped by category. The index SHALL include the skills root path so the agent
can construct file paths for direct reads.

#### Scenario: Index generated from registered skills

- **GIVEN** skills are registered across categories `.system` and root
- **WHEN** the index is generated
- **THEN** the output uses pipe-delimited format with category groupings
- **AND** each category line lists skill file paths (e.g., `name/SKILL.md`)
- **AND** the header includes the skills root directory path

#### Scenario: Index includes retrieval-led reasoning directive

- **WHEN** the compressed index is generated
- **THEN** the index includes a directive to prefer retrieval-led reasoning
  over pre-training-led reasoning

#### Scenario: Skills grouped by category

- **GIVEN** skills in `.system/` category and root-level skills
- **WHEN** the index is generated
- **THEN** skills are grouped by their `Category` property
- **AND** root-level skills appear under the `user` category
- **AND** each category line uses brace-delimited file lists

### Requirement: All skills visible in index

The system SHALL include all registered skills in the index regardless of
origin. The only exclusion is skills with `disable-model-invocation: true`.

#### Scenario: All skills visible

- **GIVEN** skills from `.system/` and root-level directories
- **WHEN** the index is generated
- **THEN** all skills appear in the index

#### Scenario: Skill without allowed-tools is always visible

- **GIVEN** a skill has no `allowed-tools` declared in frontmatter
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

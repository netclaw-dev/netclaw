# skill-execution-routing Delta Spec — skill-subagent-overlays

## ADDED Requirements

### Requirement: Declarative subagent routing metadata

The system SHALL support an optional `metadata.subagent: <name>` field in skill
frontmatter as an AgentSkills-compatible metadata extension for execution
routing. Skills MAY include this field when declarative subagent routing is
required.

When present, the value SHALL be interpreted as a subagent registry target
identifier and SHALL be validated as a non-empty string name.

#### Scenario: Skill declares routing target

- **GIVEN** a skill contains `metadata.subagent: operations-helper`
- **WHEN** the skill registry scans frontmatter
- **THEN** the registry stores the routing target on the skill entry metadata

#### Scenario: Skill omits routing target

- **GIVEN** a skill without `metadata.subagent`
- **WHEN** the skill registry scans frontmatter
- **THEN** no routed target is recorded
- **AND** activation remains eligible for inline execution path

### Requirement: Deterministic activation path selection

For skill activations that support slash command dispatch, path selection SHALL
be deterministic and SHALL evaluate `metadata.subagent` before inline
skill-body injection.

If a valid routed target exists, the system SHALL execute the routed subagent
path and SHALL NOT execute inline path for the same activation.

#### Scenario: Routed path selected over inline path

- **GIVEN** a matched slash command skill with valid `metadata.subagent`
- **WHEN** activation dispatch resolves execution path
- **THEN** the routed subagent path is selected
- **AND** inline skill-body injection is not used

#### Scenario: Inline path selected when routing metadata absent

- **GIVEN** a matched slash command skill with no `metadata.subagent`
- **WHEN** activation dispatch resolves execution path
- **THEN** inline skill-body injection path is selected

### Requirement: Skill body overlay semantics for routed execution

The system SHALL pass routed skill bodies as additive subagent
system-prompt overlays when activation uses `metadata.subagent`. The system
SHALL NOT treat routed skill bodies as user runtime context on that path.

#### Scenario: Routed execution uses additive system overlay

- **GIVEN** a skill with `metadata.subagent` and a non-empty body
- **WHEN** routed subagent execution starts
- **THEN** the skill body is appended as additive system specialization context
- **AND** existing subagent base instructions remain in effect

#### Scenario: Routed execution does not emit skill body as user context

- **GIVEN** a skill with `metadata.subagent`
- **WHEN** routed subagent execution starts
- **THEN** the skill body is not appended to user runtime message content

### Requirement: Deterministic routed failure semantics

Routed execution SHALL fail deterministically for invalid routing conditions,
including unknown subagent target, internal-only target, and malformed routed
metadata.

In all routed failure cases, the system SHALL NOT silently fall back to inline
skill execution.

Routed failure output SHALL be user-visible and SHALL include actionable
remediation guidance (for example: add the missing subagent definition, or
fix/remove `metadata.subagent` on the skill).

#### Scenario: Unknown target fails without fallback

- **GIVEN** `metadata.subagent` references a target not present in registry
- **WHEN** activation dispatch attempts routed execution
- **THEN** activation returns deterministic unknown-subagent failure
- **AND** the error includes the missing target name and remediation guidance
- **AND** inline skill execution is not attempted

#### Scenario: Internal-only target fails without fallback

- **GIVEN** `metadata.subagent` references a target marked internal-only
- **WHEN** activation dispatch attempts routed execution
- **THEN** activation returns deterministic internal-target failure
- **AND** the error includes target name, visibility reason, and remediation guidance
- **AND** inline skill execution is not attempted

#### Scenario: Malformed routing metadata fails without fallback

- **GIVEN** a skill has malformed `metadata.subagent` value
- **WHEN** activation dispatch attempts routed execution
- **THEN** activation returns deterministic metadata-validation failure
- **AND** the error includes metadata field details and remediation guidance
- **AND** inline skill execution is not attempted

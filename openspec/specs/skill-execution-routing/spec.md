# skill-execution-routing Specification

## Purpose

Define deterministic skill activation routing between inline skill-body
execution and declarative subagent execution using `metadata.subagent`.

## Requirements

### Requirement: Declarative subagent routing metadata

The system SHALL support an optional `metadata.subagent: <name>` field in skill
frontmatter as an AgentSkills-compatible metadata extension for execution
routing. Skills MAY include this field when declarative subagent routing is
required.

When present, the value SHALL be interpreted as a subagent registry target
identifier and SHALL be validated as a non-empty string name.

Validation of `metadata.subagent` SHALL be enforced at dispatch time on each
activation request. Scan-time validation MAY emit warnings, but dispatch-time
validation is authoritative.

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

For first-party skill activation entry points, path selection SHALL be
deterministic and SHALL evaluate `metadata.subagent` before inline
skill-body injection.

First-party activation entry points include slash-command dispatch, scheduled
slash payload dispatch, and any tool-driven activation path implemented by the
runtime.

If a valid routed target exists, the system SHALL execute the routed subagent
path and SHALL NOT execute inline path for the same activation.

#### Scenario: Routed path selected over inline path

- **GIVEN** a matched slash command skill with valid `metadata.subagent`
- **WHEN** activation dispatch resolves execution path
- **THEN** the routed subagent path is selected
- **AND** inline skill-body injection is not used

#### Scenario: Tool-driven activation follows same routing rules

- **GIVEN** a skill activation request arrives through a tool-driven activation path
- **AND** the target skill has valid `metadata.subagent`
- **WHEN** activation dispatch resolves execution path
- **THEN** the routed subagent path is selected
- **AND** inline skill-body injection is not used

#### Scenario: Inline path selected when routing metadata absent

- **GIVEN** a matched slash command skill with no `metadata.subagent`
- **WHEN** activation dispatch resolves execution path
- **THEN** inline skill-body injection path is selected

### Requirement: Routed tool authorization remains audience-governed for MVP

On routed executions, the system SHALL apply the same tool authorization as any
other subagent run (`tool-authorization` TA-3 and TA-12) and the subagent tool
registration constraints.

Skill `allowed-tools` metadata SHALL NOT be an additional runtime tool gate.

#### Scenario: Routed execution honors existing audience policy

- **GIVEN** a routed activation with `metadata.subagent`
- **WHEN** the subagent executes tool calls
- **THEN** tool authorization uses existing audience/boundary and subagent tool policy

#### Scenario: Skill allowed-tools is not an additional runtime gate

- **GIVEN** a routed activation where skill frontmatter includes `allowed-tools`
- **WHEN** runtime tool authorization is evaluated
- **THEN** authorization behavior remains unchanged by this change
- **AND** no additional skill-level tool intersection gate is applied

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

### Requirement: Routed subagent execution uses live registry and parent context

When a skill activation resolves through `metadata.subagent`, the runtime SHALL
use the same reloadable subagent registry and the same immutable parent-context
snapshot contract as explicit `spawn_agent` execution.

#### Scenario: Routed activation picks up edited subagent definition

- **GIVEN** a skill routes through `metadata.subagent: operations-helper`
- **AND** `operations-helper.md` is edited to a new valid state on disk
- **WHEN** the next routed activation occurs
- **THEN** the runtime reloads the subagent registry before routing
- **AND** the routed activation uses the updated definition

#### Scenario: Routed activation fails closed after invalid edit

- **GIVEN** a skill routes through `metadata.subagent: operations-helper`
- **AND** `operations-helper.md` is edited into an invalid state on disk
- **WHEN** the next routed activation occurs
- **THEN** routing fails deterministically against the reloaded registry
- **AND** inline fallback is not attempted
- **AND** the stale prior definition is not used

### Requirement: Routed activations use background acceptance and later result delivery

Every first-party routed activation SHALL use the `background-subagent-runs` start contract.
This includes `skill_load`, slash commands, scheduled slash payloads, and reminder skill activation.
The activation SHALL validate metadata, reload and validate the target, and scan the skill before acceptance.
It SHALL return an accepted run identifier without waiting for final child output.
The later result SHALL preserve skill, profile, original input, and original authority attribution.
The skill body SHALL remain an additive child system overlay on the routed path.
Inline activation and deterministic failure without inline fallback SHALL remain unchanged.

#### Scenario: Tool-driven routed skill returns acceptance

- **GIVEN** an authorized routed skill and a non-empty task
- **WHEN** `skill_load` activates that skill
- **THEN** it returns the accepted run identifier before child completion
- **AND** the later child result arrives through the owner's attributed continuation

#### Scenario: Slash activation frees the parent phase

- **GIVEN** a valid slash skill with `metadata.subagent`
- **WHEN** its child acceptance commits
- **THEN** the parent emits an acceptance reply and completes the slash input
- **AND** a later user input does not wait for that child merely because the skill routed

#### Scenario: Scheduled activation preserves its original correlation

- **GIVEN** an admitted scheduled slash payload
- **WHEN** activation retries after its accepted response disappears
- **THEN** it uses the original admitted occurrence and activation key
- **AND** it creates no second child

#### Scenario: Invalid route cannot become inline work

- **GIVEN** an unknown target, invalid metadata, or rejected skill content
- **WHEN** routed activation runs its pre-admission checks
- **THEN** it returns the existing explicit failure with useful remediation
- **AND** neither a child nor inline execution starts

#### Scenario: A loaded routed skill retains its overlay

- **GIVEN** a scanned routed skill body
- **WHEN** its background child starts
- **THEN** the body remains an additive system overlay
- **AND** acceptance does not inject that body into the parent's inline path

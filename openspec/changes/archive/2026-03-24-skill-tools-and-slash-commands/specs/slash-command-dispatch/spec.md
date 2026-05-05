# slash-command-dispatch Specification

## Purpose

Define session-level slash-command dispatch that allows users to
deterministically invoke skills via `/name` syntax, adopting the Claude Code
invocation model.

## Requirements

### Requirement: Slash-command registry from skill names

The system SHALL maintain a slash-command registry mapping `/name` to
`SkillEntry` for all skills where `UserInvocable != false`. The registry
SHALL be rebuilt when the skill registry is re-populated.

#### Scenario: Skill registered as slash command

- **GIVEN** a skill with `name: netclaw-operations` and `invocable` not set (default true)
- **WHEN** the slash-command registry is built
- **THEN** `/netclaw-operations` maps to that skill entry

#### Scenario: Non-user-invocable skill excluded

- **GIVEN** a skill with `invocable: false`
- **WHEN** the slash-command registry is built
- **THEN** the skill does not appear in the slash-command registry

### Requirement: Session-level message interception

The system SHALL intercept user messages starting with `/` in the session
actor before LLM dispatch. If matched, the skill content SHALL be injected
as a transient system message and the remainder passed as user content.

#### Scenario: Recognized slash command loads skill

- **GIVEN** `/netclaw-operations` is registered
- **WHEN** the user sends `/netclaw-operations check daemon health`
- **THEN** the `netclaw-operations` SKILL.md body is injected as a transient
  system message
- **AND** `check daemon health` is passed as the user message to the LLM

#### Scenario: Slash command with no arguments

- **WHEN** the user sends `/netclaw-operations`
- **THEN** the skill content is injected
- **AND** an empty or minimal user message is passed to the LLM

#### Scenario: Unrecognized slash command returns error

- **WHEN** the user sends `/nonexistent do something`
- **THEN** the system returns a deterministic error message
- **AND** the error lists available slash commands
- **AND** the message is NOT passed to the LLM for interpretation

### Requirement: Frontmatter invocation control fields

The system SHALL parse `disable-model-invocation`, `invocable`, and
`argument-hint` from YAML frontmatter.

#### Scenario: disable-model-invocation parsed

- **GIVEN** a skill with `disable-model-invocation: true` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.DisableModelInvocation` is `true`
- **AND** the skill is excluded from the compressed index
- **AND** the skill remains in the slash-command registry

#### Scenario: user-invocable false parsed

- **GIVEN** a skill with `invocable: false` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.UserInvocable` is `false`
- **AND** the skill is excluded from the slash-command registry
- **AND** the skill appears in the compressed index (LLM can auto-load)

#### Scenario: argument-hint parsed

- **GIVEN** a skill with `argument-hint: "[subsystem]"` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.ArgumentHint` is `"[subsystem]"`

#### Scenario: Defaults when fields absent

- **GIVEN** a skill with no invocation control fields in frontmatter
- **WHEN** the skill is scanned
- **THEN** `DisableModelInvocation` is `false`
- **AND** `UserInvocable` is `true`
- **AND** `ArgumentHint` is `null`

### Requirement: Slash commands work with scheduled jobs

The system SHALL support slash-command syntax in scheduled job and reminder
payloads. When a scheduled job fires with a message starting with `/`, the
same dispatch logic SHALL apply.

#### Scenario: Scheduled job with slash command

- **GIVEN** a reminder with payload `/netclaw-operations check health`
- **WHEN** the reminder fires and sends the message to the session
- **THEN** the slash-command dispatch intercepts it
- **AND** the operations skill is loaded before the LLM processes the message

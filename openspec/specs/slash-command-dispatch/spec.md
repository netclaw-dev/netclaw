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

- **GIVEN** a skill with `name: netclaw-operations` and `user-invocable` not set (default true)
- **WHEN** the slash-command registry is built
- **THEN** `/netclaw-operations` maps to that skill entry

#### Scenario: Non-user-invocable skill excluded

- **GIVEN** a skill with `user-invocable: false`
- **WHEN** the slash-command registry is built
- **THEN** the skill does not appear in the slash-command registry

### Requirement: Session-level message interception

The system SHALL intercept user messages starting with `/` in the session
actor before LLM dispatch. For matched commands, execution path SHALL be
deterministic:

Slash-command dispatch SHALL use the same shared skill-activation routing
resolver used by other first-party activation entry points.

- If the matched skill has valid `metadata.subagent`, the system SHALL route to
  the named user-facing subagent execution path.
- If the matched skill has no `metadata.subagent`, the system SHALL inject skill
  content as transient system context and pass the remainder as user content.

On the routed subagent path, the skill body SHALL NOT be injected into the main
session prompt stack for that turn.

#### Scenario: Recognized slash command with routed subagent

- **GIVEN** `/netclaw-operations` is registered
- **AND** the skill has `metadata.subagent: operations-helper`
- **AND** `operations-helper` is a known user-facing subagent
- **WHEN** the user sends `/netclaw-operations check daemon health`
- **THEN** dispatch routes to `operations-helper`
- **AND** skill body is not injected into main-session transient system context
- **AND** `check daemon health` is passed to routed subagent execution

#### Scenario: Recognized slash command with inline path

- **GIVEN** `/netclaw-operations` is registered
- **AND** the skill has no `metadata.subagent`
- **WHEN** the user sends `/netclaw-operations check daemon health`
- **THEN** the `netclaw-operations` SKILL.md body is injected as a transient
  system message
- **AND** `check daemon health` is passed as the user message to the LLM

#### Scenario: Slash command with no arguments on routed path

- **GIVEN** `/netclaw-operations` is registered with `metadata.subagent`
- **WHEN** the user sends `/netclaw-operations`
- **THEN** the command routes to the configured subagent
- **AND** an empty or minimal user payload is passed to subagent execution

#### Scenario: Unrecognized slash command returns error

- **WHEN** the user sends `/nonexistent do something`
- **THEN** the system returns a deterministic error message
- **AND** the error lists available slash commands
- **AND** the message is NOT passed to the LLM for interpretation

### Requirement: Slash-command interception is limited to the current authorized message

Slash-command dispatch SHALL inspect only the current authorized executable
message of a threaded adopted turn. Adopted-context material and unauthorized
pending messages SHALL NOT be parsed or intercepted as slash commands.

#### Scenario: Pending unauthorized slash-like text is not dispatched

- **GIVEN** an adopted message contains `/netclaw-operations check health`
- **AND** that adopted message came from an unauthorized speaker
- **WHEN** an authorized user later adopts the thread and sends a normal message
- **THEN** slash-command dispatch does not trigger from the adopted message text

#### Scenario: Current authorized slash command still dispatches

- **GIVEN** the current authorized executable message is `/netclaw-operations check health`
- **WHEN** the turn is processed
- **THEN** slash-command interception runs on that current authorized message

### Requirement: Frontmatter invocation control fields

The system SHALL parse `disable-model-invocation`, `user-invocable`,
`argument-hint`, and `metadata.subagent` from YAML frontmatter.

`metadata.subagent` SHALL be treated as an optional string field naming a
subagent target. Non-string, empty, or malformed values SHALL be rejected as
deterministic configuration errors.

#### Scenario: disable-model-invocation parsed

- **GIVEN** a skill with `disable-model-invocation: true` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.DisableModelInvocation` is `true`
- **AND** the skill is excluded from the compressed index
- **AND** the skill remains in the slash-command registry

#### Scenario: user-invocable false parsed

- **GIVEN** a skill with `user-invocable: false` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.UserInvocable` is `false`
- **AND** the skill is excluded from the slash-command registry
- **AND** the skill appears in the compressed index (LLM can auto-load)

#### Scenario: argument-hint parsed

- **GIVEN** a skill with `argument-hint: "[subsystem]"` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.ArgumentHint` is `"[subsystem]"`

#### Scenario: metadata.subagent parsed

- **GIVEN** a skill with `metadata.subagent: operations-helper` in frontmatter
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.Metadata.Subagent` is `operations-helper`

#### Scenario: Invalid metadata.subagent rejected

- **GIVEN** a skill with malformed `metadata.subagent` frontmatter value
- **WHEN** the skill is scanned or slash dispatch resolves the skill
- **THEN** the system returns a deterministic validation error
- **AND** the skill activation does not continue

#### Scenario: Defaults when fields absent

- **GIVEN** a skill with no invocation control fields in frontmatter
- **WHEN** the skill is scanned
- **THEN** `DisableModelInvocation` is `false`
- **AND** `UserInvocable` is `true`
- **AND** `ArgumentHint` is `null`
- **AND** `Metadata.Subagent` is `null`

### Requirement: Slash commands work with scheduled jobs

The system SHALL support slash-command syntax in scheduled job and reminder
payloads. When a scheduled job fires with a message starting with `/`, the
dispatch logic SHALL apply, including deterministic routed subagent behavior
when `metadata.subagent` is present.

#### Scenario: Scheduled job with slash command routed to subagent

- **GIVEN** a reminder with payload `/netclaw-operations check health`
- **AND** `netclaw-operations` has valid `metadata.subagent`
- **WHEN** the reminder fires and sends the message to the session
- **THEN** slash-command dispatch routes to the configured user-facing subagent
- **AND** skill body is not inlined into the main session prompt stack

#### Scenario: Scheduled job with inline slash command

- **GIVEN** a reminder with payload `/netclaw-operations check health`
- **AND** `netclaw-operations` has no `metadata.subagent`
- **WHEN** the reminder fires and sends the message to the session
- **THEN** the slash-command dispatch intercepts it
- **AND** the operations skill is loaded before the LLM processes the message

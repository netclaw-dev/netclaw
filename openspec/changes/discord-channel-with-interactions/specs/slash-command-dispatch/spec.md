## MODIFIED Requirements

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

This interception behavior SHALL be channel-agnostic, including Discord text
messages, and SHALL NOT require platform-native slash command registration.

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

#### Scenario: Discord text slash command works without app registration

- **GIVEN** Discord app-command registration is not configured
- **WHEN** the user sends `/netclaw-operations check daemon health` as plain Discord message content
- **THEN** session-level slash-command interception executes deterministically
- **AND** the message is not treated as normal free-form model input

#### Scenario: Unrecognized slash command returns error

- **WHEN** the user sends `/nonexistent do something`
- **THEN** the system returns a deterministic error message
- **AND** the error lists available slash commands
- **AND** the message is NOT passed to the LLM for interpretation

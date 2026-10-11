## ADDED Requirements

### Requirement: Channel binding lifetime follows session lifetime

Slack, Discord, and Mattermost bindings SHALL keep their session output subscription while the session remains active. A binding SHALL NOT stop on an independent idle timeout. After a binding receives committed `SessionDeactivated`, it SHALL drain its session pipeline and stop. A conversation parent SHALL NOT have an independent idle timeout. It SHALL stay active while any binding child remains and stop after its last binding child terminates.

#### Scenario: Binding stays active while its session remains active

- **GIVEN** a Slack, Discord, or Mattermost binding has an active session
- **WHEN** the former binding idle timeout would expire
- **THEN** the binding stays active
- **AND** it keeps its session output subscription

#### Scenario: Binding stops after committed session deactivation

- **GIVEN** a binding receives committed `SessionDeactivated`
- **WHEN** its session pipeline drain completes
- **THEN** the binding stops
- **AND** it does not restart its pipeline for that deactivation

#### Scenario: Parent stays active while another binding child remains

- **GIVEN** a conversation parent has two binding children
- **WHEN** one child terminates after session deactivation
- **THEN** the parent remains active
- **AND** it keeps the other child

#### Scenario: Parent stops after its last binding child

- **GIVEN** a conversation parent has one binding child
- **WHEN** that child terminates after session deactivation
- **THEN** the parent stops

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

## MODIFIED Requirements

### Requirement: skill_load tool

The system SHALL provide `skill_load` only when the skills subsystem is enabled
for the deployment and exposed to the requesting audience. Public sessions SHALL
not use `skill_load` to enumerate or load hidden internal skills.

#### Scenario: skill_load unavailable to Public

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** tool definitions are built or the session attempts to use
  `skill_load`
- **THEN** `skill_load` is absent or denied for that session

#### Scenario: skill_load unavailable when skills runtime disabled

- **GIVEN** `SkillSync.Enabled` is `false` in config
- **WHEN** a Team session attempts to use `skill_load`
- **THEN** the tool is absent or denied because the skills subsystem is runtime-disabled

### Requirement: skill_read_resource tool

The system SHALL provide `skill_read_resource` only when the skills subsystem is
enabled for the deployment and exposed to the requesting audience. Public
sessions SHALL not use it to recover skill internals.

#### Scenario: skill_read_resource unavailable to Public

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** tool definitions are built or the session attempts to use
  `skill_read_resource`
- **THEN** `skill_read_resource` is absent or denied for that session

#### Scenario: skill index does not advertise hidden skill tools to Public

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** the prompt is assembled
- **THEN** the injected skill guidance does not instruct the model to use
  `skill_load` or `skill_read_resource`

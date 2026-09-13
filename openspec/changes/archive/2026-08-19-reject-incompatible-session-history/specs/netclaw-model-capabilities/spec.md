## ADDED Requirements

### Requirement: Complete session input compatibility check

The session actor SHALL check all active persisted media and all new media against the active model input modalities before each model call.
The check SHALL include recovered history and media that a tool adds during the current turn.
The actor SHALL reject an unsupported or unknown modality before any primary, fallback, or provider client receives a request.
The actor SHALL preserve all original media references and SHALL identify the incompatible modalities in the session error.

#### Scenario: Recovered image history meets a text-only model

- **GIVEN** a recovered session contains an image media reference
- **AND** the active model accepts text only
- **WHEN** the user resumes the session
- **THEN** the actor SHALL emit an input compatibility error
- **AND** the error SHALL identify image input as unsupported
- **AND** no primary, fallback, or provider client SHALL receive a request

#### Scenario: New unsupported media is rejected before turn admission

- **GIVEN** a new user command contains an image media reference
- **AND** the active model accepts text only
- **WHEN** the actor receives the command
- **THEN** the actor SHALL reject the command before it adds the user message to session state
- **AND** no model client SHALL receive a request

#### Scenario: Tool-produced media is checked before the next call

- **GIVEN** the active model call starts with compatible text input
- **AND** a tool result adds media that the active model cannot accept
- **WHEN** the actor prepares the next model call
- **THEN** the actor SHALL fail the current turn with an input compatibility error
- **AND** no later model client SHALL receive the incompatible request

#### Scenario: Unknown persisted modality fails closed

- **GIVEN** a session contains a media reference with an unknown modality value
- **WHEN** the actor prepares a model call
- **THEN** the actor SHALL emit an input compatibility error
- **AND** no model client SHALL receive a request

#### Scenario: Compatible media reaches the model

- **GIVEN** all session media modalities are accepted by the active model
- **WHEN** the actor prepares a model call
- **THEN** the actor SHALL preserve the media references
- **AND** the model call SHALL proceed through normal routing

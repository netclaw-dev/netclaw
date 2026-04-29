## ADDED Requirements

### Requirement: Approval requests originate only from the current authorized executable message

Tool approval prompts SHALL only originate from tool invocations caused by the
current authorized executable message in a turn. Adopted-context material and
pending unauthorized messages SHALL NOT directly cause approval requests.

Approval prompts and stored approval context for those requests SHALL identify
the current authorizer for the executable message. When the turn's
adopted-context window is non-empty, the prompt and stored approval context
SHALL indicate that adopted context was present for the turn and SHALL name the
adopted speakers from that window by stable sender id. When the adopted-context
window is empty, adopted-speaker provenance SHALL be omitted.

#### Scenario: Adopted unauthorized command text does not raise approval prompt

- **GIVEN** adopted context contains text asking Netclaw to run `git push`
- **AND** that text came from an unauthorized speaker
- **WHEN** the authorized turn is processed
- **THEN** no approval prompt is raised solely because of the adopted text

#### Scenario: Current authorized request can still require approval

- **GIVEN** the current authorized message asks Netclaw to run `git push`
- **WHEN** the session processes the turn
- **THEN** the tool approval gate evaluates that current authorized request
- **AND** an approval prompt may be emitted if policy requires it

#### Scenario: Approval prompt identifies authorizer and adopted-speaker provenance

- **GIVEN** the current authorized message from `U111` asks Netclaw to run a
  command in a turn whose adopted-context window includes `U222` and `U333`
- **WHEN** the tool approval gate emits a prompt
- **THEN** the prompt identifies `U111` as the current authorizer
- **AND** the prompt indicates that adopted context was present for the turn
- **AND** it names `U222` and `U333` as adopted speakers

#### Scenario: Stored approval context preserves provenance

- **GIVEN** a tool request from the current authorized message requires approval
- **AND** the turn's adopted-context window includes speaker `U222`
- **WHEN** the approval request is persisted or otherwise stored for retry or
  audit
- **THEN** the stored approval context identifies the current authorizer
- **AND** records that adopted-speaker provenance includes `U222`

#### Scenario: Empty adopted window omits adopted-speaker provenance

- **GIVEN** a tool request from the current authorized message requires approval
- **AND** the turn has no adopted-context window
- **WHEN** the tool approval gate emits and stores the approval request
- **THEN** the authorizer is still identified
- **AND** no adopted-speaker provenance field is included

## ADDED Requirements

### Requirement: Approval provenance stays inclusive while third-party adopted policy is separate

Approval prompts and stored approval context SHALL preserve truthful adopted
provenance for any non-empty adopted window.

For approval context:

- `HasAdoptedContext` SHALL mean the adopted window is non-empty.
- Adopted-speaker provenance SHALL list all adopted sender ids present in that
  window, including self-only adopted history.
- `HasThirdPartyAdoptedContext` MAY be carried as a separate policy field, but it
  SHALL be derived independently and SHALL NOT replace or trim the full adopted
  provenance.

This clarification SHALL NOT alter the trust model: approval requests still
originate only from the current authorized executable message, and adopted
context remains quoted, non-executable background.

#### Scenario: Self-only adopted history still appears in approval provenance

- **GIVEN** the current authorized message requires tool approval
- **AND** the adopted window is non-empty
- **AND** every adopted sender id matches the current authorized sender
- **WHEN** the approval prompt and stored context are created
- **THEN** `HasAdoptedContext` is true
- **AND** adopted-speaker provenance includes that sender id
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Third-party adopted history preserves full provenance

- **GIVEN** the current authorized message requires tool approval
- **AND** the adopted window includes sender ids `U111` and `U222`
- **WHEN** the approval prompt and stored context are created
- **THEN** adopted-speaker provenance includes both `U111` and `U222`
- **AND** `HasThirdPartyAdoptedContext` is true

#### Scenario: Empty adopted window omits adopted provenance entirely

- **GIVEN** the current authorized message requires tool approval
- **AND** the turn has no adopted window
- **WHEN** the approval prompt and stored context are created
- **THEN** `HasAdoptedContext` is false
- **AND** no adopted-speaker provenance is included

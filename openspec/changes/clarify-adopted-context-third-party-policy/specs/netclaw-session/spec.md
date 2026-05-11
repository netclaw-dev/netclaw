## ADDED Requirements

### Requirement: Persisted adopted-context metadata separates truthful provenance from third-party policy

When the session persists or reuses an adopted-context record, it SHALL preserve
the full adopted window truthfully and SHALL NOT collapse self-only adopted
history into "no adopted context."

For persisted session metadata:

- `HasAdoptedContext` SHALL mean the adopted window is non-empty.
- Adopted-speaker provenance SHALL include all sender ids present in that
  adopted window.
- `HasThirdPartyAdoptedContext` SHALL be tracked as a separate policy concept and
  SHALL be true only when any adopted sender id differs from the current
  authorized author of the executable message.

This metadata split SHALL coexist with the existing trust model that adopted
context is quoted, non-executable context and only the current authorized
message is executable.

#### Scenario: Persisted record keeps self-only adopted window truthful

- **GIVEN** an adopted-context record is written for an authorized turn
- **AND** every adopted sender id matches the current authorized sender
- **WHEN** the session persists the record
- **THEN** `HasAdoptedContext` is true
- **AND** adopted-speaker provenance includes that sender id
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Persisted record marks third-party policy separately

- **GIVEN** an adopted-context record is written for an authorized turn
- **AND** the adopted window includes a sender id different from the current
  authorized sender
- **WHEN** the session persists the record
- **THEN** adopted-speaker provenance includes all adopted sender ids
- **AND** `HasThirdPartyAdoptedContext` is true

#### Scenario: Adopted context remains non-executable after metadata split

- **GIVEN** a persisted record reports `HasAdoptedContext=true`
- **WHEN** the session later uses that record for audit, retry, or recovery
- **THEN** the adopted window remains quoted, non-executable context
- **AND** only the current authorized message remains executable

## ADDED Requirements

### Requirement: Adopted-context handoff distinguishes presence from third-party policy

Threaded adapters SHALL preserve two distinct handoff facts when constructing an authorized turn with an adopted window:

- `HasAdoptedContext`: true when the adopted window is non-empty.
- `HasThirdPartyAdoptedContext`: true when any adopted sender id differs from
  the current authorized sender for the executable message.

The handoff SHALL also preserve adopted-speaker provenance as the full set of
sender ids present in the adopted window. That provenance SHALL remain inclusive
even when the adopted window contains only prior messages from the current
authorized sender.

#### Scenario: Self-only adopted window carries truthful handoff metadata

- **GIVEN** a threaded adapter adopts prior messages from the same sender as the
  current authorized message
- **WHEN** it constructs the authorized turn handoff
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is false
- **AND** adopted-speaker provenance still includes that sender id

#### Scenario: Mixed-sender adopted window marks third-party state

- **GIVEN** a threaded adapter adopts prior messages from `U111` and `U222`
- **AND** the current authorized sender is `U111`
- **WHEN** it constructs the authorized turn handoff
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is true
- **AND** adopted-speaker provenance includes both `U111` and `U222`

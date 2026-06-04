## MODIFIED Requirements

### Requirement: Adopted thread context is not direct durable-memory authority

Adopted thread context SHALL be treated as ephemeral quoted context for model
reasoning, not as ordinary authoritative turn history for direct durable memory
writes, unless the current authorized message explicitly asks Netclaw to store,
correct, or otherwise elevate that information.

For memory policy inputs, `HasAdoptedContext` SHALL mean the adopted window is
non-empty, while `HasThirdPartyAdoptedContext` SHALL mean the adopted window
contains at least one sender id other than the current authorized author.
Automatic memory suppression or equivalent extra caution that exists because the
adopted window may contain somebody else's words SHALL key off
`HasThirdPartyAdoptedContext`, not `HasAdoptedContext` alone.

Truthful approval, audit, and provenance data for the turn SHALL continue to
reflect the full adopted window whenever it is non-empty, including self-only
adopted history.

#### Scenario: Unauthorized adopted fact does not directly write memory

- **GIVEN** adopted context contains an unauthorized speaker claiming a new host
  password
- **WHEN** the authorized user does not explicitly ask Netclaw to store it
- **THEN** the adopted claim does not directly produce a durable memory write

#### Scenario: Authorized message may explicitly elevate adopted fact

- **GIVEN** adopted context contains a prior thread fact
- **AND** the current authorized message says to save that fact to memory
- **WHEN** memory policy otherwise permits the write
- **THEN** the durable memory path may proceed under the current authorized
  message's authority rather than the adopted speaker's authority

#### Scenario: Self-only adopted history does not trigger third-party suppression

- **GIVEN** the adopted window is non-empty
- **AND** every adopted sender id matches the current authorized author
- **WHEN** automatic memory policy evaluates the turn
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is false
- **AND** memory suppression is not triggered solely by adopted-context presence

#### Scenario: Third-party adopted history triggers suppression input

- **GIVEN** the adopted window contains a sender id different from the current
  authorized author
- **WHEN** automatic memory policy evaluates the turn
- **THEN** `HasThirdPartyAdoptedContext` is true
- **AND** any third-party-adopted suppression rule keys off that state

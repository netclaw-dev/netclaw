## ADDED Requirements

### Requirement: Approval-resumed memory decisions use turn context

Memory recall, memory curation, and memory checkpoint payloads created during an approval-resumed session turn SHALL use the active or restored turn context for audience, boundary, and adopted-context policy inputs. They SHALL NOT derive those inputs from missing live transport metadata after recovery.

#### Scenario: Recovered third-party adopted context suppresses automatic curation

- **GIVEN** an approval-paused turn was originally created with third-party adopted context
- **AND** the session cold-recovers before the approval response arrives
- **WHEN** the approved tool redrive completes and memory curation evaluates proposals for the resumed turn
- **THEN** curation reads `HasThirdPartyAdoptedContext` from the restored turn context
- **AND** automatic memory formation is suppressed unless the authorized user explicitly elevated the adopted fact

#### Scenario: Self-only adopted context does not suppress solely by presence

- **GIVEN** an approval-paused turn was originally created with self-only adopted context
- **WHEN** the recovered turn resumes and memory policy evaluates the turn
- **THEN** memory policy sees adopted context as present
- **AND** `HasThirdPartyAdoptedContext` remains false
- **AND** automatic memory suppression is not triggered solely by adopted-context presence

#### Scenario: Recovered memory boundary matches original turn

- **GIVEN** an approval-paused Team turn was recovered after restart
- **WHEN** memory recall or checkpoint payloads are created during the resumed continuation
- **THEN** their audience and boundary come from the restored turn context
- **AND** they do not fall back to Public because live `MessageSource` is absent

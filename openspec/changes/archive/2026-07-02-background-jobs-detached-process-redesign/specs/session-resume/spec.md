# session-resume Delta

## ADDED Requirements

### Requirement: Idle passivation proceeds with pending approvals

A session SHALL NOT defer idle passivation because tool approval prompts are
outstanding. Pending approval state is journaled (`ToolApprovalRequested` /
`ToolApprovalResolved`) and the approval response path already rehydrates a
passivated session and resumes the original turn, so keeping the session in
memory while a human decides adds no correctness — only resident memory.
Active live subscribers (CLI/TUI connections) SHALL continue to defer
passivation, because subscriber connections are ephemeral and cannot survive
actor stop. The existing resolved-approval abandonment behavior (a parked tool
batch whose approval was granted but whose tool result never completed) SHALL
be preserved.

#### Scenario: Session passivates with an approval prompt outstanding

- **GIVEN** a session is idle past its idle timeout
- **AND** a tool approval prompt is outstanding
- **AND** no live subscribers are attached
- **WHEN** the receive timeout fires
- **THEN** the session passivates normally
- **AND** the pending approval remains recoverable from the journal

#### Scenario: Approval click after passivation resumes the turn

- **GIVEN** a session passivated with an approval prompt outstanding
- **WHEN** the user responds to the approval prompt
- **THEN** the session rehydrates from the journal
- **AND** re-drives the parked tool batch per the existing restored-approval
  requirements

#### Scenario: Live subscribers still defer passivation

- **GIVEN** a session is idle past its idle timeout
- **AND** a live CLI or TUI subscriber is attached
- **WHEN** the receive timeout fires
- **THEN** passivation is deferred while the subscriber remains attached

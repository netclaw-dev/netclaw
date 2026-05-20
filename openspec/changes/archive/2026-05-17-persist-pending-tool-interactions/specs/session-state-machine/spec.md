## ADDED Requirements

### Requirement: Tool interaction response accepted in all session phases

The session actor SHALL accept a `ToolInteractionResponse` in the `Ready`,
`Passivating`, and `Compacting` phases, not only in `Processing`. A response
SHALL NOT be left unhandled (dead-lettered) because the session moved out of
`Processing` while an approval prompt was outstanding. Handling SHALL preserve
the legal phase-transition rules: re-driving a tool batch transitions
`Ready → Processing`, and aborting passivation transitions `Passivating → Ready`.

#### Scenario: Response in Ready re-drives the tool batch

- **GIVEN** the session actor is in phase `Ready` with a restored pending tool
  interaction (e.g. after cold recovery)
- **WHEN** a `ToolInteractionResponse` for that call arrives
- **THEN** the actor SHALL transition `Ready → Processing`
- **AND** re-drive the parked tool batch and continue the turn

#### Scenario: Response in Passivating aborts passivation then re-drives

- **GIVEN** the session actor is in phase `Passivating` with a pending tool
  interaction
- **WHEN** a `ToolInteractionResponse` for that call arrives before the actor stops
- **THEN** the actor SHALL abort passivation, cancel its passivation timers,
  and transition `Passivating → Ready`
- **AND** then handle the response and re-drive the tool batch

#### Scenario: Response in Compacting is buffered and replayed

- **GIVEN** the session actor is in phase `Compacting`
- **WHEN** a `ToolInteractionResponse` arrives
- **THEN** the actor SHALL buffer the response rather than re-driving mid-compaction
- **AND** SHALL replay the buffered response to itself after compaction completes

#### Scenario: Unknown call id does not transition phase

- **GIVEN** the session actor is in phase `Ready` with no matching pending
  interaction and no reconstructable call in history
- **WHEN** a `ToolInteractionResponse` arrives
- **THEN** the actor SHALL remain in phase `Ready`
- **AND** SHALL emit a user-visible "approval prompt expired" message

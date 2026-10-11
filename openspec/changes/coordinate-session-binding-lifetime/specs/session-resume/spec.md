## REMOVED Requirements

### Requirement: Idle passivation proceeds with pending approvals

**Reason**: The old requirement combined approval recovery with idle-passivation eligibility and made live CLI and TUI subscribers block passivation. The session-state-machine capability now owns eligibility.

**Migration**: Keep approval recovery in `Approval responses resume after idle passivation`. Use the session-state-machine rules for idle eligibility.

## ADDED Requirements

### Requirement: Approval responses resume after idle passivation

When a session with a journaled approval passivates after its active work ends, an approval response SHALL rehydrate the session and resume the original tool batch. This recovery SHALL preserve the existing requester and restored-approval checks. The session-state-machine capability defines whether idle passivation can start. Resolved approvals without a completed tool result SHALL keep the existing abandonment behavior.

#### Scenario: Approval response resumes the passivated turn

- **GIVEN** a session has a journaled approval and no active work when idle passivation starts
- **WHEN** the user responds to the approval prompt after passivation
- **THEN** the session rehydrates from the journal
- **AND** it re-drives the parked tool batch under the restored-approval requirements

#### Scenario: Recovery closes an approved call without a tool result

- **GIVEN** recovery finds a granted approval without a journaled tool result
- **WHEN** the session returns to `Ready`
- **THEN** the session persists a `ToolBatchAbandoned` event with a synthetic tool result
- **AND** clears the approval state
- **AND** does not execute the approved tool again

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

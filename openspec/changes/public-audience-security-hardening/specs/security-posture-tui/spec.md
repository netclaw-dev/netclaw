## MODIFIED Requirements

### Requirement: Posture step position in wizard flow

The SecurityPosture step SHALL appear after ChatServices and before the
Feature Selection step in the wizard flow. For non-Personal postures, the
Feature Selection step SHALL appear immediately after SecurityPosture so
that feature availability is configured before channel audience assignment.

#### Scenario: Step order with Feature Selection

- **WHEN** the user completes the SecurityPosture step
- **AND** the selected posture is Team or Public
- **THEN** the next step is Feature Selection
- **AND** after Feature Selection, the next applicable step follows

#### Scenario: Step order without Feature Selection

- **WHEN** the user completes the SecurityPosture step
- **AND** the selected posture is Personal
- **THEN** the Feature Selection step is skipped
- **AND** the next applicable step follows directly

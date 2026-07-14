## ADDED Requirements

### Requirement: Approval evaluation uses admitted turn authority

Tool approval evaluation SHALL receive the same required admitted `TurnContext` as authorization and dispatch. Approval infrastructure SHALL NOT be nullable for tool-enabled sessions, and missing approval infrastructure SHALL NOT mean approval is bypassed.

#### Scenario: Approval policy cannot be supplied

- **GIVEN** a tool-enabled session cannot construct its required approval policy
- **WHEN** it attempts to execute a tool batch
- **THEN** execution fails before dispatch
- **AND** no tool runs as though approval were unnecessary

#### Scenario: Child approval retains parent turn authority

- **GIVEN** a child run forked from an admitted parent turn
- **WHEN** a child tool requires approval
- **THEN** approval evaluation uses the explicitly inherited turn authority
- **AND** no audience or source fallback is inferred

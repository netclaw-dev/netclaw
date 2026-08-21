## MODIFIED Requirements

### Requirement: First-party tool outcomes are machine-actionable

First-party workspace tool execution SHALL produce exactly one call-local
outcome category: `success`, `invalid_input`, `access_denied`, `not_found`,
`transient_failure`, or `recoverable_correction`. The category SHALL be separate
from the model-facing string. The system SHALL NOT infer it from that string.
The outcome MAY carry canonical file activity. A `recoverable_correction`
outcome SHALL carry exactly one closed internal remediation code. Every other
outcome SHALL reject remediation. Dynamic facts SHALL remain in the bounded
model-facing result and SHALL NOT become a free-form receipt field. It SHALL NOT
change the public string-returning `INetclawTool` contract.

The parent and child execution paths SHALL use one shared presenter to turn a
validated remediation into one model-facing next action. The presenter SHALL
omit a next action that names a tool hidden from the current audience. It SHALL
NOT grant authority, execute a tool, rewrite a tool call, or persist the
remediation.

#### Scenario: Access denial has no successful file activity

- **GIVEN** `file_read` is called for a path outside the current read authority
- **WHEN** scoped access denies the call
- **THEN** the outcome category is `access_denied`
- **AND** the outcome contains no successful file activity
- **AND** the outcome contains no remediation
- **AND** the model receives a bounded denial string

#### Scenario: Missing declaration has a typed correction

- **GIVEN** a workspace tool can continue after the project directory is declared
- **WHEN** the missing declaration is the only blocker
- **THEN** the outcome category is `recoverable_correction`
- **AND** its remediation code is `SetWorkingDirectory`
- **AND** the shared presenter tells the model to call `set_working_directory`
- **AND** no authority is granted by the outcome itself

#### Scenario: Parent and child present the same correction

- **GIVEN** the same validated recoverable correction reaches a parent and child session
- **WHEN** each path creates its tool-role message
- **THEN** both messages contain the same single next action
- **AND** neither path parses the original result to choose that action

#### Scenario: Hidden declaration tool is not revealed

- **GIVEN** a corrective receipt uses `SetWorkingDirectory`
- **AND** `set_working_directory` is hidden from the current audience
- **WHEN** the shared presenter creates the tool-role message
- **THEN** it does not add an action that names the hidden tool

#### Scenario: Recoverable correction requires a known value

- **WHEN** an internal caller creates a recoverable correction without a remediation
- **THEN** receipt construction fails closed
- **AND** an undefined remediation code also fails closed

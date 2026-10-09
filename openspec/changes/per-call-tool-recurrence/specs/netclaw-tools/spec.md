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

The shared dispatcher, `DispatchingToolExecutor`, SHALL classify a terminal policy denial as `access_denied` for parent and child callers. An approval request SHALL NOT create a terminal receipt before its final decision.

The receipt category answers what happened in a stable machine-readable form.
The separate bounded result explains why to the model. The receipt does not copy
or parse that text.

The parent and child execution paths SHALL use one shared presenter to turn a
validated remediation into one model-facing next action. The presenter SHALL
omit a next action that names a tool hidden from the current audience. It SHALL
NOT grant authority, execute a tool, rewrite a tool call, or persist the
remediation.

The following pseudocode shows the required separation:

```text
tool implementation returns:
  result  = raw factual text
  receipt = trusted internal facts for the actor

dispatcher normal-return path:
  redact and bound the factual text

shared presenter receives:
  current model-result text
  receipt.RemediationCode
  current tool visibility

shared presenter returns:
  one final tool-role message for the model
```

Example receipt shapes:

```text
successful file read:
  category      = Success
  file activity = Read("/workspace/project/README.md")
  remediation   = none

policy denial before tool execution:
  category      = AccessDenied
  file activity = empty
  remediation   = none
  model result  = "Tool access denied: tool_not_allowed_for_audience_profile"

path access denial inside file_read:
  category      = AccessDenied
  file activity = empty
  remediation   = none
  model result  = "Error: Path is outside trusted roots: /workspace."

correctable missing path base:
  category      = RecoverableCorrection
  file activity = empty
  remediation   = SetWorkingDirectory
  model result  = "Error: invalid_context: No project or session directory is available."
```

Counterexamples:

| Result | Why it is invalid |
|---|---|
| `AccessDenied` plus successful file activity | A denied call did not read or change the file. |
| `Success` plus `SetWorkingDirectory` remediation | Only a recoverable correction may carry remediation. |
| Approval request plus terminal `AccessDenied` receipt | Approval is a paused, undecided call rather than a denial. |
| Inferring `not_found` because the string contains “not found” | The typed receipt, not prose, owns the outcome. |

#### Scenario: Access denial has no successful file activity

- **GIVEN** `file_read` is called for a path outside the current audience's read authority
- **WHEN** the path access decision denies the call
- **THEN** the outcome category is `access_denied`
- **AND** the outcome contains no successful file activity
- **AND** the outcome contains no remediation
- **AND** the model receives a bounded denial string

#### Scenario: Dispatcher denial has one category

- **GIVEN** policy denies a tool before its implementation runs
- **WHEN** a parent or child actor invokes the tool
- **THEN** the receipt category is `access_denied`
- **AND** neither actor reports `transient_failure`
- **AND** the separate model-facing result includes the bounded policy reason

#### Scenario: Approval request is not terminal

- **GIVEN** a tool requires human approval
- **WHEN** the dispatcher parks the call for that decision
- **THEN** no terminal denial receipt is recorded
- **AND** an approved retry can execute the tool

#### Scenario: Recoverable correction stays distinct from failure

- **GIVEN** a workspace tool can continue after the project directory is declared
- **AND** `set_working_directory` is visible to the current model
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
- **AND** it returns the factual tool result unchanged

#### Scenario: Ambiguous file edit has one next action

- **GIVEN** `file_edit` finds more than one `OldString` match
- **WHEN** `ReplaceAll` is false
- **THEN** the tool changes no file
- **AND** the result reports the match count
- **AND** the remediation code is `ProvideUniqueOldString`
- **AND** the presenter adds one fixed retry action

#### Scenario: Host temporary path suggests the managed temporary directory

- **GIVEN** shell policy proposes the managed temporary directory for a host temporary path
- **WHEN** the call returns a recoverable correction
- **THEN** the remediation code is `UseManagedTemporaryDirectory`
- **AND** the presenter adds one fixed managed-temporary-directory action
- **AND** a later retry still runs normal shell authorization

#### Scenario: Recoverable correction requires a known value

- **WHEN** an internal caller creates a recoverable correction without a remediation
- **THEN** receipt construction fails closed
- **AND** an undefined remediation code also fails closed


An accessible background-job status query SHALL supply a closed typed pending-operation fact only for `Pending` or `Running` status.
This fact SHALL use the existing internal receipt path and SHALL grant no authority.
The actor SHALL bind it to the original prepared query identity.
The fact SHALL NOT apply to cancellation, validation failure, denial, error, a terminal job, or another identity.
The fact SHALL NOT add a free-form receipt field or change the public string-returning tool contract.

#### Scenario: Pending state remains typed after text presentation

- **GIVEN** a valid noncancel query receives an accessible `Running` job response
- **WHEN** the tool returns its model-facing text
- **THEN** the internal receipt retains the closed pending-operation fact
- **AND** the dispatcher does not replace it with generic success

#### Scenario: A result without a receipt remains an evidence gap

- **GIVEN** the actor receives an actual tool result without a trusted receipt
- **WHEN** it records detector evidence
- **THEN** it reports an explicit missing-receipt contract failure
- **AND** it preserves paired results and prior evidence without fabricated success
- **AND** it settles partial/failure without another model request

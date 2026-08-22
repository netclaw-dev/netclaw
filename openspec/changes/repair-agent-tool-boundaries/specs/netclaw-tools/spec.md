## MODIFIED Requirements

### Requirement: First-party tool outcomes are machine-actionable

First-party workspace tool execution SHALL produce exactly one call-local outcome category: `success`, `invalid_input`, `access_denied`, `not_found`, `transient_failure`, or `recoverable_correction`. The category SHALL be separate from the model-facing string. The system SHALL NOT infer it from that string. The outcome MAY carry canonical file activity. A `recoverable_correction` outcome SHALL carry exactly one defined internal remediation code. Every other category SHALL reject remediation. The outcome SHALL NOT change the public string-returning `INetclawTool` contract.

The shared dispatcher, `DispatchingToolExecutor`, SHALL classify a terminal policy denial as `access_denied` for parent and child callers. An approval request SHALL NOT create a terminal receipt before its final decision.

#### Scenario: Access denial has no successful file activity

- **GIVEN** `file_read` is called for a path outside the current read authority
- **WHEN** scoped access denies the call
- **THEN** the outcome category is `access_denied`
- **AND** the outcome contains no successful file activity
- **AND** the model receives a bounded denial string

#### Scenario: Dispatcher denial has one category

- **GIVEN** policy denies a tool before its implementation runs
- **WHEN** a parent or child actor invokes the tool
- **THEN** the receipt category is `access_denied`
- **AND** neither actor reports `transient_failure`

#### Scenario: Approval request is not terminal

- **GIVEN** a tool requires human approval
- **WHEN** the dispatcher parks the call for that decision
- **THEN** no terminal denial receipt is recorded
- **AND** an approved retry can execute the tool

#### Scenario: Recoverable correction stays distinct from failure

- **GIVEN** a workspace tool can continue after the project directory is declared
- **WHEN** the missing declaration is the only blocker
- **THEN** the outcome category is `recoverable_correction`
- **AND** its remediation code is `SetWorkingDirectory`
- **AND** no authority is granted by the outcome itself

### Requirement: Working context records successful file activity only

`WorkingContext.RecentFiles` SHALL update only from canonical file activity in a successful tool outcome. Failed, denied, missing, malformed, or corrective tool results SHALL NOT update recent files. The session pipeline SHALL NOT infer file activity only from authored argument names. Only a successful `set_working_directory` receipt MAY replace the declared project directory.

#### Scenario: Failed write does not become recent

- **GIVEN** `file_write` targets a denied path
- **WHEN** the tool returns an access-denied outcome
- **THEN** the authored path is absent from `RecentFiles`

#### Scenario: Parallel reads record bounded canonical activity

- **GIVEN** two authorized files are read by separate `file_read` calls
- **WHEN** the session applies their successful receipts
- **THEN** both canonical resolved paths are added to `RecentFiles`
- **AND** no authored relative spelling becomes a separate file

#### Scenario: Another tool cannot declare a project

- **GIVEN** a successful receipt from a tool other than `set_working_directory`
- **WHEN** the receipt contains a project directory
- **THEN** the actor rejects that project effect
- **AND** the current project directory remains unchanged

## REMOVED Requirements

### Requirement: Batch file reads validate before content access

**Reason**: The tool duplicates parallel bounded `file_read` calls and can create a large combined result.

**Migration**: Use one or more bounded `file_read` calls. A model can issue independent reads in parallel.

### Requirement: JSON projection uses bounded data semantics

**Reason**: The tool duplicates a narrow data query language and lacks a distinct durable product use.

**Migration**: Use `file_read` for bounded JSON content. Use a purpose-built producer tool when structured data needs a stable projection.

## MODIFIED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: First-party tool outcomes are machine-actionable

First-party workspace tool execution SHALL produce exactly one call-local outcome category: `success`, `invalid_input`, `access_denied`, `not_found`, `transient_failure`, or `recoverable_correction`. The category SHALL be separate from the model-facing string. The system SHALL NOT infer it from that string. The outcome MAY carry canonical file activity. A `recoverable_correction` outcome SHALL carry exactly one defined internal remediation code. Every other category SHALL reject remediation. The outcome SHALL NOT change the public string-returning `INetclawTool` contract.

The shared dispatcher, `DispatchingToolExecutor`, SHALL classify a terminal policy denial as `access_denied` for parent and child callers. An approval request SHALL NOT create a terminal receipt before its final decision.

The receipt category answers what happened in a stable machine-readable form.
The separate bounded result explains why to the model. The receipt does not copy
or parse that text.

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

filesystem scope denial inside file_read:
  category      = AccessDenied
  file activity = empty
  remediation   = none
  model result  = "Error: Team trust context may only access files inside the
                   current session directory or configured roots: /workspace."

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
- **AND** the separate model-facing result includes the bounded policy reason

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

Concrete activity example:

```text
project = /workspace/project

file_read(Path = "README.md")
  -> Success + Read("/workspace/project/README.md")

file_read(Path = "./README.md")
  -> Success + Read("/workspace/project/README.md")

RecentFiles contains one canonical entry:
  /workspace/project/README.md

file_write(Path = "../denied.txt") -> AccessDenied
  -> no RecentFiles entry for ../denied.txt

file_read receipt carries DeclaredProjectDirectory("/outside")
  -> file activity may be applied when otherwise valid
  -> project effect is rejected because the producer is not set_working_directory
```

#### Scenario: Failed write does not become recent

- **GIVEN** `file_write` targets a denied path
- **WHEN** the tool returns an access-denied outcome
- **THEN** the authored path is absent from `RecentFiles`

#### Scenario: Parallel reads record bounded canonical activity

- **GIVEN** `README.md` and `docs/guide.md` resolve under `/workspace/project`
- **AND** separate `file_read` calls read both paths in one tool batch
- **WHEN** the session applies their successful receipts
- **THEN** `/workspace/project/README.md` and `/workspace/project/docs/guide.md` are added to `RecentFiles`
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

Example: replace `file_read_many(Paths = ["README.md", "LICENSE"])` with two
bounded `file_read` calls in one model response. This removal does not remove an
unrelated Deferred tool; it removes the `file_read_many` registration, schema,
search result, load result, and dispatch route specifically.

### Requirement: JSON projection uses bounded data semantics

**Reason**: The tool duplicates a narrow data query language and lacks a distinct durable product use.

**Migration**: Use `file_read` for bounded JSON content. Use a purpose-built producer tool when structured data needs a stable projection.

Example: replace `json_read(Path = "package.json", Pointer = "/version")` with
a bounded `file_read(Path = "package.json")` when the document itself is the
required evidence. If a workflow needs a stable domain value rather than file
content, add a purpose-built producer tool; do not recreate a general JSON query
language in another generic reader.

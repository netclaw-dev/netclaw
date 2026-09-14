## ADDED Requirements

The terms in these requirements use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).
The later `repair-agent-tool-boundaries` change removes the `json_read` and
`file_read_many` requirements. It also strengthens the receipt contract.

### Requirement: First-party tool outcomes are machine-actionable

First-party workspace tool execution SHALL produce exactly one call-local
outcome category: `success`, `invalid_input`, `access_denied`, `not_found`,
`transient_failure`, or `recoverable_correction`. The category SHALL be separate
from the model-facing string and SHALL NOT be inferred by parsing that string.
The outcome MAY carry a bounded remediation code and canonical file activity.
It SHALL NOT change the public string-returning `INetclawTool` contract.

#### Scenario: Access denial has no successful file activity

- **GIVEN** `file_read` is called for a path outside the trusted roots
- **WHEN** the path access decision denies the call
- **THEN** the outcome category is `access_denied`
- **AND** the outcome contains no successful file activity
- **AND** the model receives a bounded denial string

#### Scenario: Recoverable correction stays distinct from failure

- **GIVEN** a workspace tool can continue after the project directory is
  declared
- **WHEN** the missing declaration is the only blocker
- **THEN** the outcome category is `recoverable_correction`
- **AND** its remediation code identifies `set_working_directory`
- **AND** no authority is granted by the outcome itself

### Requirement: Working context records successful file activity only

`WorkingContext.RecentFiles` SHALL be updated only from canonical file activity
reported by a successful tool outcome. Failed, denied, missing, malformed, or
corrective tool results SHALL NOT update recent files. The session pipeline
SHALL NOT infer successful file activity solely from authored argument names.

#### Scenario: Failed write does not become recent

- **GIVEN** `file_write` targets a denied path
- **WHEN** the tool returns an access-denied outcome
- **THEN** the authored path is absent from `RecentFiles`

#### Scenario: Successful batch read records canonical files

- **GIVEN** `file_read_many` successfully reads two authorized relative paths
- **WHEN** the session applies the tool receipt
- **THEN** both canonical resolved paths are added to `RecentFiles`
- **AND** no authored relative spelling is treated as a separate file

### Requirement: Recursive workspace search is bounded and structured

The system SHALL provide a `file_search` tool for recursive literal file-name
and text search under one trusted root. The tool SHALL accept explicit
result, file, and content-byte ceilings; SHALL NOT follow directory symlinks;
and SHALL report matches, skipped entries, and truncation state. Search SHALL
use filesystem APIs rather than an external executable.

#### Scenario: Literal content search stays inside the root

- **GIVEN** an authorized project containing text files and a directory symlink
  to an external tree
- **WHEN** `file_search` searches for a literal string from the project root
- **THEN** matching project files are returned with relative paths and line data
- **AND** the external tree is not traversed

#### Scenario: Search stops at configured ceilings

- **GIVEN** more matching files than the requested result ceiling
- **WHEN** `file_search` reaches the ceiling
- **THEN** it stops enumerating further content
- **AND** the result reports that it was truncated

### Requirement: Batch file reads validate before content access

The system SHALL provide a `file_read_many` tool that accepts a bounded list of
paths plus per-file and total output ceilings. It SHALL resolve and authorize
the complete path list before reading file content. If any member is malformed,
missing, denied, or outside the batch limits, the tool SHALL return a failure
without content from another member.

#### Scenario: Denied member makes batch atomic

- **GIVEN** a batch contains one authorized file and one denied file
- **WHEN** `file_read_many` validates the batch
- **THEN** the outcome is `access_denied`
- **AND** no content from the authorized file is returned
- **AND** no file activity is recorded

#### Scenario: Authorized batch returns bounded sections

- **GIVEN** a batch of authorized text files within count limits
- **WHEN** `file_read_many` reads them
- **THEN** the result contains one labeled bounded section per file
- **AND** total output does not exceed the declared ceiling

### Requirement: JSON projection uses bounded data semantics

The system SHALL provide a `json_read` tool that reads one authorized JSON file
and projects a bounded list of RFC 6901 JSON Pointers. It SHALL parse JSON with
the repository's System.Text.Json configuration, reject duplicate or invalid
pointers, and bound input bytes, pointer count, and output characters. It SHALL
NOT accept executable query languages.

#### Scenario: Selected JSON properties returned without shell

- **GIVEN** an authorized JSON document
- **WHEN** `json_read` receives pointers `/status` and `/items/0/name`
- **THEN** it returns the selected values with their pointers
- **AND** the outcome is `success`

#### Scenario: Invalid pointer fails before partial projection

- **GIVEN** one valid pointer and one malformed pointer
- **WHEN** `json_read` validates the request
- **THEN** the outcome is `invalid_input`
- **AND** no selected value is returned

### Requirement: File inspection exposes bounded image metadata

When `file_read` inspects a supported image, its metadata result SHALL include
canonical MIME type, byte length, pixel width, and pixel height without decoding
the full image into an unbounded bitmap. Malformed or unsupported image metadata
SHALL fail closed without returning raw binary content.

#### Scenario: PNG dimensions are returned

- **GIVEN** an authorized valid PNG file
- **WHEN** `file_read` inspects it
- **THEN** the result includes `image/png`, byte length, width, and height
- **AND** the agent does not need shell or Python to obtain dimensions

### Requirement: Conditional tool schemas expose valid branches

A first-party tool with mutually exclusive execution modes SHALL publish a JSON
Schema `oneOf` whose branches require the fields for that mode and reject
fields belonging only to another mode. Native argument validation SHALL reject
zero or multiple matching branches before the tool executes.

#### Scenario: Reminder mode requires its delivery fields

- **GIVEN** a reminder tool has delivery modes with different required fields
- **WHEN** its schema is generated
- **THEN** each mode is a separate `oneOf` branch
- **AND** a call missing that mode's required field is rejected before dispatch

#### Scenario: Single-shape tool remains compatible

- **GIVEN** `file_list` has one argument shape
- **WHEN** its schema is generated
- **THEN** its existing object schema and accepted calls remain unchanged

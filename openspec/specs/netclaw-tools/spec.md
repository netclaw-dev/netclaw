# netclaw-tools Specification

## Purpose

Define Netclaw's first-party and integrated tool execution behavior, including
authorization, approval, and filesystem tooling.

Use the [Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for tool call, dispatcher, tool result, tool receipt, outcome category, application error, and tool-declared error.

## Requirements

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

### Requirement: MCP tool outcomes are machine-actionable

An MCP tool call that ends in an exception SHALL produce a tool receipt under the same rules as the requirement "First-party tool outcomes are machine-actionable". The category SHALL follow the failure kind: an HTTP 401 or 403 is `access_denied`, an HTTP 404 is `not_found`, and every other exception is `transient_failure`. The tool result SHALL stay a factual error string that names the tool. A tool-declared error is not an exception and SHALL keep its current result path. The receipt SHALL NOT grant authority, retry the call, or replay it.

#### Scenario: HTTP 500 becomes a transient failure receipt

- **GIVEN** an MCP tool call that the server answers with HTTP 500
- **WHEN** the adapter returns the tool result
- **THEN** the outcome category is `transient_failure`
- **AND** the tool result names the tool and the HTTP status
- **AND** the receipt records no file activity

#### Scenario: HTTP 403 becomes an access-denied receipt

- **GIVEN** an MCP tool call that the server answers with HTTP 403
- **WHEN** the adapter returns the tool result
- **THEN** the outcome category is `access_denied`
- **AND** no authority changes

#### Scenario: Tool-declared error keeps the result path

- **GIVEN** an MCP tool call that the server answers with HTTP 200 and a tool-declared error, for example `{"content":[{"type":"text","text":"Internal Server Error"}],"isError":true}`
- **WHEN** the adapter returns the tool result
- **THEN** the tool result carries the error text the tool declared
- **AND** no exception outcome is produced
- **AND** no reconnect occurs

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

#### Scenario: Successful batch read records canonical files

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

### Requirement: Recursive workspace search is bounded and structured

The system SHALL provide a `file_search` tool for recursive literal file-name
and text search under one authorized root. The tool SHALL accept explicit
result, file, and content-byte ceilings. It SHALL NOT follow directory symlinks.
It SHALL report matches, skipped entries, and truncation state. Search SHALL use
filesystem APIs instead of an external executable.

#### Scenario: Literal content search stays inside the root

- **GIVEN** an authorized project has text files and an external directory link
- **WHEN** `file_search` searches for a literal string from the project root
- **THEN** matching project files are returned with relative paths and line data
- **AND** the external tree is not traversed

#### Scenario: Search stops at configured ceilings

- **GIVEN** more matching files than the requested result ceiling
- **WHEN** `file_search` reaches the ceiling
- **THEN** it stops further content enumeration
- **AND** the result reports that it was truncated

### Requirement: File inspection exposes bounded image metadata

When `file_read` inspects a supported image, its metadata result SHALL include
canonical MIME type, byte length, pixel width, and pixel height. It SHALL NOT
decode the full image into an unbounded bitmap. Malformed or unsupported image
metadata SHALL fail closed without returning raw binary content.

#### Scenario: PNG dimensions are returned

- **GIVEN** an authorized valid PNG file
- **WHEN** `file_read` inspects it
- **THEN** the result includes `image/png`, byte length, width, and height
- **AND** the agent does not need shell or Python to obtain dimensions

### Requirement: Conditional tool schemas expose valid branches

A first-party tool with mutually exclusive modes SHALL publish a JSON Schema
`oneOf`. Each branch SHALL require its mode fields and reject fields that belong
only to another mode. Native argument validation SHALL reject zero or multiple
matching branches before tool execution.

#### Scenario: Reminder mode requires its delivery fields

- **GIVEN** a reminder tool has delivery modes with different required fields
- **WHEN** its schema is generated
- **THEN** each mode is a separate `oneOf` branch
- **AND** a call without required mode fields is rejected before dispatch

#### Scenario: Single-shape tool remains compatible

- **GIVEN** `file_list` has one argument shape
- **WHEN** its schema is generated
- **THEN** its existing object schema and accepted calls remain unchanged

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout: `Session.ToolExecutionTimeoutSeconds`
(default: 90 seconds), or the agent's per-call timeout hint when present. The tool
SHALL drain stdout and stderr in bounded memory (each to the capture ceiling
`ToolConfig.MaxOutputChars`) and return the combined output bounded to the
ceiling — it does NOT itself window, redact, or spill (the central
`bounded-tool-output` mechanism does, after redaction). `shell_execute` SHALL
declare a small verbose inline budget (`InlineOutputBudgetChars`) so its skimmable
output is bounded aggressively. Authorization, including hard deny and the
launch re-check, SHALL complete before the process starts (`tool-authorization`
TA-5 and TA-14).

#### Scenario: Execute command and return output

- **GIVEN** the audience and shell mode admit `shell_execute` for the session
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with `netclaw daemon stop`
- **WHEN** authorization evaluates the command
- **THEN** the tool result is `Tool access denied: hard_deny_self_destructive`
- **AND** the shell process is never started

#### Scenario: Execution timeout enforced

- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 90 seconds)
- **THEN** the process is terminated
- **AND** the tool returns a timeout error message to the LLM

#### Scenario: Combined output bounded by the capture ceiling

- **GIVEN** a shell command writes large output to both stdout and stderr
- **WHEN** the output is captured
- **THEN** the returned combined output is bounded by `MaxOutputChars` (one shared
  ceiling, not a per-stream cap)
- **AND** the dispatcher applies the inline budget + spill + steer on top
  (per `bounded-tool-output`)

#### Scenario: Stdin closed prevents interactive commands

- **GIVEN** the agent invokes the shell tool with a command
- **WHEN** the process is created
- **THEN** stdin is closed immediately
- **AND** commands that require interactive input fail promptly

#### Scenario: Working directory set to project path

- **GIVEN** the session is associated with a registered project
- **WHEN** the shell tool executes a command
- **THEN** the working directory is set to the project's registered path

### Requirement: Directory enumeration tool

The system SHALL provide a `file_list` first-party tool that returns a
single-level listing of a directory's entries, each entry identified by name
and type. It SHALL be read-only and SHALL NOT create, modify, or remove a
filesystem entry.

`file_list` SHALL be gated by the audience profile `AllowedTools` allowlist.
It SHALL authorize its target through the shared `Read` path access decision.
An explicit `Roots` or `None` read profile SHALL remain authoritative in every
interaction mode. User approval SHALL NOT widen that file profile.

#### Scenario: Team session lists a directory within its read roots

- **GIVEN** a session resolved to the `Team` audience with `file_list` granted
- **WHEN** the agent invokes `file_list` on its session directory
- **THEN** the tool returns the directory's entries with name and type
- **AND** no filesystem entry is created, modified, or removed

#### Scenario: Public session cannot list outside its session directory

- **GIVEN** a session resolved to the `Public` audience
- **WHEN** the agent invokes `file_list` outside its permitted read roots
- **THEN** the invocation is denied
- **AND** the denial message does not disclose configured root paths

#### Scenario: Counterexample - approval cannot widen explicit read roots

- **GIVEN** an interactive Personal profile explicitly limits reads to one root
- **WHEN** the agent invokes `file_list` outside that root
- **THEN** file protection denies the invocation
- **AND** the invocation does not reach user approval

#### Scenario: file_list denied when not granted to the audience

- **GIVEN** an audience profile whose `AllowedTools` omits `file_list`
- **WHEN** the agent invokes `file_list`
- **THEN** the invocation is denied with reason
  `tool_not_allowed_for_audience_profile`

### Requirement: File tools use typed MIME values

File-related tool side channels SHALL carry typed MIME values rather than raw
strings. Tool registrations for file attachments and model-input files SHALL
store canonical MIME values from the shared media catalog while preserving the
existing string wire format when serialized or displayed.

#### Scenario: Model input file carries canonical MIME

- **GIVEN** a tool registers a model-input file with MIME alias `image/jpg`
- **WHEN** the tool execution context records the file
- **THEN** the stored MIME value is canonical `image/jpeg`

#### Scenario: File attachment display preserves MIME string shape

- **GIVEN** a tool registers a file attachment with a typed MIME value
- **WHEN** the attachment is emitted as user-visible output
- **THEN** the MIME is displayed as the canonical MIME string

### Requirement: Model-input media eligibility is catalog-backed

The tool execution pipeline SHALL decide whether a file can be attached to the
next model request using the shared media catalog's model-input eligibility and
the active model's input modalities. The pipeline SHALL NOT rely on ad hoc MIME
prefix checks.

#### Scenario: Supported image is attached only for image-capable model

- **GIVEN** a PNG file has verified MIME `image/png`
- **AND** the active model supports image input
- **WHEN** the tool execution pipeline materializes model-input files
- **THEN** the image is copied into session media and attached to the next model
  request

#### Scenario: Unsupported media is skipped before provider serialization

- **GIVEN** a model-input file has MIME `audio/mpeg`
- **WHEN** the tool execution pipeline materializes model-input files
- **THEN** the file is not attached as model input
- **AND** the provider does not receive non-image `DataContent` through the
  image-only OpenAI-compatible path

### Requirement: Web fetch MIME decisions use shared media catalog

The `web_fetch` tool SHALL use the shared media catalog for content-type
normalization, binary/text classification, and fallback extension selection.
It SHALL NOT maintain a separate MIME-to-extension table for types already
present in the media catalog.

#### Scenario: Binary fetch extension comes from catalog

- **GIVEN** an HTTP response with content type `application/pdf`
- **AND** the URL path does not include a usable extension
- **WHEN** `web_fetch` saves the response
- **THEN** it chooses `.pdf` from the media catalog

### Requirement: Web fetch format is validated

The `web_fetch` tool SHALL validate the `Format` argument against the supported
set (absent, `"raw"`, `"text"`). Any other value SHALL reject the call with a
tool-result error naming the supplied value and the supported set. The tool
SHALL NOT silently fall back to raw mode for an unsupported format value.

#### Scenario: Unsupported format value rejects

- **GIVEN** a `web_fetch` call with `"Format": "markdown"`
- **WHEN** arguments are validated
- **THEN** the call is rejected with an error naming `"markdown"` and the
  supported values `raw` and `text`
- **AND** no HTTP request is made

#### Scenario: Supported formats behave unchanged

- **GIVEN** a `web_fetch` call with `"Format": "text"` (or `Format` absent)
- **WHEN** the fetch executes
- **THEN** behavior is identical to current behavior

### Requirement: Web fetch response-cap truncation is surfaced

The `web_fetch` result SHALL include a notice stating the content was
truncated at the cap whenever a fetched response body reaches the
response-byte cap. The captured byte count alone SHALL NOT be the only signal.

#### Scenario: Body larger than the cap carries a truncation notice

- **GIVEN** a URL whose response body exceeds the 5 MB response cap
- **WHEN** `web_fetch` returns its summary
- **THEN** the result includes a notice that content was truncated at 5 MB

#### Scenario: Body under the cap carries no truncation notice

- **GIVEN** a URL whose response body is under the response cap
- **WHEN** `web_fetch` returns its summary
- **THEN** no truncation notice is present

### Requirement: Webhook listing honors its filter argument

The `list_webhooks` tool SHALL honor its schema-advertised `Filter` argument:
`"active"` (the default) SHALL return only enabled webhooks, `"all"` SHALL
return every webhook, and any other value SHALL reject the call naming the
supported values. The applied filter SHALL be echoed in the result.

#### Scenario: Active filter excludes disabled webhooks

- **GIVEN** two registered webhooks, one enabled and one disabled
- **AND** a `list_webhooks` call with `"Filter": "active"` (or `Filter` absent)
- **WHEN** the tool executes
- **THEN** only the enabled webhook is listed
- **AND** the result states the `active` filter was applied

#### Scenario: All filter includes disabled webhooks

- **GIVEN** two registered webhooks, one enabled and one disabled
- **AND** a `list_webhooks` call with `"Filter": "all"`
- **WHEN** the tool executes
- **THEN** both webhooks are listed with their enabled state
- **AND** the result states the `all` filter was applied

#### Scenario: Unknown filter value rejects

- **GIVEN** a `list_webhooks` call with `"Filter": "enabled"`
- **WHEN** arguments are validated
- **THEN** the call is rejected naming the supported values `active` and `all`

### Requirement: File read tool

The system SHALL provide a `file_read` first-party tool that authorizes the
requested path through the shared `Read` path access decision before inspecting
or reading bytes. An explicit `Roots` or `None` read profile SHALL remain
authoritative in every interaction mode. The default interactive Personal
`All` profile MAY read outside configured roots because the file profile itself
grants that authority, not because shell or approval policy widens it.

Text-like files SHALL return decoded text for UTF-8,
UTF-16/UTF-32
Unicode, and common Windows-1252 text files using the existing offset/limit and
output-truncation behavior.

For non-text files, `file_read` SHALL NOT return raw binary content. It SHALL
detect the file category using the canonical attachment taxonomy where possible
and return structured metadata plus an explicit next-step message.

Images SHALL be eligible for model-visible handoff only when the active model's
input modalities include image support. The handoff SHALL use session media
references and the existing `DataContent` rehydration path, not binary content in
the tool-result string. Streaming tool-result persistence SHALL retain the media
references needed to recreate the handoff nudge during recovery.

PDF extraction, OCR, audio transcription, and video keyframe extraction SHALL NOT
be built into `file_read`.

#### Scenario: Text file read preserves existing behavior

- **GIVEN** a readable text file using UTF-8, UTF-16/UTF-32 Unicode, or Windows-1252
- **WHEN** the agent invokes `file_read` with optional offset and limit values
- **THEN** the tool returns text content with the existing line pagination and
  truncation behavior

#### Scenario: Counterexample - approval cannot widen explicit read policy

- **GIVEN** an interactive Personal profile explicitly denies or limits reads
- **WHEN** the agent invokes `file_read` for a path outside that authority
- **THEN** file protection denies the invocation
- **AND** no shell setting or approval mode widens the read policy

#### Scenario: Image read on image-capable model becomes model-visible

- **GIVEN** a readable PNG file
- **AND** the active model supports image input
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata indicating the image was loaded for visual
  inspection
- **AND** the next LLM call includes the image through a session media reference

#### Scenario: Sub-agent image read can become model-visible

- **GIVEN** a sub-agent uses `file_read` on a readable PNG file
- **AND** the sub-agent's selected model supports image input
- **WHEN** the tool result is returned to the sub-agent loop
- **THEN** the next sub-agent LLM call includes the image through a session media
  reference

#### Scenario: Image read on text-only model returns modality guidance

- **GIVEN** a readable PNG file
- **AND** the active model does not support image input
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata and the canonical image modality-gap note
- **AND** no media reference is added to the next LLM call

#### Scenario: PDF read does not extract text

- **GIVEN** a readable PDF file
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata identifying the file as a PDF
- **AND** the result says native PDF extraction is not built into `file_read`
- **AND** no raw PDF bytes are returned

#### Scenario: Unsupported binary read returns explicit guidance

- **GIVEN** a readable archive, audio file, video file, binary document, or
  unknown binary file
- **WHEN** the agent invokes `file_read`
- **THEN** the tool returns metadata and explicit unsupported-format guidance
- **AND** no raw bytes are returned

### Requirement: Attachment tool reach

All audiences SHALL apply the `ToolPathPolicy` read-deny check to attachment sources.

The system SHALL provide an `attach_file` first-party tool that sends a file to
the user. It SHALL authorize the source with the shared `Attach` path access
decision. An explicit `Roots` or `None` attach profile SHALL remain authoritative
in every interaction mode. The default interactive Personal `All` profile MAY
attach an external file after the protected-path checks pass. The tool SHALL
copy an admitted file into the current session's attachments directory before
delivery.

#### Scenario: Interactive Personal session attaches an external file

- **GIVEN** the default interactive Personal attach profile permits an external
  file
- **WHEN** the agent invokes `attach_file` for that file
- **THEN** the tool copies the file into the current session attachments directory
- **AND** the tool sends the copied file to the user

#### Scenario: Counterexample - explicit attach roots remain authoritative

- **GIVEN** an interactive Personal profile explicitly limits attachments to
  one root
- **WHEN** the agent invokes `attach_file` outside that root
- **THEN** file protection denies the invocation
- **AND** user approval does not widen the attach profile

#### Scenario: Protected control-plane file cannot be attached

- **GIVEN** an interactive Personal session requests a protected control-plane
  file
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool denies the request
- **AND** broad Personal file authority does not bypass the denial

`PathAccessPolicy` SHALL validate each tool-managed destination before directory creation or file copy.
The destination SHALL remain inside the current session workspace without links across the workspace or its known storage ancestors.
The destination SHALL pass the protected-path write check, including collision-suffix candidates.
Source attach permission authorizes this bounded copy; the copy SHALL NOT require general `WriteFiles` permission.
These checks are call-local. They do not form an operating-system sandbox against concurrent filesystem changes.

#### Scenario: Counterexample - attachment destination redirects a copy

- **GIVEN** an admitted source outside the current workspace
- **AND** the attachments directory or its session ancestor is a link to another directory
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool returns `AccessDenied` before directory creation or file copy
- **AND** the other directory remains unchanged and the tool emits no attachment

#### Scenario: Counterexample - attachment destination is write protected

- **GIVEN** an admitted source and a write-protected destination
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool returns `AccessDenied` without a destination file or new directory

#### Scenario: Attach-only profile preserves the bounded copy

- **GIVEN** an attach profile admits a source and the write profile is `None`
- **AND** the current workspace destination passes containment, link, and protected-path checks
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool copies the source and emits the attachment
- **AND** an existing destination file retains its bytes through the existing suffix rule

### Requirement: File read tool bounds its read for memory safety

The `file_read` tool's default (no `offset`/`limit`) path SHALL read a bounded
head of the file (up to `ToolConfig.MaxOutputChars`) and stop — it SHALL NOT read
the entire file into memory before truncating. The existing line-range
(`offset`/`limit`) path SHALL remain bounded. `file_read` SHALL NOT redact its
result itself; the central `DispatchingToolExecutor` redaction covers it. The
inline bound + spill (if any) is applied centrally per `bounded-tool-output`;
`file_read` is a content tool and uses the session content budget.

#### Scenario: Large file is read in bounded memory

- **WHEN** the agent reads a file larger than the capture ceiling with no
  `offset`/`limit`
- **THEN** the tool reads only a bounded head and does not materialize the whole
  file in memory
- **AND** it appends a steer to read a specific range (`offset`/`limit`) or `grep`

#### Scenario: Secrets in a read file are redacted by the dispatcher

- **GIVEN** a file contains a secret-bearing value (e.g. an API key)
- **WHEN** the agent reads the file
- **THEN** the result returned to the model has the secret redacted (by the
  central dispatcher redaction)

### Requirement: Tool invocation requires an admitted run scope

Every first-party tool invocation SHALL receive a non-null immutable invocation context created from an immutable run scope after audience admission. The runtime SHALL NOT expose a context-free production execution overload, an empty production execution context, or nullable authority dependencies. Mutable tool outputs SHALL be written through a separate per-invocation append-only sink, and approval attempt state SHALL remain outside the tool-visible context. Each invocation SHALL receive fresh output and approval state even when calls share one run scope.

#### Scenario: Parallel calls do not share call-local state

- **GIVEN** two tool calls in the same admitted turn
- **WHEN** the calls execute concurrently
- **THEN** both calls share the same immutable run authority
- **AND** outputs or approval mutations from one call are not visible to the other call

#### Scenario: Missing authority cannot reach dispatch

- **GIVEN** a caller has not constructed an admitted run scope
- **WHEN** it attempts to invoke a first-party tool
- **THEN** no context-free API permits dispatch
- **AND** the tool does not execute under default authority

### Requirement: Execution limits use validated semantic values

Timeouts, inline output budgets, and other scalar execution limits crossing the tool pipeline SHALL use validated semantic value objects. These value objects SHALL require explicit primitive access and SHALL NOT define implicit conversions to or from primitive types.

#### Scenario: Invalid limit is rejected at construction

- **GIVEN** an execution limit outside its permitted range
- **WHEN** the run scope or tool metadata is constructed
- **THEN** construction returns a validation failure before tool dispatch
- **AND** no default primitive value is substituted

### Requirement: Tool-enabled sessions require execution infrastructure

A tool-enabled session SHALL have authorization, approval, logging, and dispatch infrastructure available before accepting a tool batch. Infrastructure that production constructs unconditionally SHALL be a required dependency rather than a nullable feature switch. Interactive approval SHALL be represented as one required capability value: unavailable, or available with its required bridge. Tool-call and tool-result observability SHALL flow through the session's canonical `ToolCallOutput` and `ToolResultOutput` transcript path; the execution pipeline SHALL NOT require a parallel no-op audit sink.

#### Scenario: Security dependency is unavailable

- **GIVEN** required authorization or approval infrastructure cannot be constructed
- **WHEN** the session attempts to enable tools
- **THEN** session initialization or batch execution fails visibly
- **AND** the missing dependency does not disable its check

#### Scenario: Interactive approval cannot disagree with its bridge

- **GIVEN** a tool invocation has no admitted interactive approval bridge
- **WHEN** path and shell policies evaluate path access for an unattended run
- **THEN** the invocation is represented as non-interactive
- **AND** no nullable support flag can bypass those restrictions

#### Scenario: Production tool transcript has one owner

- **GIVEN** a production tool invocation is admitted and executed or denied
- **WHEN** the session publishes its tool-call and tool-result outputs
- **THEN** the existing session transcript path receives those outputs
- **AND** execution does not also depend on an always-discarded audit logger

### Requirement: Shell execution uses the canonical native host

The `shell_execute` tool SHALL start the executable from the canonical shell
environment. It SHALL pass the submitted command as one process argument after
the environment's fixed non-interactive arguments. It SHALL close stdin and
preserve the existing timeout, output, working-directory, and process-tree
termination behavior. Buffered and streaming execution SHALL use one shared
process-start builder. The tool schema SHALL remain unchanged.

#### Scenario: Bash command process arguments

- **GIVEN** the canonical environment uses `/bin/bash`
- **WHEN** `shell_execute` starts `git status`
- **THEN** the process arguments are `-c` and `git status`
- **AND** the tool does not invoke PowerShell or `cmd.exe`

#### Scenario: PowerShell command process arguments

- **GIVEN** the canonical environment uses a PowerShell executable
- **WHEN** `shell_execute` starts `Get-ChildItem`
- **THEN** the fixed arguments include `-NoLogo`, `-NoProfile`, and
  `-NonInteractive`
- **AND** `-Command` precedes one `Get-ChildItem` argument
- **AND** the tool does not invoke `cmd.exe`

#### Scenario: Missing selected executable fails visibly

- **GIVEN** the environment selected a PowerShell executable
- **AND** the process cannot start that executable
- **WHEN** `shell_execute` runs
- **THEN** the result identifies the required executable
- **AND** the tool does not run the command through another shell

#### Scenario: Buffered and streaming execution use the same host

- **GIVEN** one canonical environment and one submitted command
- **WHEN** buffered and streaming execution build their process start data
- **THEN** both use the same absolute executable path
- **AND** both use the same fixed arguments in the same order
- **AND** both append the submitted command as one argument

### Requirement: Generated fetch destinations

`PathAccessPolicy` SHALL check generated fetch paths before directory creation or file writes.
A bound session SHALL use its current workspace. A sessionless fetch SHALL use its configured fetch directory.
The check SHALL reject paths outside that directory, filesystem links, and protected write destinations.
A permitted fetch SHALL NOT require general file-write permission to save its response.
The tool SHALL return an explicit denial for an invalid destination and SHALL NOT create files or directories there.
These checks do not prevent another process from replacing a directory after validation.

#### Scenario: Fetch saves a response in an ordinary directory

- **GIVEN** a permitted fetch and an output directory without links or write protection
- **WHEN** the tool receives text or binary content
- **THEN** it creates the output directory if needed and saves the response there

#### Scenario: Fetch cannot write through a linked directory

- **GIVEN** a session workspace or sessionless fetch directory that is a link
- **WHEN** the tool receives a response
- **THEN** it returns `AccessDenied` without writing through that link

#### Scenario: Fetch cannot write protected output

- **GIVEN** an output directory that the protected-path policy denies for writes
- **WHEN** the tool receives a response
- **THEN** it returns `AccessDenied` without creating that directory or an output file

### Requirement: Attachment tool accepts an authorized source path directly

The parent-session model-visible `attach_file` definition SHALL tell the agent
to pass the existing authorized source path directly. The agent SHALL NOT need
to copy the file into managed temporary storage first.

Netclaw SHALL retain the existing audience, read-deny, proximity, and safe-copy
behavior. Subagents SHALL NOT receive this tool until an internal attachment
handoff can deliver child attachments to the parent invocation.

Example:

```text
interactive Personal parent model calls:
  attach_file(Path = "/workspace/project/report.pdf")

Netclaw:
  authorizes the source path
  copies it to the session attachment directory when required
  returns the attachment through the parent invocation

subagent:
  does not receive, find, load, or dispatch attach_file
  can report a saved path to the parent instead
```

Authority examples and counterexamples:

| Caller and source | Required result |
|---|---|
| Interactive Personal parent with an authorized project file | Attach it directly. Copy it into the session when required. |
| Parent with a protected credential path | Deny it. Core exposure does not bypass the read deny. |
| Team or non-interactive parent with a source outside the session tree | Deny it through the existing proximity rule. |
| Subagent with any source | Do not expose, find, load, or dispatch `attach_file`. |

#### Scenario: Interactive Personal agent attaches an existing project file directly

- **GIVEN** an interactive Personal parent session can attach an existing project file under current policy
- **WHEN** the model needs to send that file to the user
- **THEN** the initial tool set contains `attach_file`
- **AND** its definition accepts the source path directly
- **AND** Netclaw performs any required copy into the session attachments directory
- **AND** no shell copy is required

#### Scenario: Core exposure does not widen attachment reach

- **GIVEN** the path access decision denies an attachment source
- **WHEN** `attach_file` is present in the registered core
- **THEN** the model-visible set still filters the tool by audience policy
- **AND** the tool still rejects the denied source when invoked

### Requirement: Spawned child references are machine-actionable

A successful `spawn_agent` result SHALL return the child run identifier, an
exact child log path, and the exact child artifact directory. These paths SHALL
be below the current session envelope, so the parent can compose existing file
tools through the shared path access decision. A failed spawn SHALL NOT return
locations that appear usable.

The system SHALL resolve and create the child log target before it returns a
successful result. The log can be empty. An immediate authorized `file_read`
SHALL NOT fail because the log path is not ready.

The result shape SHALL be equivalent to:

```text
run_id: "run-7"
log_path: "/srv/netclaw/sessions/s-42/subagents/run-7/logs/session.log"
artifact_dir: "/srv/netclaw/sessions/s-42/subagents/run-7/artifacts"
```

#### Scenario: Example - successful spawn returns child references

- **WHEN** a parent successfully starts a child run
- **THEN** the tool result contains the child run identifier
- **AND** it contains the exact child log path and artifact directory
- **AND** both paths belong to that parent session

#### Scenario: Example - parent reads a child artifact with an existing tool

- **GIVEN** a successful spawn returned the child artifact directory
- **WHEN** the owning parent calls `file_read` or `attach_file` for a file below
  that directory
- **THEN** the shared path access decision evaluates the file operation
- **AND** no new artifact-reference reader is required

#### Scenario: Example - parent reads child logs with existing tools

- **GIVEN** a successful spawn returned the exact child log path
- **WHEN** the owning parent uses `file_read`, `file_search`, or `file_list`
- **THEN** the existing tool performs its normal bounded operation
- **AND** no special child-log tool is required

#### Scenario: Counterexample - read permission does not grant writes

- **GIVEN** the parent audience permits reads but not writes
- **WHEN** it calls `file_write` or `file_edit` for that log
- **THEN** the `Write` path access decision denies the mutation
- **AND** the trusted-root relationship does not change that result

#### Scenario: Counterexample - failed spawn has no usable child references

- **WHEN** the child run is not created
- **THEN** the tool result reports failure
- **AND** it contains no child log path or artifact directory

#### Scenario: Example - successful child log path is ready

- **WHEN** `spawn_agent` returns a successful child result
- **THEN** the returned log path identifies an existing file
- **AND** an authorized `file_read` can open it immediately

### Requirement: Git worktrees compose existing tools

The existing `[session]` context SHALL announce the exact `worktree_dir`.
Agents SHALL create Git worktrees by calling `shell_execute` with a destination
below that directory. Normal shell authorization SHALL decide the command.
After Git succeeds, the agent SHALL use the existing
`set_working_directory` tool to adopt the created worktree as project scope.
The shared path access decision and normal shell authorization SHALL decide
the destination. The operation SHALL NOT use a separate worktree permission.

The system SHALL NOT add `worktree_create`, a worktree-specific authorization
model, or a worktree ownership record. It SHALL NOT parse private Git option
grammar to infer authority. Automatic cleanup remains out of scope.

#### Scenario: Example - current project gets a managed worktree

- **GIVEN** the current project is an authorized Git repository
- **AND** session context provides
  `worktree_dir=/srv/netclaw/sessions/s-42/worktrees`
- **WHEN** the agent runs `git worktree add` through `shell_execute` with a
  destination below `worktree_dir`
- **AND** Git succeeds
- **THEN** the agent can pass that destination to `set_working_directory`
- **AND** existing project-scope behavior loads project instructions

#### Scenario: Counterexample - external destination gets no special authority

- **WHEN** an agent authors `git worktree add /tmp/fix-branch branch-name`
- **THEN** normal shell policy evaluates the authored command
- **AND** `worktree_dir` guidance does not rewrite or auto-approve it

#### Scenario: Counterexample - unauthorized source repository is denied

- **GIVEN** a requested source repository is outside current authority
- **WHEN** the agent submits the Git command through `shell_execute`
- **THEN** authorization denies the operation
- **AND** no worktree-specific tool bypasses that decision

#### Scenario: Counterexample - failed worktree does not change project scope

- **WHEN** worktree creation fails or is denied
- **THEN** the project scope remains unchanged
- **AND** the agent does not call `set_working_directory` for a failed result

#### Scenario: Counterexample - no custom worktree tool is exposed

- **WHEN** the dynamic tool catalog is assembled
- **THEN** it contains the existing shell and working-directory tools
- **AND** it does not contain `worktree_create`

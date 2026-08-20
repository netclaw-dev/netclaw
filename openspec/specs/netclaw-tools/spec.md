# netclaw-tools Specification

## Purpose

Define Netclaw's first-party and integrated tool execution behavior, including
authorization, approval, and filesystem tooling.
## Requirements

### Requirement: First-party tool outcomes are machine-actionable

First-party workspace tool execution SHALL produce exactly one call-local
outcome category: `success`, `invalid_input`, `access_denied`, `not_found`,
`transient_failure`, or `recoverable_correction`. The category SHALL be separate
from the model-facing string. The system SHALL NOT infer it from that string.
The outcome MAY carry a bounded remediation code and canonical file activity.
It SHALL NOT change the public string-returning `INetclawTool` contract.

#### Scenario: Access denial has no successful file activity

- **GIVEN** `file_read` is called for a path outside the current read authority
- **WHEN** scoped access denies the call
- **THEN** the outcome category is `access_denied`
- **AND** the outcome contains no successful file activity
- **AND** the model receives a bounded denial string

#### Scenario: Recoverable correction stays distinct from failure

- **GIVEN** a workspace tool can continue after the project directory is declared
- **WHEN** the missing declaration is the only blocker
- **THEN** the outcome category is `recoverable_correction`
- **AND** its remediation code identifies `set_working_directory`
- **AND** no authority is granted by the outcome itself

### Requirement: Working context records successful file activity only

`WorkingContext.RecentFiles` SHALL update only from canonical file activity in a
successful tool outcome. Failed, denied, missing, malformed, or corrective tool
results SHALL NOT update recent files. The session pipeline SHALL NOT infer file
activity only from authored argument names.

#### Scenario: Failed write does not become recent

- **GIVEN** `file_write` targets a denied path
- **WHEN** the tool returns an access-denied outcome
- **THEN** the authored path is absent from `RecentFiles`

#### Scenario: Successful batch read records canonical files

- **GIVEN** `file_read_many` successfully reads two authorized relative paths
- **WHEN** the session applies the tool receipt
- **THEN** both canonical resolved paths are added to `RecentFiles`
- **AND** no authored relative spelling becomes a separate file

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

### Requirement: Batch file reads validate before content access

The system SHALL provide a `file_read_many` tool that accepts a bounded path
list plus per-file and total output ceilings. It SHALL authorize the complete
path list before it reads content. If one member is malformed, missing, denied,
or outside the batch limits, the tool SHALL return no content from another
member.

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
and projects a bounded list of RFC 6901 JSON Pointers. It SHALL reject duplicate
or invalid pointers. It SHALL bound input bytes, pointer count, and output
characters. It SHALL NOT accept executable query languages.

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
### Requirement: Policy-gated tool invocation

The system SHALL check ACL grants and approval policy before every tool
execution. Tool invocations SHALL be logged with audit records including tool
name, invoking session, timestamp, allow/deny/approval result, and approval
decision details when applicable. The `ToolAccessDecision` SHALL support three
outcomes: `Allow`, `Deny(reason)`, and `RequiresApproval(context)`.

When `RequiresApproval` is returned, the tool execution pipeline SHALL pause
the individual tool task and emit a `ToolInteractionRequest` to session
subscribers. The pipeline SHALL NOT block other tool calls in the same batch.

#### Scenario: Granted tool executes successfully

- **GIVEN** the session has an ACL grant for `web_search`
- **AND** `web_search` is in Auto approval mode
- **WHEN** the LLM requests a web search tool call
- **THEN** the ACL check passes
- **AND** the tool executes
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  `allow` result

#### Scenario: Ungrantable tool denied at invocation

- **GIVEN** the session does not have an ACL grant for `shell`
- **WHEN** the LLM requests a shell tool call
- **THEN** the ACL check fails
- **AND** the tool is not executed
- **AND** a policy denial with reason code is returned to the LLM
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  `deny` result

#### Scenario: Tool requires approval and is approved

- **GIVEN** the session has an ACL grant for `shell`
- **AND** `shell_execute` is in Approval mode for the session's audience
- **AND** the command pattern is not already approved in `IToolApprovalService`
- **WHEN** the LLM requests a shell tool call
- **THEN** `ToolAccessPolicy` returns `RequiresApproval`
- **AND** `DispatchingToolExecutor` consults `IToolApprovalService`
- **AND** the pipeline emits a `ToolInteractionRequest` and pauses the task
- **AND** when the user approves, the tool executes
- **AND** an audit record is logged with `approved` result

#### Scenario: Tool requires approval and is denied by user

- **GIVEN** the pipeline has emitted an approval prompt
- **WHEN** the user denies
- **THEN** the tool result is "Command denied by user"
- **AND** an audit record is logged with `denied_by_user` result

#### Scenario: Audit records available in diagnostics

- **GIVEN** tool invocations have occurred
- **WHEN** the operator views diagnostics
- **THEN** audit records show tool name, invoking session, timestamp, and
  allow/deny/approval result for each invocation

### Requirement: Shell execution tool

The system SHALL provide a shell execution tool that runs commands as the
Netclaw process user context. Stdin SHALL be closed (no interactive commands).
Execution SHALL enforce a configurable timeout (default: 60 seconds). The tool
SHALL drain stdout and stderr in bounded memory (each to the capture ceiling
`ToolConfig.MaxOutputChars`) and return the combined output bounded to the
ceiling — it does NOT itself window, redact, or spill (the central
`bounded-tool-output` mechanism does, after redaction). `shell_execute` SHALL
declare a small verbose inline budget (`InlineOutputBudgetChars`) so its skimmable
output is bounded aggressively. Before execution, the shell tool SHALL check the
hard deny list via `ShellCommandPolicy`; hard-denied commands SHALL be rejected
before `ToolPathPolicy` path checks.

#### Scenario: Execute command and return output

- **GIVEN** the `shell` grant is available for the session
- **WHEN** the agent invokes the shell tool with a command
- **THEN** the command is executed as the Netclaw process user
- **AND** stdout and stderr are captured
- **AND** the combined output is returned to the LLM

#### Scenario: Hard-denied command rejected before execution

- **GIVEN** the agent invokes `shell_execute` with `netclaw daemon stop`
- **WHEN** `ShellCommandPolicy` evaluates the command
- **THEN** the command is rejected with "Command blocked by hard deny policy"
- **AND** the shell process is never started

#### Scenario: Execution timeout enforced

- **GIVEN** a shell command is running
- **WHEN** the command exceeds the configured timeout (default: 60 seconds)
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

### Requirement: Tool execution context carries a parsed audience

`ToolExecutionContext` SHALL represent the execution audience as a parsed
`TrustAudience`, not as an unvalidated wire string. The audience SHALL be
parsed when the context is built, so an unparseable value fails at construction
rather than at a later tool authorization check. Tool authorization SHALL read
the parsed audience directly and SHALL NOT re-parse a string or apply a
parse-failure fallback to `Public`.

#### Scenario: Context built with an unparseable audience fails loud

- **WHEN** a `ToolExecutionContext` is built from an audience value that cannot
  be parsed
- **THEN** construction throws an explicit parse error
- **AND** the failure occurs before any tool runs

#### Scenario: Tool authorization reads the parsed audience

- **GIVEN** a `ToolExecutionContext` carrying a parsed `TrustAudience`
- **WHEN** `ToolAccessPolicy` evaluates a tool invocation
- **THEN** it reads the audience as a typed value
- **AND** it performs no string parsing and applies no `Public` parse-failure
  fallback

### Requirement: Directory enumeration tool

The system SHALL provide a `file_list` first-party tool that returns a
single-level listing of a directory's entries, each entry identified by name
and type (file or directory). `file_list` SHALL be read-only and SHALL NOT
create, modify, or remove any filesystem entry.

`file_list` SHALL be a profile-managed tool gated by the audience profile
`AllowedTools` allowlist. Its target directory SHALL be authorized through the
same scoped read-access policy used by `file_read`, so the directories an
audience may list are exactly that audience's resolved read roots. A target
outside the audience's read roots SHALL be denied, and the denial message
SHALL NOT disclose configured root paths. Interactive Personal-audience
sessions are the exception: they get shell-equivalent reach, so a target
outside the read roots SHALL resolve when the session is interactive and the
audience is Personal. Autonomous sessions keep the hard denial.

#### Scenario: Team session lists a directory within its read roots

- **GIVEN** a session resolved to the `Team` audience with `file_list` granted
- **WHEN** the agent invokes `file_list` on its session directory
- **THEN** the tool returns the directory's entries with name and type
- **AND** no filesystem entry is created, modified, or removed

#### Scenario: Public session cannot list outside its session directory

- **GIVEN** a session resolved to the `Public` audience
- **WHEN** the agent invokes `file_list` on a path outside the session
  directory
- **THEN** the invocation is denied
- **AND** the denial message does not disclose configured root paths

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
requested path through the audience-scoped read-file policy before inspecting or
reading bytes. Interactive Personal-audience sessions are the exception: they
get shell-equivalent reach, so a path outside the read roots SHALL resolve when
the session is interactive and the audience is Personal. Autonomous sessions
keep the hard denial. Text-like files SHALL return decoded text for UTF-8,
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

The system SHALL provide an `attach_file` first-party tool that sends a file to
the user. Non-interactive, Team, and Public sessions SHALL only attach files
inside the current session directory or a sibling Netclaw session directory.
Interactive Personal-audience sessions get shell-equivalent reach: any path that
resolves through the read-access policy SHALL be attachable, and the file SHALL
be copied into the current session's attachments directory before delivery.

All audiences SHALL apply the `ToolPathPolicy` read-deny surface to attached
files: a path that `IsReadDenied` (credentials, keys, secrets, control-plane
state, or the shell indicator list) SHALL NOT be attachable, even when the
proximity restriction is lifted.

#### Scenario: Interactive Personal session attaches an external file

- **GIVEN** an interactive Personal session can read a file outside its session
  directory
- **WHEN** the agent invokes `attach_file` for that file
- **THEN** the tool copies the file into the current session attachments directory
- **AND** the tool sends the copied file to the user

#### Scenario: Protected control-plane file cannot be attached

- **GIVEN** an interactive Personal session requests a file that
  `ToolPathPolicy.IsReadDenied` protects
- **WHEN** the agent invokes `attach_file`
- **THEN** the tool denies the request
- **AND** the shell-equivalent read reach does not bypass the denial

### Requirement: Working directory declaration stays scoped

The system SHALL provide a `set_working_directory` first-party tool that sets
the session's project root. Its target SHALL be resolved through the read-access
policy WITHOUT interactive Personal shell-equivalent reach: the working
directory widens the shell safe-verb auto-approve zone and loads project
identity files into the system prompt, so it SHALL be clamped to the autonomous
zone (session directory, project directory, and global read roots) in every
audience and mode.

#### Scenario: Interactive Personal session cannot widen the working directory

- **GIVEN** an interactive Personal session requests a directory outside the
  autonomous zone
- **WHEN** the agent invokes `set_working_directory`
- **THEN** the project directory remains unchanged
- **AND** the tool reports that the directory is outside the allowed roots

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
- **WHEN** path and shell policies evaluate autonomous trust-zone restrictions
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

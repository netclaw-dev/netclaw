This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## MODIFIED Requirements

### Requirement: Policy-gated tool invocation

The system SHALL check ACL grants and approval policy before every tool
execution. Tool invocations SHALL be logged with audit records including tool
name, invoking session, timestamp, authorization result, and approval decision
details when applicable.

Authorization SHALL compose these layers in order:

1. tool exposure and audience capability;
2. tool-family safety, including shell mode and shell command policy;
3. file protection when the invocation addresses filesystem paths;
4. stored or one-time approval authority;
5. user approval when still required; and
6. execution.

A terminal denial SHALL stop evaluation. A later layer SHALL NOT widen an
earlier denial. File authority MAY deny an otherwise eligible shell invocation,
but it SHALL NOT grant shell capability or bypass shell command policy.

`ToolAuthorizationDecision` SHALL represent one of four outcomes:

- `Allowed`, with the rule that grants execution;
- `Denied`, with a stable reason and optional human-readable detail;
- `RequiresApproval`, with the context needed to ask the user; or
- `RequiresAgentCorrection`, with a typed correction that grants no authority.

A `RequiresApproval` decision MAY also carry a typed correction. The execution
pipeline SHALL first check existing stored or one-time authority. It SHALL
present the correction only when that authority does not satisfy the request.

When `RequiresApproval` remains after those checks, the tool execution pipeline
SHALL pause the individual tool task and emit a `ToolInteractionRequest` to
session subscribers. The pipeline SHALL NOT block other tool calls in the same
batch.

#### Scenario: Granted tool executes successfully

- **GIVEN** the session has an ACL grant for `web_search`
- **AND** `web_search` is in Auto approval mode
- **WHEN** the LLM requests a web search tool call
- **THEN** the ACL check passes
- **AND** the authorization outcome is `Allowed`
- **AND** the tool executes
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  allow result

#### Scenario: Ungrantable tool denied at invocation

- **GIVEN** the session does not have an ACL grant for `shell_execute`
- **WHEN** the LLM requests a shell tool call
- **THEN** the ACL check fails
- **AND** the authorization outcome is `Denied`
- **AND** the tool is not executed
- **AND** a policy denial with reason code is returned to the LLM
- **AND** an audit record is logged with tool name, session ID, timestamp, and
  deny result

#### Scenario: Tool requires approval and is approved

- **GIVEN** the session has an ACL grant for `shell_execute`
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

#### Scenario: Example - unmet authority exposes a correction

- **GIVEN** a tool request requires user approval
- **AND** the request has a typed correction for a more direct native tool
- **AND** neither stored nor one-time authority satisfies the request
- **WHEN** the pipeline completes authorization
- **THEN** the result carries the correction to the LLM
- **AND** the correction grants no authority to execute the original request

#### Scenario: Counterexample - stored authority suppresses a correction

- **GIVEN** a `RequiresApproval` decision also carries a typed correction
- **AND** stored authority satisfies every approval candidate
- **WHEN** the pipeline completes authorization
- **THEN** the final outcome is `Allowed`
- **AND** the correction is not presented to the LLM

#### Scenario: Counterexample - correction does not execute a tool

- **GIVEN** authorization returns `RequiresAgentCorrection`
- **WHEN** the pipeline handles the result
- **THEN** the original tool does not execute
- **AND** the agent must make a new tool call under normal authorization

#### Scenario: Audit records available in diagnostics

- **GIVEN** tool invocations have occurred
- **WHEN** the operator views diagnostics
- **THEN** audit records show tool name, invoking session, timestamp, and
  authorization result for each invocation

#### Scenario: Counterexample - shell denial stops before file protection

- **GIVEN** the audience has file write authority for a path
- **AND** shell execution is disabled
- **WHEN** the agent requests a shell command that names that path
- **THEN** shell policy denies the invocation
- **AND** file authority does not enable shell execution
- **AND** no approval is requested

#### Scenario: Example - Team file write does not require shell

- **GIVEN** a Team audience has `file_write` capability and `Write` authority
  for a path
- **AND** shell execution is disabled
- **WHEN** the agent calls `file_write` for that path
- **THEN** the structured tool uses its `Write` path access decision
- **AND** shell policy is not consulted
- **AND** the tool can proceed under its own approval policy

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

### Requirement: File read tool

The system SHALL provide a `file_read` first-party tool that authorizes the
requested path through the shared `Read` path access decision before inspecting
or reading bytes. An explicit `Roots` or `None` read profile SHALL remain
authoritative in every interaction mode. The default interactive Personal
`All` profile MAY read outside configured roots because the file profile itself
grants that authority, not because shell or approval policy widens it.

Text-like files SHALL preserve the existing encoding, pagination, and output
limits. Non-text files SHALL return structured metadata and guidance rather
than raw binary content. Images MAY use the existing model-visible media handoff
when the model supports images. PDF extraction, OCR, audio transcription, and
video keyframe extraction SHALL NOT be built into `file_read`.

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

### Requirement: Working directory declaration stays scoped

The `session-cwd` capability SHALL own `set_working_directory` behavior and
session state. This capability SHALL provide the shared `DeclareProjectScope`
path operation and its position before approval. It SHALL NOT duplicate the
declaration-root or session-state contract from `session-cwd`.

#### Scenario: Interactive Personal session cannot widen the working directory

- **GIVEN** `set_working_directory` requests a project declaration outside the
  roots that the `session-cwd` contract permits
- **WHEN** tool capability admits the invocation
- **THEN** file protection evaluates the request as `DeclareProjectScope`
- **AND** the path access decision denies the request before approval
- **AND** the `session-cwd` capability owns the unchanged session state

## ADDED Requirements

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

### Requirement: File protection is an inner tool-policy layer

`netclaw-tools` SHALL own one file-protection layer and one path access decision
for structured file tools, project-directory declarations, and known shell path
facts. The decision SHALL use:

- the canonical path;
- its relationship to a trusted root;
- the requested file operation;
- the audience policy; and
- protected-path and filesystem-link results.

The decision SHALL return an allowed or denied result. A denied result SHALL
carry one failure category and human-readable detail. A caller SHALL NOT repeat
root assembly, containment, or filesystem-link policy.

A structured tool SHALL provide its exact operation. Every known path referenced
by a shell invocation SHALL use the conservative `Write` operation. Netclaw
SHALL NOT infer whether arbitrary shell syntax reads or writes a path.

Tool capability and tool-family safety SHALL run before file protection. Shell
mode and shell command policy SHALL therefore deny an ineligible shell call
before path access is evaluated. File authority SHALL NOT enable shell. When an
eligible shell call has a known path, a `Write` denial SHALL stop the call before
approval.

An explicit `Roots` or `None` file profile SHALL remain authoritative in every
interaction mode. User approval SHALL NOT widen it. The default interactive
Personal `All` profile remains broad because that file profile grants broad
authority.

An unresolved interactive shell path MAY reach one-shot user approval. It SHALL
NOT receive reviewed-safe or persistent coverage. For a known shell path,
approval SHALL NOT widen an explicit `Roots` or `None` file profile.
File protection SHALL derive known real paths from shell command analysis,
independent of reusable approval candidates. It SHALL also check known causal
intent and fallback paths before stored or reviewed-safe coverage.

The existing `file_read`, `file_search`, `file_list`, `file_write`,
`file_edit`, and `attach_file` tools SHALL use this decision. They SHALL keep
their existing output, pagination, query, and approval contracts.

The Netclaw sessions root SHALL be a trusted root for parent and child runs.
A path in another session SHALL use the same decision as any other path below
that root. Session identity SHALL NOT add another access-control rule.

The system SHALL NOT add a log-specific tool, ownership check, projection, or
query language. `file_read` and `file_search` SHALL remain compatible with an
active log writer on POSIX and Windows.

#### Scenario: Example - one session reads another session's log

- **GIVEN** the audience permits `file_read`
- **AND** two sessions are below the Netclaw sessions root
- **WHEN** one session requests the other session's canonical log path
- **THEN** one `Read` path access decision allows the request
- **AND** `file_read` applies its normal output bounds

#### Scenario: Example - parent searches a child log

- **GIVEN** a parent receives a child log path from `spawn_agent`
- **WHEN** it calls `file_search` for that path
- **THEN** the shared path access decision evaluates the request
- **AND** the parent needs no shell or log-specific tool

#### Scenario: Example - an active Windows log remains readable

- **GIVEN** a log writer holds its append handle open
- **WHEN** `file_read` or `file_search` opens that log on Windows
- **THEN** the read succeeds
- **AND** the writer can append and flush another line

#### Scenario: Counterexample - a trusted root does not grant every operation

- **GIVEN** an audience permits `Read` but denies `Write`
- **WHEN** it requests both operations for one path below a trusted root
- **THEN** the shared decision allows the read
- **AND** it denies the write

#### Scenario: Example - structured tools use their exact operation

- **GIVEN** a path is readable but not writable under the audience file profile
- **WHEN** `file_read` and `file_write` target that path
- **THEN** `file_read` supplies `Read` and is admitted
- **AND** `file_write` supplies `Write` and is denied

#### Scenario: Counterexample - every known shell path requires write authority

- **GIVEN** an eligible shell invocation names a known path
- **AND** the audience can read but cannot write that path
- **WHEN** authorization evaluates the shell invocation
- **THEN** file protection evaluates the path as `Write`
- **AND** the shell invocation is denied before approval

#### Scenario: Counterexample - read authority cannot admit shell

- **GIVEN** a structured read would be allowed for a path
- **AND** shell execution is disabled or the command is denied
- **WHEN** a shell invocation names that path
- **THEN** shell policy denies the invocation before file protection
- **AND** the allowed read decision does not enable shell

#### Scenario: Counterexample - approval cannot widen explicit file authority

- **GIVEN** an interactive profile explicitly limits writes to one root
- **AND** an eligible shell invocation names a known path outside that root
- **WHEN** authorization evaluates the invocation
- **THEN** file protection denies the path as `Write`
- **AND** the invocation does not reach user approval

#### Scenario: Counterexample - dynamic syntax cannot hide another known path

- **GIVEN** one shell analysis contains a known path outside the write roots
- **AND** another command occurrence contains unresolved dynamic syntax
- **WHEN** the dynamic syntax removes all reusable approval candidates
- **THEN** file protection still evaluates the known path as `Write`
- **AND** the invocation is denied before one-shot approval

#### Scenario: Example - parser-resolved shell path keeps file protection

- **GIVEN** PowerShell resolves `FileSystem::C:\outside\data.txt` as the
  filesystem path `C:\outside\data.txt`
- **WHEN** shell file protection evaluates the invocation
- **THEN** it evaluates the parser-resolved path as `Write`
- **AND** it does not reinterpret the provider syntax with a command-specific
  Netclaw rule
- **AND** an outside-root path is denied before reviewed-safe or approval
  coverage

#### Scenario: Counterexample - causal approval scope cannot widen file authority

- **GIVEN** a causal Bash projection has a known intent or fallback path outside
  the write roots
- **AND** a stored grant covers each reusable command candidate
- **WHEN** the shell coordinator evaluates the projection
- **THEN** file protection denies the external path as `Write`
- **AND** the stored grant does not admit the invocation

#### Scenario: Counterexample - a filesystem link cannot escape

- **GIVEN** a path below a trusted root crosses a filesystem link outside it
- **WHEN** any file operation requests that path
- **THEN** the shared decision denies the request
- **AND** no caller can bypass that result with another path policy

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

### Requirement: Ordinary configuration is readable without exposing secrets

`netclaw.json` SHALL contain ordinary configuration. Its schema has no
secret-bearing fields. When a structured file read is otherwise authorized by trusted
roots and audience policy, `file_read` SHALL be able to read the exact
`netclaw.json` path. The implementation SHALL keep structured read-deny rules
independent from broader shell-deny indicators.

Secret-valued configuration SHALL be stored only in protected secret stores.
`secrets.json`, key material, OAuth token and credential material, webhook
secret material, the session database and sidecars, process-control files, and
similar protected state SHALL remain read-denied. A readable configuration
file SHALL NOT imply write, edit, attach, or shell authority.

This change SHALL NOT add content redaction, secret-field heuristics, or a
configuration migration path. Existing configuration validation and separate
secret storage remain authoritative.

#### Scenario: Example - agent reads ordinary stored configuration

- **GIVEN** `netclaw.json` contains no secret-valued fields
- **AND** the current audience can use `file_read` under a trusted root that
  contains the configuration file
- **WHEN** the agent calls `file_read` for the exact `netclaw.json` path
- **THEN** the tool returns its normal bounded file content
- **AND** no shell command or special configuration reader is required

#### Scenario: Counterexample - secret store remains denied

- **GIVEN** the same agent can read ordinary configuration
- **WHEN** it requests `secrets.json`, key material, OAuth credentials, or
  webhook secret material
- **THEN** protected-path policy denies the read
- **AND** the result does not include secret content

#### Scenario: Counterexample - read authority does not grant mutation

- **GIVEN** `file_read` can read `netclaw.json`
- **WHEN** the agent calls `file_write`, `file_edit`, `attach_file`, or a shell
  command for that path
- **THEN** the read decision is not reused
- **AND** the operation follows its independent policy

#### Scenario: Counterexample - stored configuration is not effective configuration

- **GIVEN** an environment variable overrides a value from `netclaw.json`
- **WHEN** an agent reads `netclaw.json`
- **THEN** it receives the persisted non-secret file content
- **AND** the tool does not claim that the file explains the source or
  effective value of every configuration setting

# Netclaw Engineering Glossary

This glossary defines cross-cutting terms for Netclaw engineering documents.
PRDs, engineering specs, OpenSpec files, and reviews use these meanings.

A code anchor shows the current implementation owner. It does not freeze the
class name or prevent a later refactor.

## How to Use This Glossary

- Link to this file instead of copying a definition into another document.
- Define a local term when only one capability uses it.
- Add a term here when two or more capabilities need the same meaning.
- State an explicit exception when a specification narrows a glossary term.
- Prefer a plain term over an internal class name in descriptive text.

## Tool Call Flow

This flow shows a normal tool return:

```text
model
  -> tool call
  -> dispatcher
       -> authorization
       -> optional approval request
       -> tool implementation
       -> factual tool result -> redaction and output bound --+
       -> internal tool receipt ------------------------------+---> correction presenter
                                                                  -> model-facing result

internal tool receipt
  -> optional actor working-context update
```

The result and receipt are sibling outputs of execution. The correction
presenter can use both to build the final model-facing result. The actor uses a
successful receipt only when that receipt contains a defined state effect.

A terminal non-cancellation exception follows a different delivery branch:

```text
dispatcher classifies the receipt and throws
  -> parent or child actor creates the factual failure result
  -> correction presenter
  -> model-facing result
```

An approval request remains non-terminal, and caller cancellation propagates.
Neither case creates this terminal receipt and failure result.

## Runtime and Actor Terms

### Actor

An actor owns mutable runtime state and processes one message at a time. Netclaw
uses actors for sessions, subagents, reminders, and other runtime components.

### Main session

The main session is the actor that owns the user conversation. Some code and
specs call it the parent session when they compare it with a subagent.

**Code anchor:** `LlmSessionActor`

### Subagent

A subagent is a child actor that performs one delegated task. It has its own
model requests, tool exposure set, and ephemeral working context.

**Code anchors:** `SubAgentActor`, `SubAgentSpawner`

### Working context

The working context is actor-local state that helps later model turns. It
contains the optional declared project directory and a bounded list of recently
used files. The main session persists its `WorkingContext`. A subagent keeps its
working context only for the child run. Git branch and worktree facts come from
a separate inspection and are not fields in either working context.

**Code anchors:** `WorkingContext`, `ChildFileActivityTracker`

### Durable and ephemeral

Durable data survives actor restart because Netclaw stores it. Ephemeral data
exists only in memory for the current call, turn, actor, or process.

Example:

```text
durable:   a stored tool-role chat message
ephemeral: the ToolInvocationReceipt for that tool call
```

## Tool Lifecycle Terms

### Tool definition or tool schema

A tool definition is the name, description, and argument schema that the model
can see. It tells the model how to author a tool call.

### First-party tool and MCP tool

A first-party tool is implemented and registered by Netclaw. An MCP tool comes
from an external Model Context Protocol server. Both kinds still pass Netclaw's
exposure and authorization boundaries.

### Policy-visible tool

A policy-visible tool passes `ToolAccessPolicy.IsToolExposed` for the current
audience. This check applies feature gates, audience rules, approval-mode
denies, and shell-coupled limits. MCP registrations also apply MCP server and
tool rules. A policy-visible tool can still be Deferred and absent from the
current model request. Policy visibility does not grant execution authority.

Example:

```text
list_reminders passes ToolAccessPolicy.IsToolExposed
  -> policy-visible
  -> still absent until the actor loads its Deferred schema
  -> normal authorization still controls a later call
```

**Code anchor:** `ToolAccessPolicy.IsToolExposed`

### Native Netclaw tool

A native Netclaw tool is a first-party structured tool in `ToolRegistry`. In
this phrase, native does not mean a host executable or a native binary.

**Code anchors:** `ToolRegistry`, `NativeToolShellCorrectionDetector`

### Skill resource

A skill resource is an additional file inside a registered file-backed skill
directory. The `skill_read_resource` tool resolves it from a logical skill name
and a permitted relative path. The `skill_load` tool reads `SKILL.md` instead.

```text
skill_read_resource("netclaw-operations", "references/tools.md")
  -> read references/tools.md inside the netclaw-operations skill folder

skill_read_resource("netclaw-operations", "SKILL.md")
  -> reject the request and direct the model to skill_load
```

**Code anchors:** `SkillReadResourceTool`, `FileSkillSource`

### Workspace tool

A workspace tool reads, lists, writes, edits, attaches, or selects files and
directories. It uses the project and session path rules in this glossary.

### Core tool

A core tool is eligible for the initial model-visible tool set. Policy can still
hide it from a specific audience or session.

### Deferred tool

A deferred tool is registered but absent from the initial model-visible set.
An allowed actor can find it and load its schema later.

### Schema exposure

Schema exposure means that the model can see a tool definition. Exposure does
not grant permission to execute the tool.

Example:

```text
load_tool("list_reminders")
  -> the next model request can see the list_reminders schema
  -> a later list_reminders call still runs normal authorization
```

### Tool call

A tool call is the model-authored tool name, arguments, and call identifier.
It is a request, not proof that Netclaw executed the tool.

### Dispatcher

The dispatcher is the shared execution boundary for registered tools. It finds
the registration, runs authorization, invokes the tool, and records an outcome.

**Code anchor:** `DispatchingToolExecutor`

### Authorization

Authorization is the runtime decision that allows, denies, or pauses a tool
call. It evaluates the current audience, policy, path, and approval state.

### Authority

Authority is the set of actions that the current session is permitted to take.
Tool exposure, a receipt, and a correction instruction do not add authority.

### Approval

Approval is an operator decision for a call that requires consent. Approval is
one input to authorization. It is not the same as schema exposure.

Example:

```text
schema exposed + approval required + no approval
  -> the model can author the call
  -> Netclaw does not execute the tool
```

### Tool result

The tool result is the text that Netclaw returns to the model. Normal dispatcher
results pass output redaction and bounding before delivery. A result can contain
data, an error, or a factual explanation of a correctable problem.

### Tool receipt

A tool receipt is trusted internal data about one completed invocation attempt.
The attempt can end before the tool implementation runs. An actor uses the
receipt to update state without parsing model-facing text or authored arguments.

**Code anchor:** `ToolInvocationReceipt`

Example:

```text
result:
  "README content..."

receipt:
  category      = Success
  file activity = Read("/workspace/project/README.md")
```

### File activity

File activity is a canonical path and an operation kind recorded in a successful
receipt. The working context uses it instead of guessing paths from authored
arguments or result text.

### Outcome category

The outcome category is the closed status in a tool receipt. Current categories
include success, invalid input, access denied, not found, transient failure, and
recoverable correction.

**Code anchor:** `ToolInvocationOutcomeCategory`

### Remediation code or next-action code

A remediation code is a closed internal value for one bounded correction
strategy. It contains no path or free-form instruction.

**Code anchor:** `ToolRemediationCode`

Example:

```text
result:
  "No project or session directory is available."

receipt:
  category    = RecoverableCorrection
  remediation = SetWorkingDirectory
```

The prose phrase "typed remediation" means that the receipt uses this enum. It
does not mean that Netclaw executes the action or grants new authority.

### Correction presenter

The correction presenter converts a valid remediation code into one fixed model
instruction. It does not inspect arguments, execute tools, or grant authority.

**Code anchor:** `ToolRemediationPresenter`

Example:

```text
input result:  "No project or session directory is available."
input code:    SetWorkingDirectory
tool visible:  true

final message:
  "No project or session directory is available.
   Next action: call set_working_directory ..."
```

If the named tool is hidden, the presenter leaves the factual result unchanged.
This prevents an instruction that the current model cannot follow.

## MCP Invocation Terms

These terms classify what an MCP server's answer means for the client. The
`netclaw-mcp` and `netclaw-tools` capabilities share them. The examples come
from a link-shortener MCP server observed on 2026-08-26.

### Transport or session failure

A transport or session failure means the request got no usable answer. The
connection broke, the request timed out, or the server reported that the
session is gone (HTTP 404 under Streamable HTTP). A new session can repair it.
Netclaw reconnects once for later calls and never replays the failed call.

Example:

```text
HttpRequestException with no status code      -> transport failure
HttpRequestException with HTTP 404            -> session failure
IOException, ClientTransportClosedException   -> transport failure
```

**Code anchor:** `McpClientManager.IsTransportOrSessionFailure`

### Application error

An application error is an answer from a server that received the request: an
HTTP status other than 404, a JSON-RPC error, or a tool-declared error. A new
session cannot change the answer, so Netclaw returns it without a reconnect.

Example:

```text
HTTP 429  {"statusCode":429,"error":"Too Many Requests","message":"Rate limit exceeded, retry in 52 seconds"}
HTTP 401  {"jsonrpc":"2.0","error":{"code":-32000,"message":"Unauthorized: No API key provided"},"id":null}
```

Both are application errors. Neither triggers a reconnect.

### Tool-declared error

A tool-declared error is a successful JSON-RPC response whose result carries
`isError: true`. The tool ran and reported a failure in its own words. Netclaw
formats it as a tool result and logs it at Warning. It is not an exception and
does not produce an exception outcome.

Example (`search-links` called with only its one required argument):

```text
HTTP 200
{"result":{"content":[{"type":"text","text":"Internal Server Error"}],"isError":true},"jsonrpc":"2.0","id":70}

tool result: "Error: MCP tool 'shortio/search-links' reported a failure: Internal Server Error"
daemon log:  [WRN] McpClientManager: MCP tool 'shortio/search-links' reported a failure: Internal Server Error
```

**Code anchors:** `McpClientManager.ReportToolFailure`, `McpToolResultFormatter`

### OAuth-managed server

An OAuth-managed server is one for which Netclaw uses OAuth. The daemon knows
this from two facts, not from header names: it holds OAuth tokens for the
server, or the server answered with a genuine OAuth challenge that the SDK
turned into a Bearer-scheme `McpException`. Only an OAuth-managed server gets
the `netclaw mcp auth` remedy. A 401 from any other server means the operator
must check the configured credentials or headers, whatever those headers are
named.

Example:

```text
http, stored OAuth tokens, tool call -> 401           -> OAuth-managed; "Run: netclaw mcp auth"
http, no tokens, SDK reports a Bearer challenge        -> OAuth-managed; "Run: netclaw mcp auth"
http, X-Api-Key header, no tokens, tool call -> 401    -> not OAuth-managed; "Check configured credentials or headers."
http, no headers, no tokens, isError "token expired"   -> not OAuth-managed; stays Connected
```

**Code anchors:** `McpClientManager.HasStoredOAuthTokens`, `McpClientManager.IsOAuthChallenge`

## Filesystem and Output Terms

### Project scope

Project scope is the declared project directory for a main session or subagent
run. Workspace tools use it as the first base for relative paths.

### Session directory and session scratch

The session directory path is fixed for one session. Its contents are mutable.
Session scratch means disposable work inside that directory. It is the
relative-path base when the session has no valid project directory.

### Allowed root

An allowed root is a configured or context-derived directory boundary for a
specific access type. A path inside one root can still fail another security
check.

### Canonical path

A canonical path is a fully qualified, normalized path with no unresolved dot
segments. Canonical form does not by itself grant access.

### Safe, unavailable, and unsafe path bases

These states describe a candidate base for a relative path:

- **Safe:** The base is usable and passes the required authority and link checks.
- **Unavailable:** The base is absent, not a fully qualified path, or is a
  missing project directory. Policy can try the next documented base before
  authorization starts.
- **Unsafe:** The base is present but normalization or inspection fails, has no
  owning authority root, or crosses a link boundary. Netclaw denies the call and
  does not try another base.

Example:

```text
project missing
  -> Unavailable
  -> try the session directory

project ancestor is a link to /outside
  -> Unsafe
  -> AccessDenied
  -> do not try the session directory
```

**Code anchor:** `ScopedFileAccessPolicy`

### Spill and output continuation

A spill is the full redacted result stored in session-owned output after the
result exceeds its inline budget. The model receives an opaque call identifier.
It uses `tool_output_read` to read another bounded window.

```text
large result
  -> bounded inline preview
  -> opaque call id
  -> tool_output_read(CallId, Start, Limit)
```

The model does not receive the internal spill path.

## Context

The prior tool work added relative paths, call-local receipts, progressive disclosure, and several focused tools. Live use and adversarial review exposed three problems.

First, a relative project base can contain an ancestor link that escapes its
trusted root. Second, receipt categories differ between parent and child policy
denials. Third, two bulk tools duplicate smaller tools and can consume too much
model context.

The canonical spill contract also still describes a raw file path and shell grep. The runtime now exposes an opaque `tool_output_read` route instead.

Constraints:

- ACL, audience, approval, path, and MCP policy remain authoritative.
- Public durable session and approval formats do not change.
- No released tag contains `json_read` or `file_read_many`.
- ShellSyntaxTree remains the owner of shell syntax facts.
- Netclaw adds no executable-specific command parser.

## Vocabulary and Tool Flow

Use the [Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for the
cross-cutting terms in this design.

The normal first-party tool-return flow is:

```text
model
  -> tool call
  -> DispatchingToolExecutor
       -> authorization
       -> tool implementation
       -> factual result -> redaction and output bound --+
       -> ToolInvocationReceipt --------------------------+---> remediation presenter
                                                           -> model-facing result

ToolInvocationReceipt
  -> optional working-context update in LlmSessionActor or SubAgentActor
```

An approval request pauses this flow before tool execution. It does not create
a terminal receipt. Caller cancellation also propagates without a receipt or
model-facing failure. For another terminal exception, the dispatcher first
classifies the receipt. The parent or child actor then creates the factual
failure result and applies the same remediation presenter before model delivery.

## Goals / Non-Goals

**Goals:**

- Close the ancestor-link path escape.
- Produce one receipt category for the same failure in every actor path.
- Remove two bulk tools before their first release.
- Keep the agent tool surface small and composable.
- Align prompts, skills, fixtures, and canonical specifications.
- Test a real child catalog instead of a parent substitute.

**Non-Goals:**

- Add search or grep to spilled output.
- Block a known deferred tool name at dispatch before schema load.
- Change shell approval authority.
- Add a durable receipt format.
- Preserve compatibility for unreleased tool classes.

## Decisions

### 1. Base selection and path authorization have different owners

`session-cwd` selects one available base for a relative path. It does not decide
whether that path is authorized. The `netclaw-tools` path access decision validates
the complete selected base chain and the resolved authored path.

The decision rejects a link or junction in the base or its ancestors below the
trusted root. It also applies the audience, file-operation, and protected-path
rules. The system can use the session directory only when the project base is
unavailable before authorization starts. It does not retry another base after
a denial.

Example:

```text
trusted root     = /srv/workspaces
declared project = /srv/workspaces/team/project
authored path    = README.md

/srv/workspaces/team is a link to /outside
  -> relative base availability = Available
  -> path access decision       = Denied(link boundary)
  -> result category            = AccessDenied
  -> session fallback           = forbidden

/srv/workspaces/team/project does not exist
  -> relative base availability = Unavailable
  -> try the valid session directory before authorization starts
```

Alternative: inspect only the final base. This leaves an ancestor-link escape.

### 2. The dispatcher owns terminal receipt classification

The dispatcher will include authorization in its receipt classification boundary. A `ToolAccessDeniedException` will produce `access_denied` for all callers.

A `ToolApprovalRequiredException` will stay non-terminal. The caller can still complete an approval and retry the same call.

Workspace tools can set a more exact terminal receipt. The dispatcher will fill a receipt only when the tool has not set one.

The receipt carries the stable category used by actor state. The separate
bounded result carries the reason presented to the model. For example, a
dispatcher policy denial can return
`Tool access denied: tool_not_allowed_for_audience_profile` while the receipt
remains the closed `AccessDenied` category. Actors never parse that sentence to
recover the category.

Example:

```text
policy denies file_read before FileReadTool runs
  -> parent receipt = AccessDenied
  -> child receipt  = AccessDenied
  -> file activity  = empty

policy requires approval
  -> terminal receipt = none
  -> approved retry enters authorization again
```

Alternative: keep actor-specific exception maps. That design already caused parent and child drift.

### 3. Only one named tool can declare project scope

Actors will apply `DeclaredProjectDirectory` only for a successful `set_working_directory` receipt. Another tool cannot mutate project scope through the internal receipt seam.

Remediation codes will use a closed internal enum. A corrective receipt must use
one defined enum value. Every other outcome must reject remediation.

Example:

```text
file_read + Success + DeclaredProjectDirectory
  -> actor rejects the project effect

set_working_directory + Success + DeclaredProjectDirectory
  -> actor replaces the project scope

file_edit finds three matches
  -> result reports the match count
  -> receipt = RecoverableCorrection(ProvideUniqueOldString)
  -> no arbitrary instruction enters the receipt
```

Alternative: trust any internal receipt producer. This makes future tool additions able to widen scope by accident.

### 4. Remove bulk readers and keep composable primitives

The implementation will remove `json_read` and `file_read_many`. It will remove their registration, audience entries, schemas, guidance, tests, and eval cases.

An agent can compose these retained tools:

- `file_search` locates candidate files.
- `file_read` reads one bounded file window.
- Parallel tool calls read several known files without one bulk result.
- `tool_output_read` continues one spilled result by opaque call id.

The replay corpus will not preserve a product expectation for a removed tool. A retained scenario can assert the composed route when that route matches the original intent.

Example:

```text
one model response:
  file_read(Path = "README.md", StartLine = 1, Limit = 200)
  file_read(Path = "CONTRIBUTING.md", StartLine = 1, Limit = 200)

runtime:
  execute both bounded calls in one tool batch
  return two separate bounded results
```

For bounded JSON text, the model uses `file_read`. A stable domain query should
use a purpose-built producer tool.

Alternative: keep the bulk tools with lower bounds. The larger schemas and batch results still duplicate simpler calls and invite context floods.

### 5. Loading controls schema exposure only

`load_tool` adds a deferred schema to one actor's model-visible set. It does not grant execution authority.

Dispatch will still resolve a known registered name and run normal authorization. This preserves compatibility with models that recall a name from earlier context.

Guidance will tell an agent to call `load_tool` directly when it knows the exact name. The agent will use `search_tools` only when it knows an intent but not a name.

When retention and maximum-count tuning are positive, main sessions retain a
loaded schema for the configured number of future user turns, which defaults to
three. Reloading refreshes the lease. The default maximum is twelve loaded
schemas; adding another evicts the oldest. Recovery or an LLM failure discards
the actor-local loaded set. A child has a shorter and simpler lifetime: a loaded
schema remains for later iterations in that child run and disappears when the
child ends.

Example:

```text
model knows the exact name:
  load_tool(Name = "list_reminders")
  -> next request includes the list_reminders schema
  -> later call still runs authorization

model knows only the intent:
  search_tools(Query = "show scheduled reminders")
  -> policy-filtered results
  -> load one exact result
```

Alternative: require an activation lease before dispatch. This adds a second authority-like state and does not improve the real approval boundary.

Counterexample: a loaded schema is not durable session state. A recovered main
session and a newly started child both begin from their policy-filtered core.

### 6. Spill continuation uses one opaque contract

The dispatcher will not reveal a raw spill path to the model. Its steer will name the call id and `tool_output_read`.

The canonical specification will remove the old `file_read` and shell grep route. A later design can assess search over spilled output.

Example:

```text
shell result length = 40,000 characters
inline budget       = 12,000 characters

model receives:
  bounded head and tail preview
  opaque call id = call-example
  next tool      = tool_output_read

model does not receive:
  /session/.../tool-calls/call-example.txt
```

Alternative: expose the spill file to file tools or shell. This couples continuation to filesystem authority and lower-audience shell access.

### 7. Tests follow actual actor and policy boundaries

The security PR will add POSIX and native Windows ancestor-link tests. It will also add executor, parent, and child policy-denial receipt tests.

The tool-removal PR will update the core snapshot and remove bulk-tool fixtures. The rollout PR will create a real subagent for the child catalog replay.

The child catalog excludes `spawn_agent` by design, not merely because the
schema is Deferred. A child cannot recursively create a grandchild. This keeps
concurrency bounded for self-hosted models and preserves one parent-owned
`spawn_agent` lifecycle per child.

The final eval run will use the reduced surface. Public evidence will contain aggregate, PII-free results only.

Example:

```text
child catalog test:
  start SubAgentActor through SubAgentSpawner
  capture the first child model request
  assert the allowed core schemas
  assert hidden schemas are absent

path security test:
  create a native link or junction in the selected base chain
  call a relative file tool
  assert AccessDenied and no file access
```

## Actor Boundaries and Persistence

`DispatchingToolExecutor` owns authorization and terminal receipt classification. `LlmSessionActor` and `SubAgentActor` consume the same receipt facts.

Only those actors update their current working context. The main session persists its existing `WorkingContext`. A child keeps its context ephemeral.

This change adds no event, snapshot, protobuf, or configuration field. Recovery behavior stays unchanged.

## Failure Modes and Recovery

- Project ancestor escapes its trusted root: return `access_denied`; do not access the file.
- Policy denial: return or propagate the existing bounded denial with an `access_denied` receipt.
- Approval request: record no terminal receipt; retry after the approval result.
- Removed tool name: return the normal hidden or unknown tool result.
- Missing spill: return `not_found` for the opaque call id.
- Failed project declaration: keep the previous project and instructions.
- Eval provider failure: mark infrastructure failure; do not count it as task behavior.

## Risks / Trade-offs

- [Agents may need more file calls] -> Keep parallel `file_read` calls and strict output bounds.
- [A recalled deferred name can bypass schema load] -> Keep normal authorization and define load as exposure only.
- [Native link tests need platform support] -> Use deterministic platform tests and explicit skips only when link creation is unavailable.
- [Corpus changes can erase historical intent] -> Keep sanitized intent and source classification, but remove product expectations for deleted tools.
- [The stack can drift after a rebase] -> Rebase each branch and compare patch identity before PR publication.

## Migration Plan

1. Merge the path and receipt security fixes.
2. Remove both unreleased bulk tools and update the tool surface.
3. Repair the spill, discovery, replay, and eval contracts.
4. Run deterministic tests on each PR.
5. Run hosted evals once on the final stacked head.

Rollback uses normal code reversal. No persisted state requires migration.

## Open Questions

The design for search inside spilled output remains open. It requires a separate security and product review.

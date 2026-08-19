## Context

Netclaw currently treats every non-MCP registration as always loaded. Main
sessions dynamically load MCP tools, but subagents receive every discoverable
registration except recursive `spawn_agent`. In the observed deployment this
meant roughly 220 schemas per subagent. Despite that large choice surface,
agents repeatedly used approval-gated shell or Python for operations absent from
the first-party workspace API: recursive search, batch reads, JSON projection,
image dimensions, and continuation of spilled output.

The file tools also resolve a relative path through the daemon process current
directory. Recent-file context is reconstructed from authored arguments for all
tool results, including failures. These behaviors make the less-safe route
easier and allow a failed operation to influence later model context.

Constraints:

- ACL, audience, approval, path, and MCP grant enforcement remain authoritative.
- `INetclawTool.ExecuteAsync` is a public string-returning contract and cannot
  break in this change.
- Session actors own per-session loaded-tool state and durable working context.
  Subagent actors own ephemeral child loaded-tool state; neither state is newly
  persisted.
- ShellSyntaxTree remains a syntax-fact provider. This design adds no
  executable-specific policy parsing.

## Goals / Non-Goals

**Goals:**

- Make the structured workspace route require fewer decisions than shell.
- Reduce the default tool-schema surface for main sessions and subagents.
- Give tools machine-actionable success, failure, and correction semantics while
  preserving their model-facing string results.
- Make relative file paths deterministic and scoped to project then session.
- Turn sanitized live failures into deterministic contract and replay tests.

**Non-Goals:**

- Reclassifying shell commands or widening reviewed-safe command authority.
- Persisting loaded-tool leases or changing durable session serialization.
- Parsing arbitrary executable flags, Markdown artifact links, or remote MCP
  payloads as filesystem authority.
- Implementing layered configuration provenance from issue #2009.
- Replacing every existing first-party tool in one step.

## Decisions

### 1. Registration owns an explicit safe-default exposure tier

`ToolRegistry` will distinguish `Core` and `Deferred` registrations. The
existing registration path will default to `Deferred`; a deliberately named
core-registration path will be required for the small always-loaded set. This
makes adding a new tool context-safe by default without adding visibility state
to the public `INetclawTool` interface.

The initial core set is:

- `search_tools` and `load_tool`;
- `skill_load` and the resource reader needed by a loaded skill;
- `set_working_directory`;
- `file_read`, `file_list`, `file_write`, and `file_edit`;
- `shell_execute` as the bounded compatibility escape hatch.

Structured workspace tools added by this change are core only when they replace
a commonly observed shell fallback with a small schema. Reminder, webhook,
memory-administration, background-job, subagent-administration, web, channel,
and other specialty tools are deferred. MCP tools remain deferred.

Alternative considered: infer tiers from tool names or grant categories. That
would couple unrelated security and context concerns and would silently change
when names/categories evolve. Explicit registration is smaller and auditable.

### 2. One audience-filtered exposure set serves parent and child actors

The existing discovered-tool cache will be generalized around canonical tool
identity rather than MCP type. Main sessions continue to own lease retention and
eviction. Each subagent receives a private ephemeral exposure set seeded from
the same audience-filtered core registrations. Its `search_tools` and
`load_tool` calls activate deferred tools only for that child run.

Search and load always re-run `ToolAccessPolicy` against the current invocation
context. Hidden tools do not appear in indexes, search results, suggestions, or
load errors. Loading never grants execution authority; normal dispatch policy
still runs on every call.

Alternative considered: provide each subagent a task-specific static tool list.
Observed models choose unexpected but legitimate workflows, and static lists
would either recreate overexposure or create brittle missing-tool failures.

### 3. Workspace paths resolve from explicit session-owned roots

First-party filesystem tools will resolve a relative authored path against:

1. the declared project directory when present and valid;
2. otherwise the immutable session directory;
3. otherwise fail with `invalid_context`.

The resolved canonical path then enters the existing read/write/attach policy.
Absolute paths retain their existing policy behavior. Resolution never uses the
daemon process current directory. A relative path that escapes its selected base
through traversal is still evaluated as the resulting canonical absolute path
and denied when outside the applicable roots.

This is implemented once in `ScopedFileAccessPolicy`, not independently in each
tool.

Alternative considered: require absolute paths forever. Live evidence shows
models invent `/tmp` or use shell to obtain an absolute path; the requirement
creates friction without adding authority because a trusted base already exists.

### 4. Add narrow workspace primitives instead of more guidance

The first implementation slice adds:

- recursive literal content/path search with explicit root and bounds;
- bounded reads of multiple files in one call;
- JSON projection using a bounded list of RFC 6901 JSON Pointers;
- image metadata, including dimensions, through the file inspection path;
- continuation of a spilled result by opaque call id and bounded character
  window.

Every filesystem operand uses the shared scoped policy. Search does not follow
directory symlinks, has file/result/byte ceilings, and returns truncation state.
Batch tools validate the complete batch before reading so a denied member cannot
produce a partial successful result. The output-continuation tool resolves only
inside the current session's `tool-calls` directory and never accepts a raw path.

Alternative considered: teach agents preferred shell commands through skills.
That leaves approvals, executable availability, quoting, and cross-platform
syntax in the critical path and failed in live use across two model families.

### 5. Internal outcomes drive state; strings remain presentation

An internal append-only invocation receipt will carry one terminal category:

- `success`;
- `invalid_input`;
- `access_denied`;
- `not_found`;
- `transient_failure`;
- `recoverable_correction`.

It may also carry canonical successful file activity and a bounded remediation
code. First-party workspace tools set the receipt before returning their existing
model-facing string. Dispatcher exceptions and policy denials set it centrally.
The session pipeline consumes the receipt; it does not parse strings to infer
success. Only canonical file activity from a successful receipt updates
`WorkingContext.RecentFiles`. Failed declarations and failed file operations do
not update project or recent-file state.

The receipt is call-local, never serialized, and is not added to public
`INetclawTool`. Existing tools without a receipt receive a conservative centrally
known category; they cannot claim successful file activity.

Alternative considered: standardize JSON in every string result. That would
break human/model output expectations and tempt downstream code to parse a
presentation string as authority.

### 6. Conditional schemas are generated from explicit variants

For tools with mutually exclusive modes, the source generator will accept an
internal variant declaration and emit JSON Schema `oneOf` branches with the
correct required fields. Dispatch validates the chosen branch before execution.
The first conversions cover the observed conditional-schema failures; tools with
a single parameter shape remain unchanged.

Alternative considered: put all fields in one object and explain combinations
in descriptions. Multiple models ignored or could not satisfy those prose-only
constraints.

### 7. Tests prove product mechanics deterministically

Tests are layered:

1. schema snapshots and malformed-branch rejection;
2. audience/search/load/core-tier contracts for parent and child actors;
3. path, symlink, traversal, bounds, atomic-batch, outcome, and working-context
   unit/integration tests;
4. PII-sanitized transcript fixtures replaying the observed intent and asserting
   the structured tool path remains available without shell approval.

The sanitized corpus stores no session ids, usernames, hostnames, repository
names, authored content, credentials, or raw paths from production.

## Actor Boundaries and Persistence

- `LlmSessionActor` owns the main exposure cache and applies successful file
  activity to durable `WorkingContext` through existing events/snapshots.
- `SubAgentActor` owns an equivalent ephemeral cache and returns only its
  existing bounded working-context delta to the parent.
- `ToolRegistry` owns immutable registration metadata and remains daemon scoped.
- `DispatchingToolExecutor` owns policy enforcement and the call-local outcome
  receipt. A loaded tool is never an authorization shortcut.
- No new durable event or snapshot field is required. Recovery reseeds core
  tools; deferred tools are intentionally rediscovered.

## Failure Modes and Recovery

- Unknown/hidden tool: bounded `not_found` result without revealing hidden
  names; search can be retried.
- Tool removed after discovery: load or dispatch returns `not_found`; no stale
  registration executes.
- Invalid relative-path context: `invalid_context` correction names the missing
  declaration/session prerequisite without exposing configured roots.
- Batch contains denied/invalid member: whole batch fails before content reads.
- Spill missing/expired: `not_found` names the call id, never probes other paths.
- Child LLM failure: child exposure state is discarded with the actor.
- Main LLM failure: existing discovered-tool eviction applies to every deferred
  tool, not only MCP tools.

## Risks / Trade-offs

- [A deferred tool becomes harder to discover] -> Keep a compressed,
  audience-filtered index and contract-test representative intents.
- [Core set grows back into a large catalog] -> Make deferred the registration
  default and snapshot core names/schema bytes.
- [Structured tools duplicate mature CLI behavior] -> Implement only generic
  filesystem/data primitives with strict bounds; keep private executable
  semantics out.
- [Cross-platform traversal differs] -> Use .NET filesystem APIs and existing
  canonical path policy; run native Windows and Linux tests.
- [Typed outcome adoption becomes a broad rewrite] -> Convert workspace tools
  first, retain a conservative adapter for untouched tools, and remove legacy
  argument scraping only after parity tests pass.

## Migration Plan

1. Add exposure metadata and parent/subagent parity while preserving current
   dispatch authorization.
2. Add the internal outcome receipt and convert current workspace file tools.
3. Add bounded workspace primitives and conditional schemas.
4. Add sanitized replay fixtures and diagnostic counters.
5. Rebase on `upstream/dev`, run full Linux and native Windows gates, then build
   a binary for live swap.

Rollback is a normal code rollback. No persisted state or configuration needs
migration; recovered sessions simply reseed the prior always-loaded catalog.

## Open Questions

- Whether `attach_file` is frequent and small enough to remain core will be
  decided from schema-byte measurements and sanitized traffic counts.
- Whether the JSON projection primitive should support only RFC 6901 pointers or
  a second bounded array-index shorthand will be decided before its schema is
  frozen; arbitrary query languages remain out of scope.

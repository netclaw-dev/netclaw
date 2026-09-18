## Why

Recent PII-sanitized live-session evidence shows that agents receive hundreds of
tool schemas yet still fall back to approval-gated shell or Python for routine
workspace inspection, batch reads, JSON projection, image metadata, and spilled
result continuation. This undermines PRD-001 outcomes 7 and 9, PRD-006 outcome
5, and PRD-007 outcomes 3 and 5: the safe, structured route exists in pieces but
is not the easiest route for either main sessions or subagents.

## Status and Superseding Changes

This change records the original progressive-disclosure and workspace-tool
delivery. Two later stacked changes replace part of this plan:

- [`repair-agent-tool-boundaries`](../repair-agent-tool-boundaries/design.md)
  removes `json_read` and `file_read_many`. It also strengthens path, receipt,
  child, and spill boundaries.
- [`prevent-native-tool-shell-mistakes`](../prevent-native-tool-shell-mistakes/design.md)
  makes `attach_file` a parent-session Core tool. It keeps the tool unavailable
  to subagents until a child attachment handoff exists.

Review the later delta specifications for the current target behavior. The
original decisions below remain a history of the implemented first pass.

## What Changes

- Define one audience-filtered progressive-disclosure contract for first-party
  and MCP tools. A small workspace core remains always visible; specialty tools
  are discoverable and loadable on demand. Subagents use the same contract
  instead of receiving nearly the entire discoverable catalog.
- Make workspace file tools resolve relative paths against the declared project
  directory, then the immutable session directory, while retaining the existing
  scoped-access checks. Failed operations do not mutate recent-file context.
- Add structured, bounded workspace operations for recursive search, batch file
  reads, JSON projection, image metadata, and continuation of spilled tool
  output. These operations return machine-actionable outcomes and remediation
  without requiring shell syntax.
- Make conditional first-party tool schemas describe valid argument shapes so
  models cannot select mutually incompatible modes or omit mode-required fields.
- Replace prompt-only success claims with deterministic schema, authorization,
  outcome, and PII-sanitized transcript-replay tests.

### In scope for this change

- First-party tool visibility tiers and on-demand loading.
- Parent-session and subagent parity for tool discovery and audience filtering.
- Workspace-oriented structured read/search/metadata/result-continuation tools.
- Relative workspace path ownership and typed recoverable tool outcomes.
- Sanitized regression fixtures derived from observed failures and approval
  prompts.

### Out of scope for this change

- Executable-specific shell policy or command-syntax knowledge in Netclaw.
- ShellSyntaxTree grammar or public API changes.
- Layered configuration provenance and CLI editing; tracked separately by
  issue #2009.
- Weakening ACL, approval, path-containment, audience, or MCP grant checks.
- Automatically interpreting arbitrary Markdown links as local artifacts.
- A general tool-runtime rewrite or public `INetclawTool` breaking change.

## Capabilities

### New Capabilities

- `progressive-tool-disclosure`: Defines core versus deferred first-party tools,
  audience-filtered search/load behavior, and identical parent/subagent loading
  semantics.

### Modified Capabilities

- `netclaw-tools`: Adds workspace-relative paths, structured workspace
  operations, machine-actionable outcomes, conditional schemas, and successful
  file-activity ownership.
- `bounded-tool-output`: Adds a bounded structured continuation path for spilled
  results without requiring shell or Python.
- `netclaw-subagents`: Replaces eager exposure of all discoverable tools with
  the shared progressive-disclosure contract.
- `session-cwd`: Makes project-then-session path resolution the explicit default
  for relative first-party filesystem operations.

## Impact

### Code and APIs

The change affects tool registration and discovery, subagent tool resolution,
first-party file/result tools, their MEAI schemas, execution-result handling,
and session file-activity tracking. Public `INetclawTool` and durable session,
approval, and configuration formats remain compatible; structured outcomes use
an internal adapter and the existing string result boundary.

### Security

All discovered and loaded tools remain filtered by deployment switches,
audience allowlists, MCP grants, and existing scoped filesystem authorization.
Relative paths acquire no ambient process-cwd authority. Unknown tools,
malformed conditional arguments, missing spill artifacts, and invalid paths fail
closed with bounded results. No shell allowlist is widened.

### Operations

Tool-definition token load should fall substantially for subagents and modestly
for main sessions. New diagnostics will report core/deferred/loaded counts and
structured outcome categories without command contents or PII. Rollout evidence
will use deterministic tests plus a PII-sanitized replay corpus before a live
binary swap.

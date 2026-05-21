## Why

File-defined subagents currently load at daemon startup, and both
`docs/runbooks/subagents.md` and the `subagent-authoring` system skill tell
operators to restart after every edit. That keeps authoring loops slow, leaves
`spawn_agent` and `metadata.subagent` routed activations on stale registry data,
and makes the active `subagent-explicit-model-selection` planning work refer to
an undefined "reload boundary."

Delegated subagents also do not yet have a planning contract for inheriting the
parent session's filesystem and project context. The parent session can know its
`session_dir` and `project_dir`, load project identity files, and accumulate
working context, but spawned subagents are not guaranteed to start from the same
grounding. That forces callers to restate project details in per-call context and
creates avoidable drift between main-session and subagent behavior.

## Source PRDs

- `PRD-001`: reliable delegation, persistent session continuity, and predictable
  runtime behavior.
- `PRD-002`: default-deny, fail-closed behavior for delegated execution and
  operator-visible diagnostics.
- `PRD-007`: project instructions, local memory, and working-directory grounded
  tool use.
- `PRD-009`: consistent transport-agnostic execution semantics for all session
  entry points, including routed subagent execution.

## What Changes

- Define a live subagent-definition loading contract for
  `~/.netclaw/agents/*.md` so `spawn_agent` and `metadata.subagent` routed
  activations pick up add/update/delete changes without daemon restart.
- Define a deterministic reload boundary before subagent lookup using a
  reloadable registry snapshot rather than startup-only loading.
- Define fail-closed behavior for invalid edits: invalid or no-longer-loadable
  definitions disappear from the active registry with explicit diagnostics rather
  than continuing to serve a stale last-known-good version.
- Define parent-context inheritance for subagent executions so the child receives
  the parent session's `session_dir` and current `project_dir` as a read-only
  execution snapshot.
- Define inherited project-instruction loading for subagents so delegated work
  sees the same project identity file precedence as the parent session.
- Align explicit `spawn_agent` delegation and declarative `metadata.subagent`
  routing so both paths use the same live-loaded registry and inherited parent
  context contract.
- Define inherited shell cwd snapshot for subagent executions so the child's
  `ToolExecutionContext.InheritedCwd` captures the parent's resolved working
  directory at spawn time, and pin down approval-gate behavior for subagent
  invocations under both inherited and null cwd (folder-scoped grants match
  under the parent's cwd; global grants match regardless of cwd, including
  null).

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `netclaw-subagents`: add live definition reload, fail-closed invalid-edit
  behavior, immutable parent-context snapshots, and consistent execution
  semantics across subagent entry points.
- `session-cwd`: define how the session `ProjectDirectory` flows into spawned
  subagent executions and remains read-only from the child.
- `project-instructions`: define inherited project-instruction loading for
  subagent system prompts using the same file precedence as the parent session.
- `skill-execution-routing`: align `metadata.subagent` routing with the same
  reloadable registry and parent-context inheritance behavior as `spawn_agent`.
- `tool-approval-gates`: pin down how the approval gate evaluates subagent
  shell invocations under inherited and null cwd so persisted folder-scoped
  and global grants match consistently with the parent session.

## Impact

- **Runtime wiring**: `FileSubAgentDefinitionLoader`, `SubAgentDefinitionRegistry`
  (or equivalent registry service), `spawn_agent` lookup path, and routed-skill
  dispatch will need a shared reloadable snapshot flow.
- **Delegation context**: the session actor's subagent spawn pipeline will need a
  child execution context that carries parent `session_dir` and `project_dir`
  without widening permissions.
- **Prompt assembly**: subagent prompt construction will need to load project
  identity files from the inherited `project_dir` when present.
- **Security/operations**: invalid subagent edits must fail closed with
  actionable diagnostics; stale definitions must not remain silently active.
- **Docs and skills**: the subagent runbook and `subagent-authoring` guidance
  need to stop instructing operators to restart after every edit and instead
  describe live reload and inherited parent context.
- **Compatibility**: running subagents keep the definition and parent-context
  snapshot captured at spawn time; only subsequent spawns/routed activations see
  reloaded definitions or later parent project changes.

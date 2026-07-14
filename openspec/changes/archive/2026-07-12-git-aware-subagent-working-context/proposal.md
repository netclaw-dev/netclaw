## Why

Netclaw sessions know their project directory and recently used files, but they do not expose the active Git worktree, branch, HEAD, or dirty state to the model. Ephemeral subagents inherit filesystem authority without inheriting or maintaining model-visible working context, so coding delegates can lose track of the files and worktree they are operating on and cannot return a reliable structured change summary to their parent.

This advances PRD-001 FR-006 layered session context and PRD-007 project/environment awareness while preserving Netclaw's default-deny audience filtering and cache-stable prompt assembly.

## What Changes

- Enrich the existing turn-start `[working-context]` block with bounded, credential-safe Git worktree state when `ProjectDirectory` is inside a Git repository.
- Give each subagent an independent run-scoped working context initialized from the parent's project directory and recent-file snapshot.
- Track child file activity from canonical tool metadata and use start/final Git snapshots to report indirect worktree changes without claiming exclusive authorship.
- Return structured child working-context metadata and merge only confirmed child-touched files into the parent's durable recent-file state after successful completion.
- Add targeted multi-turn coding evals that compare behavioral correctness, redundant orientation calls, structured handoff, and cache usage on deterministic linked-worktree fixtures.

In scope for MVP: main turn-boundary snapshots, subagent spawn/completion snapshots, linked-worktree awareness, structured handoff, audience filtering, and focused eval coverage.

Out of scope: refresh during an active tool loop, automatic worktree creation per child, exact authorship attribution in a shared worktree, and GitHub PR/issue context.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-cwd`: Working context includes derived, turn-boundary Git worktree state without persisting that volatile state.
- `netclaw-subagents`: Subagents inherit a read-only parent snapshot, maintain run-scoped file context, and return structured working-context results.
- `audience-context-filtering`: Git paths and repository state follow the same Public suppression rule as working context.
- `netclaw-testing`: The behavioral eval harness supports deterministic, fixture-backed multi-turn coding-context cases.

## Impact

- Session and subagent actor prompt assembly, subagent spawn/result protocol, tool-result file tracking, and daemon dependency registration.
- Actor protocol serialization compatibility: new result metadata is optional for older messages and does not alter durable session event shapes.
- A bounded local `git` subprocess is added at eligible context boundaries; non-Git projects emit no Git section, while inspection failures are explicit and observable.
- No configuration schema changes and no expansion of tool/file authority.
- Operationally, remote URLs are not emitted and Public turns receive no internal working/Git context.

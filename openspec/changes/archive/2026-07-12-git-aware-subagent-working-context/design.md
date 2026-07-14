## Context

The durable main-session `WorkingContext` owns `ProjectDirectory` and `RecentFiles`. `LlmSessionActor` renders it once at turn start into a volatile history nudge so subsequent turns extend the byte-stable prompt prefix. `SubAgentActor` uses a separate ephemeral prompt/tool loop: it inherits project/cwd authority but neither runs `SessionMessageAssembler` nor owns file context.

Git state is volatile process-derived data. Persisting it would create stale state, while running Git inside the synchronous assembler would mix I/O into a pure cache-layout component. Subagent results cross an actor/tool boundary and must use framework-owned, serialization-safe types.

## Goals / Non-Goals

**Goals:**

- Produce one audience-filtered working-context snapshot implementation for main and child agents.
- Preserve durable parent ownership and cache-stable tail insertion.
- Track confirmed child file activity separately from worktree changes merely observed during the run.
- Keep Git inspection bounded, credential-safe, linked-worktree-aware, and explicit on failure.
- Provide deterministic contract tests and focused multi-turn behavioral evals.

**Non-Goals:**

- Refreshing context between tool-loop calls.
- Automatically creating isolated worktrees.
- Proving authorship from shared-worktree status changes.
- Persisting subagent state or Git snapshots.

## Decisions

### Shared snapshot service, pure rendering

Add a working-context snapshot service that accepts audience, project directory, and recent files and returns an immutable snapshot. It performs conditional, strictly time-bounded Git inspection at the existing synchronous turn boundary before prompt assembly. Rendering remains pure and produces one `[working-context]` block with a nested `git:` section.

Alternative: extend `IContextLayerProvider` with session state. Rejected because subagents do not use that pipeline and the resulting interface would mix process I/O into the assembler. An asynchronous actor continuation was also rejected for v1 because it would add a new reentrancy/state-machine transition to every LLM call; the bounded snapshot preserves the existing actor contract.

### Boundary-only refresh

The main session snapshots at the first LLM call of each new turn. A subagent snapshots at spawn and completion. Earlier history bytes are never rewritten.

Alternative: refresh after Git-mutating tools. Deferred because it adds context messages and invalidation logic inside autonomous tool loops, weakening the cache behavior this pipeline deliberately preserves.

### Git porcelain inspection

Use `git` directly through `ProcessStartInfo.ArgumentList`, never a shell. A bounded porcelain-v2 status command supplies branch, HEAD, upstream, ahead/behind, and file state; separate rev-parse queries resolve the worktree root and common Git directory when required. All invocations share a short cancellation deadline and capture bounded output. Remote URLs are not requested or rendered.

No project directory means no Git section. A successful Git response identifying a non-worktree means no Git section. Missing executable, timeout, permission, or corrupt-repository failures render `git.status: unavailable` with a sanitized reason for Team/Personal and are logged; they do not masquerade as a non-Git directory.

### Independent child context and structured handoff

`RunSubAgent` carries a copy of the parent's recent files in addition to existing project/cwd fields. `SubAgentActor` owns an ephemeral context, updates it from the same canonical tool-call path extraction used by the parent, and captures final Git state. `SubAgentResult` gains optional framework-owned working-context metadata.

Confirmed files come from first-party file-tool semantics. Git start/final differences are `ObservedFiles` because concurrent actors can share the worktree. The parent merges confirmed files only after successful completion; observed files remain structured evidence but are not silently attributed or merged.

### Compatibility

New spawn/result members are optional collection/record members with empty defaults, so existing callers and older serialized messages remain readable. No durable session event or config schema changes are introduced.

## Risks / Trade-offs

- Git status can be slow on pathological repositories → enforce cancellation, bounded output, and one snapshot per boundary.
- A shared worktree can change concurrently → distinguish confirmed from observed and never claim observed authorship.
- Added context consumes tokens → render compact counts/paths and measure uncached tokens plus avoided discovery calls.
- Child completion may fail before handoff → do not merge partial activity into parent durable state; logs retain diagnostic evidence.
- Git may be absent or broken → fail visibly in eligible context rather than silently emitting a clean/non-Git state.

## Migration Plan

Deploy as an additive runtime/protocol change with no configuration migration. Rollback removes the optional child metadata and derived renderer; durable `WorkingContext` remains compatible because its stored shape is unchanged.

## Open Questions

None for v1.

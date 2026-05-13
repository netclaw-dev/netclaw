## Context

Subagents are currently described as file-defined markdown documents under
`~/.netclaw/agents/*.md`, but operator guidance still says to restart the daemon
after editing them. That means the runtime behaves like a startup-only loader
even though the user-facing authoring loop wants live updates. At the same time,
the current session-context planning work gives the main session an explicit
`session_dir`, a persisted `ProjectDirectory`, and project-instruction loading,
but there is no matching contract saying delegated subagents inherit that same
grounding.

This leaves three planning gaps:

1. There is no defined reload boundary for file-defined subagents.
2. There is no defined fail-closed behavior for invalid edits during live reload.
3. There is no defined parent-context inheritance contract for spawned or routed
   subagents.

The active `subagent-explicit-model-selection` change already refers to startup
or reload-boundary validation, so this change needs to define that boundary in a
way later subagent changes can plug into.

## Goals / Non-Goals

**Goals:**

- Pick up subagent definition add/update/delete changes without daemon restart.
- Keep the implementation simple by using an explicit reload boundary rather than
  long-lived hidden state.
- Fail closed when an edited definition is no longer valid; do not keep serving a
  stale definition after the source file has become invalid.
- Ensure delegated subagents inherit the parent session's `session_dir` and
  current `project_dir` as a spawn-time snapshot.
- Ensure delegated subagents load project instructions from the inherited
  `project_dir` using the same file precedence as the parent session.
- Keep `spawn_agent` and `metadata.subagent` routed activations on one shared
  loading and inheritance contract.

**Non-Goals:**

- Hot-reloading model-provider configuration, tool grants, or other
  `netclaw.json`-backed settings that already require restart-driven recovery.
- Turning subagents into persistent child sessions with independent working
  directories or durable state.
- Letting a subagent mutate the parent session's `ProjectDirectory` or other
  `WorkingContext` state.
- Retroactively changing the definition, prompt, or project context of a subagent
  that is already running.
- Adding a separate interactive UI for subagent management.

## Decisions

### D1. Reload subagent definitions on demand before lookup

**Decision:** The runtime reload boundary is on demand, immediately before
subagent-registry lookup for `spawn_agent` and `metadata.subagent` routed
execution. The registry tracks the last successful directory fingerprint/mtime
state for `~/.netclaw/agents` and reloads only when that state has changed.

**Rationale:** Subagent definitions are only needed when a subagent is about to
run. An on-demand reload keeps the design simpler than a background watcher,
avoids extra concurrency surface, and still gives operators live-reload behavior
for the next activation.

**Alternatives considered:**

- Background `FileSystemWatcher` with push reload. Rejected for MVP because the
  registry is not latency-sensitive enough to justify the extra complexity and
  race surface.
- Startup-only loading. Rejected because it keeps the current slow authoring loop
  and leaves the reload boundary undefined.

### D2. Reload rebuilds the active snapshot from disk and drops invalid edits

**Decision:** When reload is triggered, the runtime rebuilds the active
definition snapshot from disk using the same loader rules as startup. Valid
definitions enter the new snapshot; invalid, duplicate, or now-disallowed
definitions are excluded from the new snapshot and emit deterministic
diagnostics. The runtime SHALL NOT keep serving the prior version of a file that
no longer loads successfully.

**Rationale:** Keeping a stale last-known-good definition after the operator has
changed the source file is a silent fallback. Excluding the invalid definition is
fail-closed and matches the repo's operational posture.

**Alternatives considered:**

- Per-file stale fallback to the old definition. Rejected because it hides the
  fact that the source on disk and the active registry have diverged.
- Fail the entire registry reload when any file is invalid. Rejected because the
  current startup/load behavior already treats definitions independently and we do
  not want one bad file to hide unrelated valid changes.

### D3. Running subagents keep an immutable definition snapshot

**Decision:** Once a subagent starts, it keeps the resolved definition, tool set,
model-selection inputs, and inherited parent-context snapshot for the duration of
that run. Reloaded definitions affect only future activations.

**Rationale:** Mid-run mutation would make subagent behavior nondeterministic and
hard to debug. Spawn-time snapshotting keeps actor execution stable.

### D4. Subagents inherit parent `session_dir` and `project_dir` as read-only context

**Decision:** Every subagent execution receives an immutable parent-context
snapshot containing the parent session identifier, parent `session_dir`, and the
parent's current `WorkingContext.ProjectDirectory` when set.

The snapshot is read-only from the child. Subagent execution may use it for
prompt assembly, tool path/token resolution, and filesystem grounding, but it
does not let the child mutate the parent session's `WorkingContext`.

**Rationale:** Delegated work should start from the same grounded workspace as
the parent session without forcing the caller to restate it manually. Making the
snapshot read-only preserves session ownership and avoids hidden side effects.

### D5. Subagents load project instructions from the inherited project directory

**Decision:** When the inherited parent-context snapshot includes a non-null
`project_dir`, subagent prompt assembly uses that directory to resolve project
identity files with the same precedence as the parent session:

1. `.netclaw/AGENTS.md`
2. `CLAUDE.md`
3. `AGENTS.md`
4. `CONTEXT.md`

The resulting project instructions are included in the subagent's system prompt.
If no project directory is present, no project instructions are added.

**Rationale:** The delegated worker should receive the same project rules and
constraints the parent session is operating under. Requiring the caller to pass
this in ad hoc `context` text is redundant and brittle.

### D6. Parent project changes affect future subagents only

**Decision:** If the parent session changes `ProjectDirectory`, the new value is
used for subagents spawned after that change. Any subagent already running keeps
the prior spawn-time snapshot.

**Rationale:** This matches the immutable-run contract and avoids in-flight prompt
or tool-grounding drift.

### D7. Routed skill execution uses the same contracts as `spawn_agent`

**Decision:** `metadata.subagent` routing uses the same reloadable registry
lookup, same definition snapshot semantics, and same parent-context inheritance
contract as explicit `spawn_agent` execution.

**Rationale:** Routed skills are another entry point into subagent execution, not
an alternate subagent model. Keeping one contract avoids divergence between skill
routing and explicit delegation.

## Risks / Trade-offs

- **[Risk]** Reload-before-lookup adds filesystem I/O to subagent spawn and routed
  skill execution. -> **Mitigation:** only probe reload state when the definitions
  directory fingerprint/mtime changes; unchanged requests continue using the last
  successful snapshot.
- **[Risk]** Operators may be surprised that an invalid edit removes the active
  definition immediately. -> **Mitigation:** emit explicit diagnostics and update
  runbooks/skills to describe the fail-closed behavior.
- **[Risk]** Context inheritance could accidentally broaden child filesystem
  behavior. -> **Mitigation:** inherited directories are grounding inputs only;
  child tool authorization and file-root policy remain bounded by the parent
  session's existing audience and tool policy.
- **[Trade-off]** On-demand reload is not instantaneous in the absence of a new
  activation. -> **Mitigation:** this is acceptable because subagent definitions
  only matter when a subagent is about to run, and the design stays much simpler
  than a background watcher.

## Migration Plan

1. Introduce a reloadable subagent registry snapshot and switch `spawn_agent`
   lookups to use it.
2. Reuse the same lookup path for `metadata.subagent` routing.
3. Add a parent-context snapshot object for subagent execution and wire it from
   the session actor.
4. Update subagent prompt assembly to use inherited `project_dir` for project
   instructions.
5. Update operator guidance (`docs/runbooks/subagents.md`,
   `feeds/skills/.system/files/subagent-authoring/SKILL.md`, and any related
   routing guidance) to reflect live reload and inherited parent context.

Rollback is straightforward: remove reload-before-lookup and return to
startup-only subagent loading, while keeping the old documentation in sync.

## Open Questions

- None for the change-plan phase. Follow-on implementation work can decide the
  exact fingerprinting mechanism (directory mtime, per-file hash, or equivalent)
  so long as the observable reload and fail-closed contracts remain intact.

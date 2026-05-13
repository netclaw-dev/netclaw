## 1. Reloadable subagent registry

- [x] 1.1 Introduce a reloadable definition-registry path for `~/.netclaw/agents/*.md` that detects directory changes before `spawn_agent` and `metadata.subagent` lookups.
- [x] 1.2 Rebuild registry snapshots from disk on reload using the same validation/loader rules as startup.
- [x] 1.3 Ensure invalid, duplicate, or no-longer-loadable definitions are excluded from the active snapshot and surface deterministic diagnostics instead of leaving stale definitions active.
- [x] 1.4 Add tests covering add, update, delete, and invalid-edit reload behavior without daemon restart.

## 2. Shared lookup alignment across subagent entry points

- [x] 2.1 Update explicit `spawn_agent` lookup to use the reloadable registry snapshot.
- [x] 2.2 Update `metadata.subagent` routed execution to use the same reloadable registry snapshot and failure semantics.
- [x] 2.3 Add tests proving explicit delegation and routed skill execution both pick up reloaded definitions on the next activation.

## 3. Parent-context snapshot for subagent execution

- [x] 3.1 Introduce a subagent execution-context snapshot carrying parent session id, `session_dir`, and current `WorkingContext.ProjectDirectory`.
- [x] 3.2 Wire the parent-context snapshot into spawned and routed subagent execution paths.
- [x] 3.3 Ensure child execution treats inherited `session_dir` and `project_dir` as read-only context and does not mutate parent `WorkingContext` state.
- [x] 3.4 Add tests covering parent project unset, parent project set, and parent project changed between two subagent runs.

## 4. Inherited project instructions for subagents

- [x] 4.1 Update subagent prompt assembly to load project identity files from inherited `project_dir` using the same precedence as the parent session.
- [x] 4.2 Ensure no project instructions are added when the inherited parent context has no `project_dir`.
- [x] 4.3 Add tests proving spawned subagents receive inherited project instructions and that running subagents keep their spawn-time snapshot after later parent project changes.

## 5. Documentation and system guidance

- [x] 5.1 Update `docs/runbooks/subagents.md` to replace restart-required authoring guidance with live-reload guidance and fail-closed invalid-edit behavior.
- [x] 5.2 Update `feeds/skills/.system/files/subagent-authoring/SKILL.md` to describe live reload, inherited parent context, and the new verification workflow.
- [x] 5.3 Update any relevant skill-routing guidance so `metadata.subagent` documentation matches the shared reload and inheritance contract.
- [x] 5.4 Run the eval suite if system skill content changes as part of the implementation PR.

## 6. Verification and OpenSpec completion

- [x] 6.1 Run targeted tests for subagent loading, routed skill execution, prompt assembly, and parent-context inheritance.
- [x] 6.2 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 6.3 Run `openspec validate align-subagent-loading-and-parent-context`.
- [ ] 6.4 `/opsx-verify align-subagent-loading-and-parent-context` after implementation lands.
- [ ] 6.5 `/opsx-sync align-subagent-loading-and-parent-context` to merge the deltas into the main specs.
- [ ] 6.6 `/opsx-archive align-subagent-loading-and-parent-context` after merge.

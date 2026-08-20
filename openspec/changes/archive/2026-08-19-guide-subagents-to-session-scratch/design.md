## Context

Parent sessions render a `[session]` context block that names `session_dir` as private scratch for Personal and Team audiences. A spawned child already receives the same directory in `ChildRunScope.Authority.Session`, and shell execution already uses it as a safe-space root and fallback cwd. `SubAgentActor` builds the child's model-visible working context from `WorkingContextSnapshot`, which contains project and shell facts but no session directory. The model therefore cannot choose the private scratch path until an eligible interactive correction happens, and dynamic temporary work never reaches that correction.

The active `redirect-shared-temp-to-session-scratch` change already requires Personal and Team headless guidance. Its current eval exercises a parent session and explicitly tells the model to use session scratch, so it cannot detect the child-context omission observed in post-0.26.0 live traffic.

## Goals / Non-Goals

**Goals:**

- Give Personal and Team subagents the exact private session scratch path before their first model call.
- Keep the path in volatile per-run context rather than static agent identity.
- Preserve Public redaction, existing shell authority, and explicit platform-temp intent.
- Add deterministic prompt coverage and a non-tautological delegated eval.
- Reuse the child scope Netclaw already owns without adding protocol or persistence state.

**Non-Goals:**

- Auto-approve dynamic or complex shell calls.
- Rewrite authored commands or working directories.
- Make the shared platform temporary root trusted.
- Change session-directory layout, lifetime, or cleanup.
- Add `session_dir` to `SubAgentDefinition` or any public API.

## Decisions

### Render scratch context at the child actor boundary

`SubAgentActor` will derive a small `[session]` block from the bound `ToolExecutionContext.SessionDirectory` and audience when it builds the initial user message. The block will contain the exact `session_dir` and one short instruction:

`For disposable shell work, always set WorkingDirectory to session_dir unless the task explicitly requires another directory.`

The actor already owns the authoritative child scope at this point. Rendering there avoids copying a runtime path into the public `SubAgentDefinition` record, spawn profile data, or persisted actor protocol.

An alternative was adding the directory to `WorkingContextSnapshot`. That type represents project, shell, Git, and recent-file state and is also used outside subagents; changing it would mix session identity into a reusable project snapshot and broaden the public surface.

### Keep volatile path context out of the system prompt

The session block will join the existing runtime and working-context parts of the child's initial user message. The static system prompt remains reproducible across runs and project instruction refreshes. `set_working_directory` changes only project scope, so it does not need to rebuild the unchanged session block.

### Disclose only to Personal and Team audiences

The renderer will return no session block for Public subagents. This mirrors parent-session audience filtering and the current child behavior that omits the complete working-context block for Public. The child already holds a bound session directory for execution bookkeeping; this change controls model visibility only.

### Guidance changes selection, not authority

The prompt names an existing safe root but does not grant coverage. Every authored shell call still traverses hard deny, path policy, syntax analysis, reviewed-safe coverage, stored grants, and headless authority rules. An explicitly requested `/tmp` or native Windows temporary path remains unchanged. The existing interactive correction and unchanged-retry logic remain intact.

### Make delegated choice observable in evals

A new fixture subagent will receive a task requesting disposable multi-command diagnostic work without naming `session_dir`, `/tmp`, a cwd, or `set_working_directory`. It will run two non-mutating diagnostic commands so the eval measures path selection without introducing an unrelated headless-write grant. The assertion will derive the bound session directory from the response session ID and require each child `shell_execute` call to pass that exact path as `WorkingDirectory`. It will reject omission and `/tmp`, require successful child completion, and verify the expected result. The parent-only eval will also stop prescribing the answer.

Deterministic actor tests remain the contract proof for exact prompt assembly and Public redaction. The model eval measures alignment only; it does not claim to exercise interactive approval.

## Risks / Trade-offs

- **The model still chooses `/tmp`.** → Keep the existing strict policy and correction behavior; use eval results to decide whether wording needs refinement.
- **A private path leaks to Public.** → Build the block only after an explicit Personal/Team audience check and add exact Public prompt tests.
- **Guidance is mistaken for a grant.** → Change no policy input or authorization state and assert headless shell calls still require existing authority.
- **The eval passes because the parent supplies the answer.** → Reject scratch, temp, cwd, and declaration hints in the parent-authored child task and inspect exact child tool arguments.
- **Prompt text drifts between parent and child.** → Keep the normative meaning aligned in tests; a later refactor may consolidate rendering after the behavior is pinned.

## Migration Plan

Deploy as a prompt-context-only addition. No stored session, approval, or agent definition requires migration. Rollback removes the child-visible session block and delegated eval while leaving execution scope and persisted data unchanged.

## Open Questions

None for this slice. Automated session cleanup remains a separate future change.

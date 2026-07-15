## 1. Stage 1 — Required Execution Scope

- [x] 1.1 Inventory the current tool execution call graph, nullable/default dependencies, mutable state owners, persisted message boundaries, and MCP schema consumers; record the migration map in the design or implementation notes.
- [x] 1.2 Introduce validated semantic value objects for execution limits and the immutable admitted `ToolRunScope`, with no implicit primitive conversions.
- [x] 1.3 Replace production `ToolExecutionContext.Empty`, context-free overloads, and nullable authority/security dependencies with required immutable invocation APIs; keep any temporary friend-only test fixture out of production and remove it after test migration.
- [x] 1.4 Split mutable tool outputs into a per-invocation append-only sink and approval retry/match state into a pipeline-owned attempt object while sharing only immutable run authority across a batch.
- [x] 1.5 Add focused tests proving invalid scope values fail before dispatch, missing authority has no dispatch path, and parallel calls cannot observe each other's mutable state.
- [x] 1.6 Update affected engineering documentation; review the mapped `netclaw-operations` system skill and leave it unchanged because the internal refactor must not alter model-visible guidance.
- [x] 1.7 Run targeted tests, tool-related evals/full eval suite as required, `dotnet test`, Slopwatch, file-header verification, and `git diff --check`; open and babysit Stage 1 through review, CI, merge, and post-merge `dev` verification.

## 2. Stage 2 — Composed Session Pipeline

- [x] 2.1 Replace the broad session tool-call parameter list with a cohesive batch command and a composed `SessionToolExecutionPipeline` whose production dependencies are required.
- [x] 2.2 Trace each nullable pipeline service through every intended production composition path; make proven-unconditional services required, model genuinely production-reachable absence explicitly with unchanged behavior, and keep test-only fixture states out of the production API.
- [x] 2.3 Preserve existing `_background` behavior for shell, non-shell, missing-manager, and dispatch-failure paths while removing redundant parameter plumbing.
- [x] 2.4 Add characterization tests for audit/logging/approval/background infrastructure, malformed metadata, ACL and approval denial, supported background routing, missing-manager fallback, dispatch failure, and non-shell fallback.
- [x] 2.5 Verify MCP request/response schemas and persisted actor contracts remain compatible; update affected engineering docs and specs, and review the versioned `netclaw-operations` system skill without changing model-visible guidance for an internal behavior-preserving refactor.
- [x] 2.6 Run targeted tests, the tool-definition eval suite, `dotnet test`, Slopwatch, file-header verification, and `git diff --check`; open and babysit Stage 2 through review, CI, merge, and post-merge `dev` verification.

## 3. Stage 3 — Child Context and Async Git

- [x] 3.1 Introduce a framework-owned child-run scope that forks immutable parent authority and a read-only working-context snapshot while allocating independent child activity state.
- [x] 3.2 Introduce typed child outcomes and working-context deltas; merge only confirmed changed files from successful children and keep Git-observed changes non-attributed.
- [x] 3.3 Split Git process execution, snapshot composition, and rendering; make inspection asynchronous with explicit available, not-repository, and unavailable outcomes.
- [x] 3.4 Gate Git inspection before process launch so it occurs only for Team/Personal runs with a declared project directory that Git recognizes as a worktree.
- [x] 3.5 Correlate session-actor Git continuations with turn generations and discard stale results; await bounded spawn/completion snapshots at subagent async boundaries.
- [x] 3.6 Add deterministic tests for Public no-inspection, missing/non-Git project handling, sanitized failures, stale continuation rejection, child isolation, successful confirmed merge, and failed/cancelled no-merge without sleeps.
- [x] 3.7 Update engineering docs and the versioned `netclaw-operations` and `netclaw-projects` system skills for working-context and subagent behavior.
- [x] 3.8 Run targeted tests, prompt/tool eval suites when provider credentials are available (otherwise record the explicit environment block), `dotnet test`, Slopwatch, file-header verification, and `git diff --check`; open and babysit Stage 3 through review, CI, merge, and post-merge verification, then close GitHub issue #1633 with the acceptance evidence. Stage 3 merged as PR #1644, all GitHub Actions checks passed, fresh-worktree verification passed, issue #1633 was closed with evidence, and local eval execution was explicitly blocked by absent `NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, and `NETCLAW_EVAL_MODEL_ID`.

## 4. Closeout

- [x] 4.1 Verify all three merged stages against the OpenSpec scenarios and PRD traceability from the merged Stage 3 baseline, including serialization/recovery and MCP compatibility evidence. The full solution passed 5,884 tests; the focused serialization, recovery, MCP adapter/schema, session integration, and subagent integration set passed 183 tests; source inspection confirms volatile run scopes, child scopes, approval capabilities, and Git snapshots are absent from persisted event and MCP payload types.
- [x] 4.2 Sync the delta specs to main specs with `/opsx-sync`, run `/opsx-verify`, and archive the completed change with `/opsx-archive`. Eleven added or modified requirement blocks matched their synced main-spec counterparts, the obsolete audit requirement was removed, all 69 OpenSpec items passed strict validation, and the completed change was archived as `2026-07-15-simplify-tool-execution-context`.
- [x] 4.3 Run the RALPH adversarial output review, diagnostics, and after-action workflow; capture durable follow-ups without leaving undocumented behavior drift. The independent review moved from HOLD to MERGE after all five findings were repaired. The after-action output-quality verdict is PASS; diagnostics are PARTIAL because original per-iteration RALPH logs were not preserved, with Git/PR/OpenSpec/test evidence retained as the durable source of truth.
- [x] 4.4 Remove `IToolAuditLogger`: production registered only `NullToolAuditLogger`, while `ToolCallOutput` and `ToolResultOutput` already flow through the canonical `SessionLogActor` transcript. The active delta removes the obsolete structured-audit requirement without changing shipped output behavior.

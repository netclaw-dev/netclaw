## Context

The session-owned tool path currently combines admitted-turn authority, mutable working state, tool-call metadata, optional capabilities, approval state, logging, and subagent handoff in a broad parameter surface. Several dependencies are nullable even though production always supplies them, and `ToolExecutionContext.Empty` permits execution without an admitted turn. This makes trust-boundary omissions compile, increases branching, and lets parallel calls accidentally share mutable state.

PR #1630 made Git worktree context useful, but also exposed a second conflation: prompt snapshot composition, Git process execution, rendering, and child-run state are coupled. Netclaw is a single executable; MCP is the extension boundary, so internal C# source compatibility is not a constraint. Existing MCP schemas and persisted actor contracts remain constraints.

## Goals / Non-Goals

**Goals:**

- Make admitted turn authority and tool-session infrastructure required by construction.
- Replace primitive-heavy, nullable parameter lists with cohesive immutable scopes, semantic value objects, and required infrastructure.
- Give every tool call fresh mutable execution state while sharing only immutable run scope.
- Let subagents fork a parent snapshot, evolve independently, and return a typed delta that the parent merges only after successful completion.
- Inspect Git asynchronously only for non-Public turns with a declared project directory that Git identifies as a worktree.
- Preserve MCP tool contracts, persisted wire contracts, tool selection, dispatch mode, fallback behavior, authorization and approval outcomes, and all model-visible behavior delivered by PR #1630.

**Non-goals:**

- Adding third-party in-process tool authoring APIs.
- Changing MCP schemas or introducing new configuration.
- Persisting volatile Git snapshots or child execution scopes.
- Redesigning the background job actor or the approval user experience.

## Decisions

### Migration inventory

The Stage 1 inventory on `dev` at `8b654482` identified these authoritative seams:

- `INetclawTool` and `NetclawTool<TParams>` expose both context-free and context-aware execution. The base class routes the former through `ToolExecutionContext.Empty`.
- `DispatchingToolExecutor` accepts nullable context in execute, stream, authorization, policy, and output-spill paths, choosing between overloads at runtime.
- `ToolAccessPolicy`, `ScopedShellSafeVerbPolicy`, and `IMcpToolInvoker` accept nullable context and infer audience or safe roots when it is absent.
- `SessionToolExecutionPipeline.ExecuteToolsAsync` accepts 26 parameters, including nullable source, audit, approval, logging, background dispatch, working context, and admitted `TurnContext`; it constructs a mutable context inside each call.
- Production creates tool contexts in the main session pipeline, direct session execution, subagents, and the reminder HTTP route. These are the migration roots; tests and MCP adapter tests are consumers, not justification for compatibility overloads.
- The initial search found 29 source/test files referencing `ToolExecutionContext.Empty`, 20 files declaring nullable context signatures, and four pipeline invocation sites. Compilation after each seam change is the authoritative completeness check.

The migration order is abstraction contract, dispatcher/policies, production roots, concrete tools, then tests. Existing `SessionId`, `TurnId`, `ToolName`, and related value objects are reused. New scalar value objects are limited to execution limits that currently cross the seam as raw `int` or `TimeSpan`.

### Required immutable run scope and fresh call context

`ToolRunScope` is an immutable value assembled only after audience admission. It groups the resolved audience, explicit bound/unbound session identity, channel and delivery authority, workspace/project roots, model modalities, child-spawn capability, and typed output limits. Per-call timeout remains on the invocation because metadata can select a different clamped timeout for each call. Semantic scalar values use validated value objects with explicit `.Value` access and no implicit primitive conversions.

`ToolInvocationContext` will be created afresh for each invocation from the run scope and normalized tool arguments. It is an immutable description of the call: run authority, call identity, validated timeout, resolved working directory, and tool-visible services. Context-free overloads and `ToolExecutionContext.Empty` will be removed; all production call sites migrate in the same staged series.

Mutable data is not stored as replaceable context properties. Tool-produced attachments, model inputs, and activity flow into a separate per-call append-only `ToolExecutionOutputs` sink. Approval grants, match results, and retry state remain owned by the pipeline in a dedicated `ToolApprovalAttempt`; tools receive only the `ToolInvocationContext` base contract and cannot access approval state.

`ToolInvocationContext` deliberately retains reference identity even though its data is construction-only. A record's generated `with` copy would shallow-copy the output sink and make two apparent invocation values share mutable products. Record/value semantics are therefore limited to immutable values such as `ToolRunScope`, `ToolSessionScope`, `ToolExecutionTimeout`, and `InlineOutputBudget`; the append-only sink, invocation wrapper, and stateful approval attempt remain classes.

### Composed session execution pipeline

The session actor will invoke a composed pipeline with required constructor dependencies for authorization, approval, audit, logging, and dispatch. A batch command carries the run scope and calls instead of threading the current broad parameter list through helpers. Dependencies that are always constructed in production are non-nullable and required.

Before making any pipeline dependency required, the implementation will prove from the composition root whether every intended production path constructs it unconditionally. Background dispatch is not assumed to satisfy that test merely because it ships in the executable: if an intended production mode executes without a manager, that state will be represented explicitly and retain its current synchronous behavior. Test fixtures and direct unit invocation do not make a state production-reachable and will use explicit test-only construction instead of weakening the production contract. This refactor does not change non-shell background handling, job-creation failure handling, or any model-visible result. Security infrastructure already proven unconditional remains required and fail-closed.

### Child-run fork and typed merge

A subagent receives a framework-owned local `ChildRunScope` copied from the parent's immutable authority and a read-only working-context snapshot. The child owns a new `FileActivityTracker` and may change its own working state without mutating the parent.

Completion returns `ChildRunOutcome` plus a typed `WorkingContextDelta`. Only successful outcomes merge confirmed first-party changed files into the parent's durable recent-file context. Git-observed changes remain observational and are not attributed to the child. Failure and cancellation merge nothing.

These local actor messages are serialization-safe framework types but are not external wire contracts. Existing persisted events and MCP payloads are unchanged.

### Async Git inspection behind eligibility gates

`WorkingContextSnapshotProvider` first checks audience and project-directory eligibility. Public turns never invoke Git inspection. Missing project directories yield ordinary non-Git working context. For eligible directories, `IGitWorkingContextInspector.InspectAsync` returns an explicit result: available snapshot, not a repository, or unavailable with a sanitized reason.

The inspector owns process execution and timeouts; composition owns eligibility; rendering is pure. Session actors correlate async inspection continuations with the active turn generation and discard stale results. Subagent spawn and completion await bounded inspections at their natural async boundaries.

### Staged delivery

The work ships as three sequential PRs:

1. internal execution scope, value objects, required dependencies, and per-call isolation;
2. composed session pipeline with behavior-preserving dependency modeling;
3. child-run fork/delta merge and gated asynchronous Git inspection.

Each stage updates its tests and specs and lands only after CI, review, and post-merge `dev` verification. The durable task list lives in this OpenSpec change and `IMPLEMENTATION_PLAN.md`; RALPH flight recorders contain per-run evidence.

## Risks / Trade-offs

- **Large source-breaking migration:** staged PRs keep each review bounded, but intermediate adapters could preserve the smell. We will migrate all internal call sites rather than add compatibility overloads.
- **Async actor races:** generation correlation and explicit stale-result tests prevent a prior turn's Git result from entering a later prompt.
- **More types:** semantic value objects and typed outcomes add declarations, but remove invalid states and repeated branches. Closely related types stay grouped where that improves the call chain.
- **Accidental behavior drift:** replacing nullable dependencies can subtly alter fallback paths. Characterization tests lock current routing and results before the constructor surface changes.
- **Child attribution ambiguity:** Git snapshots cannot prove authorship. The delta keeps confirmed and observed changes separate and only merges confirmed successful activity.

## Failure / Recovery

Invalid scope construction and missing infrastructure already required by the production composition root fail loudly before dispatch. Existing background and metadata behavior is preserved exactly. Git process failures retain their current sanitized unavailable result for eligible internal audiences. A failed or cancelled child retains its current no-merge behavior. No new durable schema is introduced, so rollback consists of reverting the relevant stage without data migration.

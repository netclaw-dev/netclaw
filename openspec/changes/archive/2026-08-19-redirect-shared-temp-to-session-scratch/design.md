## Context

`ShellTool` already resolves a missing working directory to the declared project directory and then to the per-session directory. The per-session directory is already a Personal, Team, and Public safe-space root and is the existing scratch location described by PRD-002 SEC-009 and PRD-007. Agents nevertheless author an explicit operating-system temporary root, either through `WorkingDirectory` or a static leading directory change such as `cd /tmp`, and the approval layer correctly treats that foreign location as outside session scope.

PR #1890 introduces typed agent corrections before the user approval surface. This change adds a separate correction for platform-temp intent. It spans the shell policy, parent session pipeline, and subagent approval bridge, but it changes neither execution semantics nor persisted grants.

## Goals / Non-Goals

**Goals:**

- Identify a statically proved shell execution scope equal to the platform temporary root.
- Offer the existing session directory as the private scratch alternative before a user or parent approval request.
- Preserve the original call and correction in model history.
- Let an exact unchanged retry proceed to ordinary one-time approval rather than loop on the correction.
- Apply identical semantics to parent agents and subagents.
- Keep the implementation general and shell-fact driven, with no executable-specific parsing.

**Non-Goals:**

- Trust, declare, or persist authority over the platform temporary root.
- Rewrite the command, `WorkingDirectory`, inline `cd`, redirect, or arguments.
- Infer executable-private file behavior.
- Auto-approve work merely because it moves to session scratch.
- Add a new scratch subdirectory or change the existing session-directory layout.
- Purge, retain, compact, or otherwise manage old session directories. Automated cleanup requires a later OpenSpec change.

## Decisions

### Use the existing session directory

The correction names `ToolInvocationContext.SessionDirectory`. It does not create `{session_dir}/scratch` because the session directory already owns scratch, inbox, media, and bounded tool-output artifacts and already participates in safe-space checks.

An alternative was a new nested scratch directory. That would add path creation, prompt/context plumbing, migration, and future cleanup ambiguity without improving containment.

### Compare only statically proved effective directories

The policy recognizes an exact, normalized platform temporary root supplied by the host and a statically resolved shell scope. Bash can use Netclaw's existing causal-directory analysis, so static `WorkingDirectory=/tmp` and `cd /tmp && ...` can qualify. Native PowerShell qualifies only through the typed `WorkingDirectory`; `Set-Location`, `cd`, and other PowerShell causal directory mutations remain strict in this slice. The policy does not guess dynamic variables, aliases, symlink destinations, alternate branches, or executable-private path meanings. Dynamic `cd "$TMPDIR"` remains on the normal approval path.

An internal immutable platform-temp policy captures the root once when `ToolAccessPolicy` is constructed, using the resolved shell environment's path style. This avoids changing the public `ShellExecutionEnvironment` API while ensuring one authorization attempt cannot observe different environment values. Path comparison follows the platform shell/path rules.

### Correction is advice, not authority

Hard deny and protected-path checks run first. The correction executes nothing, records no grant, and changes no working context. It is eligible only for Personal audiences, which preserves the existing rule that rejects Team and Public shell execution before approval policy. Every executable occurrence whose scope matters must resolve to the platform temporary root, no authored absolute path may escape that root, the session directory must be a valid nonempty normalized path, and the ordinary policy result must otherwise request approval. The directory need not exist yet: replacement execution already owns its creation. Public callers retain existing path redaction and never receive a private session path.

Platform-temp classification runs before the undeclared-project correction. The project correction explicitly excludes the platform temporary root, so one call can produce at most one correction and Netclaw never recommends `set_working_directory` for that root.

The correction tells the agent to author a replacement call with the session directory. It does not promise that the replacement will auto-run; the replacement receives the complete ordinary authorization pass. This preserves strict behavior for network calls, writing redirects, dynamic values, and mutating commands.

The correction requires an interactive approval capability because its unchanged-retry fallback routes to ordinary one-time approval. Headless, scheduled, webhook, and benchmark execution retain their existing noninteractive authorization result and never receive this correction.

The headless eval harness therefore cannot validate the interactive correction pipeline. Deterministic actor integration tests own that proof. Personal and Team headless model evals validate a working-context nudge only: ordinary disposable work should use the announced session directory, while a prompt that explicitly requires the platform temporary directory must preserve that path and continue under existing noninteractive policy. Public headless context retains path redaction.

### An unchanged retry means intentional platform-temp use

The parent session actor or subagent actor owns an in-memory correction key over the complete cleaned execution semantics: canonical shell, command text, explicit working-directory presence and value, resolved temporary scope, background mode, and timeout. Rationale is deliberately excluded because it changes audit explanation rather than execution. The actor arms the key only after the correction tool result has been committed to model history. Parallel identical calls in the original model batch are therefore all first attempts and all receive corrections; none can observe another call's uncommitted result.

If a later tool iteration in the same user turn submits an equivalent call, the actor consumes the armed key, suppresses the repeated correction, and routes that one retry to an approval context containing exactly `Once` and `Deny`. The key grants no execution. Actors clear keys on turn completion, cancellation, failure, passivation/recovery, or a new user turn. Keys are not written to session persistence or the approval store.

### Share one correction decision across actor boundaries

A pure policy component returns either no correction or a typed `SessionScratchSuggested` result. The parent session consumes it before the user approval requester. The subagent consumes the same result before its parent approval bridge. Rendering may differ by transport, but the directory and correction key are identical.

### Reuse parser-owned Bash transition facts for advice

For Bash, advice-only classification reads the exact leading authored directory transition already represented by ShellSyntaxTree's `IsCwdAttribution` argument and related `CommandOccurrence.WorkingDirectory` projection. It does not require authority-eligible causal approval intent because the correction grants and executes nothing. Actual execution, folder grants, and safe coverage retain the parent change's authority preconditions. Netclaw does not add a second `cd` parser.

### Fail closed across links and reparse points

The captured platform temporary root is resolved to its final filesystem target once at daemon startup, allowing platform-owned aliases without trusting later mutable segments. Every policy-relevant cwd and authored filesystem path must remain beneath that canonical root. A descendant symlink, junction, reparse point, or attribute-inspection failure suppresses the correction. A path under session scratch belongs in a replacement call, not the original platform-temp call.

### Keep cleanup separate

This change neither deletes nor schedules deletion of session data. A future cleanup design must define age calculation, active-session exclusion, background-job ownership, attachment/tool-output retention, crash recovery, observability, and operator overrides before deletion is safe.

## Risks / Trade-offs

- **The agent ignores the recommendation.** → An equivalent unchanged retry falls through to ordinary one-time approval instead of repeating forever.
- **A retry creates reusable temp authority.** → Limit its approval context to exactly Once and Deny and prove no actor/store write occurs.
- **Parallel duplicates race the correction key.** → Arm actor-owned state only after history commit and consume it only in a later tool iteration.
- **A dynamic or aliased temp path is missed.** → Fail closed to normal approval; do not add heuristics.
- **A replacement call remains approval-worthy.** → Run the complete normal policy after replacement and make no auto-approval promise in the correction.
- **Both cwd corrections appear eligible.** → Give platform-temp classification precedence and normatively exclude that root from project declaration.
- **A headless task requires the platform temporary directory.** → Do not emit the correction without interactive approval capability; preserve the existing noninteractive result and every authored `/tmp` path.
- **A headless eval is mistaken for prompt coverage.** → Label headless cases as model-alignment guidance and use actor integration tests for the real correction/approval bridge.
- **A non-Personal caller learns a private path.** → Require Personal audience and test parent/subagent Public redaction without widening the existing Team shell boundary.
- **PowerShell causal scope is accidentally relaxed.** → Accept only the typed Windows `WorkingDirectory`; keep `Set-Location` and aliases strict.
- **A temp descendant escapes through a link.** → Canonicalize the platform root once and reject descendant symlink/reparse paths or inspection failures.
- **The platform temp root changes during daemon lifetime.** → Capture it once in an internal immutable policy value keyed to the resolved shell path style.
- **A subagent reaches the parent bridge first.** → Put the shared correction check before both approval entry points and cover both with end-to-end tests.
- **Session directories accumulate.** → Preserve current behavior and track cleanup as a separate future change rather than introducing undeclared destructive lifecycle work.

## Migration Plan

No persisted data or configuration migration is required. Deploy the new policy and model-facing guidance with PR #1890. Rollback removes only the correction; existing shell approval, one-time approval, and session-directory behavior remain valid.

## Open Questions

None for this slice. Cleanup policy is deliberately deferred.

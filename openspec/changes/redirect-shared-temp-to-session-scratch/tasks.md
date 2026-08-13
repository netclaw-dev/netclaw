## 1. Platform and Policy Facts

- [x] 1.1 Add an internal immutable platform-temp policy constructed alongside `ToolAccessPolicy` from the resolved shell path style, with POSIX and Windows comparison tests and no public API change.
- [x] 1.2 Add a pure typed policy result that identifies eligible platform-temp scope without executing, rewriting, or granting authority.
- [x] 1.3 Reuse ShellSyntaxTree `IsCwdAttribution` and `CommandOccurrence.WorkingDirectory` facts for advice-only Bash causal scope; keep authority preconditions for execution and keep native PowerShell causal scope strict.
- [x] 1.4 Give platform-temp correction precedence and exclude the platform temporary root from undeclared-project correction, with a one-result regression test.
- [x] 1.5 Canonicalize the platform-temp target once and suppress correction for descendant symlinks, junctions, reparse points, or inspection failures.

## 2. Parent and Subagent Corrections

- [x] 2.1 Consume the typed correction before the parent user-approval prompt and render the exact session-directory recommendation.
- [x] 2.2 Consume the same correction before the subagent parent-approval bridge.
- [x] 2.3 Add actor-owned correction keys over cleaned execution semantics that arm only after history commit, consume once in a later iteration, clear at every lifecycle boundary, and restrict retries to Once/Deny without actor/store writes.
- [x] 2.4 Preserve current behavior for headless, scheduled, webhook, benchmark, and other noninteractive execution.
- [x] 2.5 Replace the post-denial `set_working_directory` hint for platform temp with the same session-scratch recommendation and suppress project hints for undeclarable roots.

## 3. Regression and Model Evals

- [x] 3.1 Add deterministic positive tests for POSIX explicit cwd, POSIX causal `cd`, a platform-owned POSIX temp alias with a redirect, and native Windows temporary roots.
- [x] 3.2 Add deterministic negatives for hard deny, protected paths, dynamic identity/flow/cwd, unresolved redirects, mixed incomplete batches, native PowerShell `Set-Location`, inherited/default temp cwd, external authored paths, POSIX symlinks, Windows reparse points, inspection failures, invalid/empty session scope, and Public redaction; add a fresh valid session path that does not exist yet as a positive.
- [x] 3.3 Add parent and subagent end-to-end tests for duplicate first-attempt batches, later once-only consumption, Once/Deny-only retry options, no actor/store grant writes, execution-meta near misses, lifecycle/cancel/recovery reset, unchanged calls, and denial after an intentional retry.
- [x] 3.4 Replace eval cases that teach `set_working_directory("/tmp")` as project scope with the eval-owned configured workspace directory.
- [x] 3.5 Add headless model-alignment evals that prefer the announced session directory for ordinary disposable work and preserve `/tmp` when the task explicitly requires it; do not claim these exercise interactive correction or approval prompts.
- [x] 3.6 Keep PR #1896's stdout/stderr harness work compatible and do not duplicate its capture changes.

## 4. Documentation and Validation

- [x] 4.1 Update working-context and consumer guidance to state that `{session_dir}` is private scratch and that cleanup is not yet automatic.
- [x] 4.2 Run strict OpenSpec validation for this change and the parent structured-approval change.
- [ ] 4.3 Run focused policy, actor, shell-environment, and eval assertion tests on Linux; run native Windows coverage for path and PowerShell behavior.
- [ ] 4.4 Run the full required build, tests, headers, formatting, Slopwatch, and eval gates for every changed system-guidance artifact.
- [x] 4.5 Record automated session-directory cleanup as explicit future scope without implementing deletion in this pull request.

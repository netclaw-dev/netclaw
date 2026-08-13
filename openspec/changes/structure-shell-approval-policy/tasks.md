## 1. Decisions and exact evidence

- [x] 1.1 Approve Netclaw's use of ShellSyntaxTree `AuthoredValue` as an
  approval fact. Effective facts retain runtime semantics.
- [x] 1.2 Keep all v2 grants exact until the user approves a new token-prefix
  grant.
- [x] 1.3 Keep `evidence/approval-matrix.json` byte-identical to the paired
  ShellSyntaxTree artifact.
- [x] 1.4 Add `evidence/netclaw-policy-fixtures.json` with exact structured
  candidates, phrases, scopes, grant and safe inputs, coverage, ordered trace,
  and outcome for D02, D03, D07, D08, D09, D10, D11, D14, D17, and D18; tests
  must load explicit authority defaults and fields, not branch on IDs.
- [ ] 1.5 Add adversarial dynamic identity, redirect, protected path, prefix
  collision, runtime loop, wrapper, provider, and unsafe-catalog cases.
- [x] 1.6 Run the PII audit and manually inspect every command and fixture.

## 2. Typed coordinator and actor protocol

- [x] 2.1 Snapshot immutable preflight facts from existing
  `ToolExecutionContext`, `ToolRunScope`, `ToolApprovalAttempt`, and
  `ShellExecutionEnvironment`; preserve `OneTimeApprovalKeys` exact-set
  semantics and do not add a parallel context or scalar retry key.
- [x] 2.2 Add one coordinator that runs synchronous preflight, sends one actor
  batch request, and completes policy without a second grant scan.
- [x] 2.3 Add `ShellApprovalMatchRequest` and `ShellApprovalMatchResult` to
  `ToolApprovalActor`; match inherited session and persistent snapshots
  atomically, return typed persistent-store status, and leave one-time state in
  `ToolApprovalAttempt`.
- [x] 2.4 Route `DispatchingToolExecutor` through the coordinator without
  changing the original source, argument object, or tool history.
- [ ] 2.5 Preserve session-pipeline pending-request persistence,
  stale/duplicate response rejection and recovery; preserve exact-set one-time
  retry in `ToolApprovalAttempt` and actor-owned subagent scope inheritance.

## 3. Ordered security stages and coverage

- [ ] 3.1 Implement parse validation, hard deny, protected path, approval mode,
  candidate construction, noninteractive trust-zone enforcement, actor match,
  safe policy, exact-set one-time matching, and prompt completion in the
  specified order.
- [x] 3.2 Track coverage per candidate; allow only when all candidates are
  covered and call-level invariants pass.
- [x] 3.3 Make internal exceptions, invalid enums, duplicate candidate IDs,
  mismatched actor results, and impossible transitions terminal deny.
- [x] 3.4 Allow calls covered by one-time or session authority and
  approval-exempt side effects when persistent state is unavailable. Also allow
  reviewed-safe phrase coverage for an interactive run. Deny with
  `ApprovalStoreUnavailable` rather than open a prompt when any candidate still
  depends on persistent state.
- [ ] 3.5 Let expected unresolved shell input offer only one-time approval and
  deny; never create a reusable candidate.
- [ ] 3.6 Keep legacy token scans deny-only and prove they cannot authorize,
  create persistence choices, or widen scope.
- [x] 3.7 Apply reviewed-safe phrase coverage only when interactive approval is
  available. Prove that unattended calls need explicit one-time or stored-grant
  authority while approval-exempt side effects keep their current behavior.

## 4. Typed grant phrases and persistence

- [x] 4.1 Add version-3 token-prefix and legacy-exact approval entry shapes with
  explicit canonical shell tags.
- [x] 4.2 Implement backed-up atomic v2 migration; keep each valid shell entry
  exact and do not add token-prefix authority.
- [x] 4.3 Specify and test absent, v1, malformed, partial-v3, future-version,
  invalid-enum/token, backup-failure, and atomic-replacement-failure behavior;
  internal store failures deny and never salvage partial authority.
- [x] 4.4 Use the same typed phrase comparison for one-time, session,
  persistent, global, and folder coverage.
- [ ] 4.5 Require real exact scope, normalization, containment, and symlink
  checks for folder grants; global grants do not require cwd.
- [ ] 4.6 Preserve prompt display/spoof protections and store one clean entry per
  persistable candidate.
- [x] 4.7 Update CLI list/add/revoke behavior and operator docs for schema 3,
  including manual rollback by restoring the preserved `.v2.bak` while the
  daemon is stopped.

## 5. Immutable reviewed safe-policy catalog

- [x] 5.1 Replace runtime-overridable safe-verb strings with embedded typed
  per-platform catalog entries.
- [x] 5.2 Audit every Linux and Windows entry against the reviewed-diagnostic
  rule and its explicit threat-model boundary.
- [x] 5.3 Remove `find`, `awk`, `rg`, `sort`, and every other unproved entry;
  add direct adversarial cases.
- [x] 5.4 Delete executable-specific normalization from reviewed-safe
  authorization. Match canonical ShellSyntaxTree token prefixes instead.
  Reject parser-owned arguments before the matched phrase completes.
- [x] 5.5 Preserve redirect, explicit path, safe-root, audience, and symlink
  checks as separate effects. Use lexical path shape only to reject unsafe or
  unresolved possible local paths.
- [x] 5.6 Preserve native PowerShell provider checks, including strict
  `Get-Content Env:SECRET` behavior.
- [x] 5.7 Return an agent scope-declaration correction before a parent-session
  or subagent user prompt only when each reviewed-safe candidate remains
  beneath the exact shell cwd and the registered `set_working_directory` tool
  accepts that non-temp cwd; preserve the authored call and tool history, and
  retain the approval bridge when the tool is absent or rejects the cwd. Apply
  a successful child declaration to later child contexts, reload project
  instructions, and keep the parent project unchanged. Reject NUL, CR, and LF
  at the shared declaration boundary; prove the tool returns a bounded error
  without a child-scope or prompt update. Prove a headless declaration prevents
  a repeated correction but does not grant authority to the unchanged retry.

## 6. Bash causal approval intent

- [x] 6.1 Derive intent from ShellSyntaxTree 0.3.4 working-directory effects and
  canonical Bash control flow without changing execution analysis.
- [x] 6.2 Implement replacement and invalidation across later directory changes,
  `||`, joins, groups/subshells, dynamic flow, and unsupported regions.
- [x] 6.3 Apply intent only to reviewed diagnostic candidates without writing
  redirects; keep folder grants and every deny check on real facts.
- [x] 6.4 Pin D03's `/tmp` trace and later-directory-mutation counterexamples.
- [x] 6.4a Validate each fallback directory, POSIX `/tmp` alias descendants,
  session prerequisites, and real-scope folder grants. Use parser effects for
  temporary-scope transitions. Keep protected fallback denial terminal before
  symlink eligibility.
- [ ] 6.5 Keep native PowerShell causal scope strict and record native Windows
  expected results.

## 7. ShellSyntaxTree 0.3.1 facts through 0.3.4

- [x] 7.1 Upgrade the central package to public 0.3.2, which includes the
  0.3.1 authored-source facts and keeps same-language child shells strict.
- [x] 7.2 Consume effective `Value` for runtime checks and approved
  `AuthoredValue` only for the documented approval perspective.
- [x] 7.3 Treat `IntegerRange` and `Concatenation` as bounded scalar data only.
- [x] 7.4 Check every finite effective value whose `Argument.IsPath` is true
  through `ToolPathPolicy`. Check each ShellSyntaxTree 0.3.3 `Exact` or
  `FiniteSet` `AuthoredFileSystemValue` through the same policy. Treat
  `AuthoredPathShape` as lexical-only. Keep unknown path values strict.
- [x] 7.5 Delete the broad Bash environment-variable relaxation and its
  superseded tests.
- [x] 7.6 Pin exact D02, D10, and D14 input-to-coverage results.
- [x] 7.7 Upgrade to public ShellSyntaxTree 0.3.4. Consume its closed
  working-directory effect without command-name parsing.

## 8. Trace, guides, and behavioral evals

- [x] 8.1 Emit the capped trace schema and enforce control/bidi escaping,
  secret redaction, and truncation.
- [x] 8.2 Append actor grant rows without a second scan; project near-miss logs
  from the same trace.
- [x] 8.3 Keep trace data out of model prompts and session journals.
- [x] 8.4 Update consumer and operator guides with complete input, facts,
  coverage, trace, and output examples.
- [x] 8.5 Update the always-loaded rules, the `netclaw-operations` skill, and
  deterministic approval evals for schema 3 and the authored-source boundary.
  Remove the false claim that an absolute path declares a safe root. Add a
  subagent eval that proves a different user-named project is declared before
  the child's first shell inspection.
- [x] 8.6 Update `IMPLEMENTATION_PLAN.md` and canonical OpenSpec requirements.

## 9. Validation and staged delivery

- [x] 9.1 Run strict OpenSpec validation before implementation and delivery.
- [x] 9.2 Run focused `Netclaw.Security`, `Netclaw.Actors`, persistence, CLI,
  recovery, and eval tests.
- [ ] 9.3 Run the complete Linux approval matrix and native Windows PowerShell
  matrix.
- [x] 9.4 Run Release build, repository tests, header verification, and
  Slopwatch.
- [x] 9.5 Obtain adversarial review of every vertical slice and resolve all
  findings.
- [ ] 9.6 Rebase each slice on `upstream/dev` and deliver dependency-ordered PRs
  with observed CI status.

## 1. Freeze the behavior contract

- [x] 1.1 Rebase onto the merged live-regression corpus and record its exact commit and fixture hashes.
- [x] 1.2 Pin terminal-precedence overlaps for protected paths, invalid evidence, unavailable persistence, corrections, one-time authority, prompts, and allows.
- [x] 1.3 Record the baseline files, lines, control-flow lines, method complexity, coverage risk, coordinator size, helper duplication, and callers.
- [x] 1.4 Record public API, approval-store, actor-event, snapshot, session-history, configuration, prompt, and trace compatibility baselines.
- [x] 1.5 Run the D-case, adversarial, live-regression, and full disposition suites before production edits.

## 2. Add call-local policy state

- [x] 2.1 Add a closed preflight result, closed stage outcome, and typed policy faults.
- [x] 2.2 Add the internal call-local evaluation state with immutable candidate identity and one coverage slot per candidate.
- [x] 2.3 Route coverage and its bounded trace row through one atomic state operation.
- [x] 2.4 Add tests for duplicate coverage, changed candidate facts, invalid candidate IDs, invalid transitions, and multiple terminal results.
- [x] 2.5 Run exact fixture parity and adversarial review before production decisions use the new state.

## 3. Make preflight data flow explicit

- [x] 3.1 Return one explicit shell preflight result from `ToolAccessPolicy` with no change to gate order or decisions.
- [x] 3.2 Pass the successful preflight result directly into projection and asynchronous completion.
- [x] 3.3 Return the exact authorized analysis with the internal shell authorization result.
- [x] 3.4 Pass the authorized analysis directly into stream and non-stream shell execution.
- [x] 3.5 Preserve the decision-only internal test seam without analysis retention.
- [x] 3.6 Remove shell analysis cache reads and writes after all execution paths migrate.
- [x] 3.7 Add exact-analysis, Auto allow, consume-once, authorization-only, overlap, and cross-call isolation tests.
- [x] 3.8 Run exact fixture parity, focused actor tests, and adversarial review for the preflight slice.

## 4. Validate actor evidence once

- [x] 4.1 Add the validated actor-evidence factory and bind every result field to projected candidates.
- [x] 4.2 Reject malformed store status, enums, candidate identity, scopes, timestamps, near misses, and unavailable-store combinations.
- [x] 4.3 Move session and persistent coverage behind the validated evidence boundary.
- [x] 4.4 Isolate shell use of public `IToolApprovalService` behind one exact, non-inferential adapter.
- [x] 4.5 Add malformed protocol, mixed coverage, unavailable store, and exact one-batch actor tests.
- [x] 4.6 Run exact fixture parity and adversarial review for the actor-evidence slice.

## 5. Extract the ordered policy stages

- [x] 5.1 Add one pipeline runner that stops on the first complete or fault result.
- [x] 5.2 Extract syntax, protected-path, causal-path, and causal-directory stages with no change to precedence.
- [x] 5.3 Extract actor-evidence and approval-exempt stages with no change to candidate order or request count.
- [x] 5.4 Extract reviewed-safe real-scope and intent-scope stages with no change to catalog behavior.
- [x] 5.5 Extract exact one-time and persistent-store availability stages while their authority owners stay fixed.
- [x] 5.6 Extract final correction, prompt, and allow completion into one terminal stage.
- [x] 5.7 Add stage-isolation tests and terminal-overlap tests after each extraction.
- [x] 5.8 Run exact fixture parity and adversarial review after each production stage slice.

## 6. Consolidate policy facts and prompt context

- [x] 6.1 Project candidate-scoped real, intent, fallback, redirect, and authored filesystem facts with origin, domain, base, and resolution state.
- [x] 6.2 Route typed facts through current denied-path, symlink, and reviewed-safe rules without later command-text rescans.
- [x] 6.3 Derive one uncovered-candidate context for exact one-time authority and user prompts.
- [x] 6.4 Preserve causal full-context, scratch limits, reusable phrases, directory depth, and candidate order.
- [x] 6.5 Centralize bounded trace completion and preserve exact redaction and row order.
- [x] 6.6 Add path origin/domain/base, redirect-mode, runtime recheck, prompt-context, one-time-key, causal-context, and trace-parity regressions.
- [x] 6.7 Run exact fixture parity and adversarial review for the consolidation slice.

## 7. Remove obsolete structure

- [x] 7.1 Remove dead coordinator branches, duplicate coverage mutation, duplicate path helpers, and duplicate prompt-scope logic.
- [x] 7.2 Keep the required public compatibility adapter isolated from new typed policy code.
- [x] 7.3 Record the separate generic approval API work that can remove the compatibility adapter ([#1944](https://github.com/netclaw-dev/netclaw/issues/1944)).
- [ ] 7.4 Verify production policy contains no new executable names or executable-private argument rules.
- [ ] 7.5 Verify no call-local parser occurrence, command text, path, or secret crosses actor or persistence boundaries.
- [ ] 7.6 Run API and durable-contract comparisons against the frozen baseline.
- [x] 7.7 Audit the complete changed production footprint and revise the reduction gate when file moves hide growth.
- [ ] 7.8 Remove the displaced production lines and control flow until the complete footprint is below baseline.
- [ ] 7.9 Run exact parity and adversarial review after each additional reduction slice.

## 8. Prove equivalence and reduction

- [ ] 8.1 Run Release build, full tests, headers, format checks, strict OpenSpec, diff checks, and changed-file Slopwatch.
- [ ] 8.2 Run the exact D-case, adversarial, live-regression, full Bash, PowerShell 7, and Windows PowerShell 5.1 matrices.
- [ ] 8.3 Run native Linux, macOS, and Windows validation for platform-specific path and shell behavior.
- [ ] 8.4 Confirm channel, reminder, webhook, headless, recovery, and subagent outcomes remain unchanged.
- [ ] 8.5 Compare public APIs and persisted wire bytes against the frozen baseline.
- [ ] 8.6 Report final lines, control-flow lines, method complexity, coverage risk, files, largest methods, and duplicate helpers beside the baseline.
- [ ] 8.7 Require the original-file and complete-footprint reduction gates while all required checks remain.
- [ ] 8.8 Obtain final adversarial review of authority, precedence, compatibility, test sufficiency, and code reduction.
- [ ] 8.9 Stop and revise this change if any outcome, trace row, authority boundary, compatibility contract, or reduction gate differs.

## 1. Plan and baseline

- [x] 1.1 Save the canonical HTML plan and validate the OpenSpec change with `openspec validate --strict`.
- [x] 1.2 Record baseline, corpus hashes, fixture authority, and private incident source maps; verify the manifest.
- [x] 1.3 Run the bounded same-revision baseline through the existing corpus runner and retain its report and limits.

## 2. Legacy grant diagnostics

- [x] 2.1 Add baseline-failing tests for legacy token, assignment, and shell near misses; retain actual-match and scope controls.
- [x] 2.2 Repair legacy near-miss interpretation without changing grant coverage; pass the focused security tests.
- [x] 2.3 Update the existing runbook and mapped runtime skill; verify the skill version and applicable behavioral evals. Runbook and skill version `2.134.0` are verified. The user waived runtime eval; no eval pass is claimed.
- [x] 2.4 Review the diagnostic diff independently and compare candidate authorization fields with the baseline; permit diagnostic changes only. The review found only near-miss reporting changes. The isolated diagnostic comparison has zero differences.
- [x] 2.5 Pass Slopwatch, copyright verification, and whitespace checks on the diagnostic slice.

## 3. Assignment proof experiment

- [x] 3.1 Reproduce the launch-derived and literal assignment forms under the same host, environment, and directory fixture.
- [x] 3.2 Audit existing parser source and effect facts; deliver a written generic proof or a concrete counterexample.
- [x] 3.3 Test unknown HOME changes, branch joins, substitutions, wrapper sources, environment qualifiers, protected paths, links, scopes, and launch checks.
- [x] 3.4 Record the parent's independent proof decision before any assignment product edit; rejection must name the missing fact.

## 4. Conditional assignment implementation

- [x] 4.1 After task 3.4 accepts the proof, reuse existing facts for exact assignment identity and pass the positive and negative controls.
- [x] 4.2 Prove a literal-qualified grant covers the equivalent launch-derived candidate and an unqualified or changed-value grant does not.
- [x] 4.3 Update affected current architecture and operational guidance in the same diff; validate the matching spec and skill gates. Architecture, mapped guidance, and canonical spec are updated. The mapped skill is `2.134.0`; runtime eval is waived.
- [x] 4.4 Review and run the focused assignment mutation target; fail when an expected mutant survives or does not run. All 84 selected mutants died, including three new guard mutants.
- [x] 4.5 Pass Slopwatch, copyright verification, and whitespace checks on the assignment slice.
- [x] 4.6 If the proof fails, remove unsupported implementation requirements through OpenSpec and record the rejected experiment instead of a product change. Not applicable: task 3.4 accepted the bounded Bash proof.

## 5. Combined verification and handoff

- [x] 5.1 Integrate accepted commits in the named integration worktree and run the affected security and actor test suites. Candidate `7c2718db3` passes 1,977 Security and 3,366 affected Actors tests. One baseline actor failure and 42 total skips remain recorded.
- [x] 5.2 Compare the pinned baseline and combined candidate on the historical corpus and incident representatives; review every changed decision. Historical replay: 1,635,245 records reviewed; no outcome transitions. The bounded representative replay preserves outcomes.
- [x] 5.3 Record the operator waiver of runtime LLM evaluation; retain the cancelled partial run as unresolved evidence. The operator explicitly removed this gate. No eval pass is claimed. The user waived runtime eval. Preserve the partial 77-trial result as inconclusive, not a pass; do not run a follow-up eval or control.
- [x] 5.4 Complete an independent authority review on the combined diff and its actual test assertions. The final review covers candidate `7c2718db3`. It finds no new bypass within scope and retains the known baseline limits.
- [x] 5.5 Document loop-redirect feasibility; identify compound file effects and unknown input-value limits. Track the separately authorized SST implementation in its own change.
- [x] 5.6 Update the plan with exact commits, passed checks, unresolved limits, and the next review step; preserve the live daemon and approvals.

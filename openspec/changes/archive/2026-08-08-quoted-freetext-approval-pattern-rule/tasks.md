## 1. Implementation (already merged in PR #1815 / commit 4836e881)

- [x] 1.1 Add the `IsQuotedFreeTextArg` helper that detects a quote-wrapped argument with internal whitespace
- [x] 1.2 Add the termination check to `ReconstructClauseText` alongside the digit-bearing and multi-line rules
- [x] 1.3 Exempt path-shaped arguments (`IsPath = true`) so a quoted path keeps directory scoping
- [x] 1.4 Add regression tests for drop, single-word keep, path keep, and generalization across values

## 2. Spec reconciliation

- [x] 2.1 Write the proposal, design, and delta spec for the `tool-approval-gates` capability
- [x] 2.2 Sync the delta into `openspec/specs/tool-approval-gates/spec.md` with `openspec` tooling
- [ ] 2.3 Verify the change with `openspec` validation
- [ ] 2.4 Archive the change after the spec is synced

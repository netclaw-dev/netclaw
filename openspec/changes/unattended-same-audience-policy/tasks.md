## 1. Policy

- [x] 1.1 Remove the unattended-only trust zone from `PathAccessPolicy`; verify `UnattendedPathAccessTests` (attended and unattended reach are equal)
- [x] 1.2 Remove the attended-only conditions (reviewed-safe coverage, D1 split, causal-list proof, managed temporary advice) and the unresolved-input screen; verify `UnattendedSamePolicyTests`
- [x] 1.3 Remove the PR 6e grant-first replacement and its allow reason; verify `ToolAuthorizerOrderMutationTests`
- [x] 1.4 Deny an unattended consent request in `ToolAuthorizer` with `approval_required_unattended`; verify `An_unattended_call_without_a_grant_is_denied`

## 2. Corpus and gates

- [x] 2.1 Update the approval catalog and snapshot; verify no attended row changes and `scripts/check-approval-outcome-direction.py` passes
- [x] 2.2 Update the focused mutation targets and their expected counts; verify each gate locally

## 3. Docs

- [x] 3.1 Update `docs/runbooks/tool-approval-gates.md`, `docs/architecture/tool-authorization.md`, `TOOLING.md`, and the `netclaw-operations` skill

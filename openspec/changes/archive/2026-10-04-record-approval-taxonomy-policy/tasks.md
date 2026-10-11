## 1. Verify the specification against the merged code

- [x] 1.1 Compare each modified requirement with the PR bodies and diffs of #2314 to #2319, #2321 to #2323, #2327, and #2329, and verify that each rule has a source PR
- [x] 1.2 Verify that each catalog case named in a scenario exists in `ShellApprovalDispositionMatrixTests.Shell_approval_cases_match_review_table.verified.md` with the stated result
- [x] 1.3 Run `openspec validate record-approval-taxonomy-policy --strict` and verify that the change is valid

## 2. Sync and archive

- [ ] 2.1 Merge the specification PR only after #2329 merges, and verify with `gh pr view 2329 --repo netclaw-dev/netclaw`
- [x] 2.2 Sync the delta into `openspec/specs/tool-authorization/spec.md` with `/opsx:sync`, add the new mutation targets to the Verification Map, and verify that `openspec validate --specs --strict` passes
- [x] 2.3 Archive the change with `/opsx:archive` and verify that the change folder moves to `openspec/changes/archive/`

## 1. Candidate-derived repository proof

- [x] 1.1 Add candidate scope resolution with one canonical common directory. Verify focused security tests cover missing, linked, mixed, and sibling scopes.
- [x] 1.2 Use candidate scopes for prompt eligibility. Verify actor tests offer the repository choice from an unrelated working directory.
- [x] 1.3 Recompute candidate scopes during grant creation. Verify each grant carries its candidate-derived worktree root.
- [x] 1.4 Use candidate scope during stored grant reuse. Verify unrelated working directories, moved worktrees, and swapped metadata remain safe.

## 2. Security gates and guidance

- [x] 2.1 Update the focused repository mutation target and `TOOLING.md`. Verify every expected mutant is killed.
- [ ] 2.2 Update the operations skill and approval runbook. Verify the skill version changes and the behavioral eval suite passes.
- [x] 2.3 Audit all new samples for PII. Verify fixtures contain only synthetic names, paths, identities, and commands.

## 3. Delivery evidence

- [ ] 3.1 Run focused and full affected tests, Slopwatch, file-header checks, and strict OpenSpec validation.
- [ ] 3.2 Run approval native smoke or record a reproducible environment blocker.
- [ ] 3.3 Obtain an independent security review and resolve all authority findings.
- [x] 3.4 Open a draft pull request with the contract, verification evidence, and remaining gates.

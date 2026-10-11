## 1. Shared reach rule

- [x] 1.1 Add `ToolApprovalEntryComparer.CoversCommandWords` and verify `ExactVerbChainMutationTests` pass (verb grant covers later words, program-only grant stays exact, empty grant covers nothing)
- [x] 1.2 Route both `TokenPrefix` and `LegacyExact` matching in `ApprovalPatternMatching` through the shared rule and verify `ApprovalPatternV3Tests` pass
- [x] 1.3 Route `ApprovalGrantHygiene.Covers` through the shared rule and verify the save-skip theory in `ToolApprovalStoreTests`, `ApprovalStoreHygieneInvariantTests`, and `ToolApprovalHygieneDoctorCheckTests` pass

## 2. Link words

- [x] 2.1 Add `ShellGrantFileWords.NamesLink` and add each link word's path as a path scope in `ShellApprovalMatcher`; verify `FileWordCommandWordsTests` deny `git add keylink` under a `git add` grant

## 3. Real approval path gates

- [x] 3.1 Add the owner examples as a data-driven gate for both stored kinds in `SubcommandEverywhereGrantTests` and verify it passes
- [x] 3.2 Update the tests, catalog rows, review snapshot, and policy fixtures that pinned exact-only matching; verify the Actors, Security, Configuration, and Cli suites pass and `scripts/check-approval-outcome-direction.py` passes

## 4. Gates and documentation

- [x] 4.1 Retarget `scripts/run-exact-verb-chain-mutations.sh` to the shared rule and verify every tested mutant is detected
- [x] 4.2 Update the CLI help, runbook, architecture document, TOOLING.md, and the `netclaw-operations` skill (version bump); verify by review
- [x] 4.3 Run the authorization corpus differential against dev and verify only prompt to Allowed transitions
- [x] 4.4 Run slopwatch and the file header check and verify both pass

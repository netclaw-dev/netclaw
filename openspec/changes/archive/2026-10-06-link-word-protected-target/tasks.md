## 1. Link rule

- [x] 1.1 Remove the link path scope from `ShellApprovalMatcher` and verify `FileWordCommandWordsTests.Folder_grant_covers_a_link_to_its_own_folder` passes attended and unattended
- [x] 1.2 Add the plain-word link screen to `ToolPathPolicy` and verify `Plain_word_link_to_a_protected_path_is_denied` (including `keys2` and `key1link`) and the mutation test pass
- [x] 1.3 Add the screen to `run-exact-verb-chain-mutations.sh` and verify every tested mutant is detected
- [x] 1.4 Update the runbook, architecture document, TOOLING.md, and the `netclaw-operations` skill; verify by review

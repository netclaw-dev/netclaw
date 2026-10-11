## 1. Protected paths

- [x] 1.1 Remove the system skill folder and the server feed folder from the write list in `DaemonToolPathPolicyFactory`, and verify `DaemonToolPathPolicyFactoryTests.Skill_folders_are_writable_and_the_control_plane_is_not` passes
- [x] 1.2 Verify through the production authorizer that `bash <feed skill script>` with a `bash` grant is allowed and `ls <system skill folder>/` is not denied (`ApprovalContractBoundaryTests.Skill_folders_get_the_decision_of_an_ordinary_path`)
- [x] 1.3 Verify that each write form to config, the grant store, secrets, webhooks, and keys stays denied (`ApprovalContractBoundaryTests.Control_plane_writes_stay_denied_in_every_form`)

## 2. Reason codes

- [x] 2.1 Replace `shell_path_outside_trust_zone` with `shell_path_protected` and `shell_path_outside_trusted_roots`, and verify the authorization suites and `ToolAuthorizerOrderMutationTests` pass
- [x] 2.2 Update the eval assertion, the runbook, and the architecture glossary note, and verify no `shell_path_outside_trust_zone` text remains outside archives

## 3. Feed sync restore

- [x] 3.1 Record per-file SHA-256 hashes in the feed sync state and install again on a mismatch, and verify `ServerFeedSkillSyncServiceTests.SyncOnce_restores_a_locally_changed_skill_of_the_same_version` passes
- [x] 3.2 Confirm that the system skill tree is restored at each daemon start (`BuiltInSkillSeedingTests.Restore_replaces_the_managed_tree_and_preserves_user_skills`)

## 4. Guidance and proof

- [x] 4.1 Update `netclaw-operations` and `skill-authoring` skill text, bump their versions, and verify the skill seeding tests pass
- [x] 4.2 Run the authorization corpus differential against `dev` and verify the only outcome changes are skill-folder paths from `Denied`
- [x] 4.3 Run the shell config read and tool authorizer order mutation gates, slopwatch, and the header check, and verify each passes

## 1. Fix A: Personal read reach

- [x] 1.1 Add the effective-access helper of design D1 to `PathAccessPolicy` and call it from `Evaluate`, `GetTrustedRoots`, and `HasUnrestrictedFileAccess`; verify `PersonalReadReachTests` and the updated `PersonalFileAccessPolicyTests` pass
- [x] 1.2 Verify that `UnattendedPathAccessTests`, `PublicAudienceFileAccessPolicyTests`, `ReviewedSafeShellPolicyTests`, and `ToolApprovalGateTests` still pass

## 2. Fix B: short private temp folder

- [x] 2.1 Replace the stubs in `SessionStoragePaths` (`CreateVersion2`, `CreateLegacy`, `ForChild`) and `ManagedTemporaryLocation.PrivateBase` with the layout of design D2; verify `ManagedTemporaryLayoutTests` pass
- [x] 2.2 Implement `ManagedTemporaryBase.ForCurrentUser` (`geteuid`) and verify `Base_of_the_current_user_uses_the_effective_user_id` passes
- [x] 2.3 Implement the private-folder checks of design D3 in `ManagedTemporaryEnvironment.Prepare` and `HostManagedTemporaryFolderInspector`; verify `ManagedTemporaryFolderCheckTests` and `ManagedTemporaryPrivateFolderTests` pass, including the real Unix socket bind
- [x] 2.4 Add the key folder as a session root in `PathAccessPolicy.AddSessionRoots` (design D4); verify `ManagedTemporaryRootAccessTests` pass
- [x] 2.5 Skip the correction advice for the own temp folder (`session-cwd` spec); verify `OwnTemporaryFolderCorrectionTests` and `TemporaryPathCorrectionPolicyTests` pass
- [x] 2.6 Make the layout a required argument of the public `SqliteSessionStorageResolver` constructor and register `ManagedTemporaryLayout.ForHost()` in the daemon; verify `SessionStorageResolverTests` pass
- [ ] 2.7 Run `./scripts/smoke/run-smoke.sh light` and one manual Chrome or Playwright render in a session; verify that no `Socket path too long` error occurs and that `file_read` reads the render output

## 3. Doctor warning

- [x] 3.1 Add the write-wider-than-read check of design D6 to `ToolAudienceProfilesDoctorCheck`; verify `WriteWiderThanReadDoctorTests` and `ToolAudienceProfilesDoctorCheckTests` pass

## 4. Mutation gate

- [x] 4.1 Extend `scripts/run-path-access-mutations.sh` with spans for the effective-access helper, the key-root addition in `AddSessionRoots`, and the folder checks in `ManagedTemporaryEnvironment`; verify that `PersonalReadReachMutationTests` and the linked `ManagedTemporaryFolderCheckTests` kill each mutant
- [x] 4.2 Update the expected mutant count in the script and the `path-access` row of `TOOLING.md`; verify the CI time budget of the group
- [ ] 4.3 Run `./scripts/run-shell-command-analysis-mutations.sh` and `./scripts/run-no-program-mutations.sh`; if the D1 known-path mutant of `ReviewedSafeShellPolicy` now survives for Personal, record it as equivalent in `TOOLING.md`

## 5. Docs and skills

- [ ] 5.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` and `references/projects.md` with the text in the PR description, bump `metadata.version`, and run `./evals/run-evals.sh`
- [ ] 5.2 Add the release note for hosts with an explicit Personal `ReadFiles.Mode = Roots`
- [x] 5.3 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`
- [ ] 5.4 After merge, run `/opsx-sync` and `/opsx-archive`

## 6. PR #2559 review round

- [x] 6.1 Make a file tool write below the base use `ManagedTemporaryEnvironment.EnsureFolders` (B1); verify `ManagedTemporaryFirstWriteTests` pass
- [x] 6.2 Deny a Team or Public path in another key folder below a configured root (S1); verify `ManagedTemporaryConfiguredRootTests` pass and add the span to the `path-access` gate
- [x] 6.3 Delete the resolver constructors with a default layout (S3); verify `Each_resolver_constructor_emits_the_short_layout_of_the_given_base` passes

## 1. Default audience profiles and tool gating

- [x] 1.1 In `Netclaw.Configuration/ToolAudienceProfiles.cs`, set `CreatePublic()` `AllowedTools` to `[file_read, file_list, attach_file]` (`WriteFiles` stays session-scoped — see design.md).
- [x] 1.2 In `CreateTeam()`, set `AllowedTools` to `[file_read, file_list, file_write, file_edit, attach_file, web_search, web_fetch, skill_manage, set_reminder, list_reminders, cancel_reminder, get_reminder_history, set_working_directory]`; keep `ToolsMode = Allowlist` and MCP disabled.
- [x] 1.3 Add a code comment on the factory methods stating the monotonic invariant `Public ⊆ Team ⊆ Personal`.
- [x] 1.4 In `Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs`, add `file_edit`, `file_list`, `web_search`, and `web_fetch` to `IsProfileManagedTool`.
- [x] 1.5 Verify `netclaw-config.v1.schema.json` `AllowedTools` accepts arbitrary tool-name strings; if it enumerates names, add `file_list`.
- [x] 1.6 Gate `web_search`/`web_fetch` as profile-managed tools — Public loses outbound web access; Team and Personal keep it. The deployment-wide search feature flag stays an orthogonal kill switch.

## 2. file_list directory-enumeration tool

- [x] 2.1 Add `Netclaw.Actors/Tools/FileListTool.cs`: read-only single-level directory listing (entry name + file/dir type, capped entry count), with copyright header.
- [x] 2.2 Authorize the target path through `ScopedFileAccessPolicy` read access, mirroring `FileReadTool`; sanitize denied-path errors so no configured root path leaks.
- [x] 2.3 Register `file_list` in `ToolRegistrationExtensions.WithFirstPartyTools` with grant category `file`, alongside `file_read`.

## 3. DM audience classification

- [x] 3.1 In `Netclaw.Channels/AudienceResult.cs`, change the `Resolve` fallback from `(isDirectMessage || isExplicitUser || isExplicitChannel)` to `(isExplicitUser || isExplicitChannel)` so a non-allowlisted DM resolves to `Public`; keep the `channelAudiences` / `dm` override precedence intact.

## 4. Tests

- [x] 4.1 Rework `DispatchingToolExecutorTests` Team-profile test (rename to `Team_profile_exposes_file_tools_and_hides_shell_and_webhooks`) and add `Public_profile_exposes_read_tools_and_hides_mutation_tools`.
- [x] 4.2 Update the `_restrictedExecutor` constructor overrides and retarget the public file_write test to a `Team` context.
- [x] 4.3 Add default-profile tests (`ToolAudienceProfileDefaultsTests`): Public excludes `file_write`/`file_edit`; Team includes them; monotonic containment holds. Update `SchedulingToolAudienceTests` / `SetWorkingDirectoryAudienceTests` for the new Team defaults.
- [x] 4.4 Add `FileListToolTests`: happy-path enumeration, Public confined to session dir, Team can list a workspace root, sanitized denial outside roots.
- [x] 4.5 Update `AclPolicyContractTests` DM cases (non-allowlisted DM → Public, allowlisted DM → Team) and the DM-dependent attachment/history tests.

## 5. Docs, schema, and system skill

- [x] 5.1 Update `docs/spec/configuration.md` `AudienceProfiles` JSON example (Public + Team `AllowedTools`) and the accompanying prose.
- [x] 5.2 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`: correct the `set_working_directory` audience-availability line, document `file_list` and the per-audience grant tiers, and bump `metadata.version` (2.4.0 → 2.5.0).

## 6. Verification

- [x] 6.1 `dotnet build` + `dotnet test` green: Configuration.Tests 318, Actors.Tests 1745, Cli.Tests 699, Security.Tests 554, Daemon.Tests 572 — 0 failures (Channels code covered under Actors.Tests).
- [x] 6.2 `dotnet slopwatch analyze` — 0 issues; `./scripts/Add-FileHeaders.ps1 -Verify` — all files have headers.
- [x] 6.3 Added the `tool_file_list` discovery eval case to `evals/run-evals.sh`. Running `./evals/run-evals.sh` needs Docker + a model endpoint — deferred to CI / a model-equipped host.
- [x] 6.4 `./scripts/smoke/run-smoke.sh init-wizard` not run here (provisions native Ollama). Not strictly required: no TUI prompt flow changed — only the scaffolded config values, which `SecurityPostureStepViewModelTests` covers.
- [x] 6.5 Wizard config-scaffolding and `netclaw doctor` behavior on the new defaults are covered by `SecurityPostureStepViewModelTests` and `ToolAudienceProfilesDoctorCheckTests` (Cli.Tests, passing).

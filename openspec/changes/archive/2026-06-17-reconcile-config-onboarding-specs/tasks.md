<!-- This is a spec-reconciliation change: the implementation already shipped on
docs/netclaw-validated-ui-components. Tasks are VERIFICATION (confirm each delta matches the
cited code/tests) + sync, not new implementation. -->

## 1. Verify netclaw-onboarding deltas against shipped code

- [x] 1.1 Confirm `InitWizardViewModel` builds the 5-step flow (Provider → Identity → Security Posture → Enabled Features → Health Check) and Personal posture skips Enabled Features (`FeatureSelectionStepViewModel.IsApplicable`); the spec's 5/4 dynamic step count matches.
- [x] 1.2 Confirm `IdentityStepViewModel.SubStepCount == 4` and `ContributeConfig` writes only AgentName/CommunicationStyle/UserName/UserTimezone (no workspaces directory, no notification webhook).
- [x] 1.3 Confirm `HealthCheckStepViewModel.RunHealthCheckCoreAsync` calls `LaunchChat()` on success with no Enter gate, and stays on the summary for warnings/failure — `HealthCheckStepViewModelTests` auto-launch assertions.
- [x] 1.4 Confirm the container-supervisor deferral reason is surfaced when the daemon is supervised-but-absent — `HealthCheckStepViewModelTests.RunWithOrchestrator_SupervisorMarkerSetButNoSupervisor_SurfacesActionableReason`.
- [x] 1.5 Confirm NO Memory/Memorizer wizard step exists and memory health is reported as SQLite (`IdentityStepViewModel.ContributeHealthChecksAsync` "Memory backend (SQLite)") — the REMOVED requirements correspond to no shipped code.
- [x] 1.6 Confirm the onboarding trigger updates `SOUL.md` (+ `TOOLING.md`) via `BuildOnboardingTrigger`/`WriteIdentityFiles`; `PERSONALITY.md`/`INSTRUCTIONS.md`/`USER.md` are not written. Confirm environment-discovery / project-registration remain unimplemented (DEFERRED).

## 2. Verify channel-audience-tui deltas

- [x] 2.1 Confirm `ChannelsConfigViewModel.ValidateSlackChannelsAsync` blocks only on a genuine probe failure (non-empty `ErrorMessage`) and persists unresolved channel names non-blockingly (inert in the ACL) — `ChannelsConfigViewModelTests`.
- [x] 2.2 Confirm `NormalizeSlackChannelNamesToIds` runs on the background label refresh and auto-persists; confirm `GetEffectiveSecret` blank-preserve on credential rotation.
- [x] 2.3 Confirm the add-channel flow is resolve-before-add single-entry (`BeginAddChannel`/`ApplyAddChannelAsync`/`ResolveSingleChannelAsync`) — no type-to-filter `conversations.list` search exists.

## 3. Verify netclaw-config-command deltas

- [x] 3.1 Confirm `ConfigDashboardViewModel.Items` lists `Workspaces Directory` as the 10th domain area.
- [x] 3.2 Confirm Skill Sources "add a local folder" and Workspaces use `FilePickerNode` directory pickers (`SkillSourcesConfigPage`/`WorkspacesConfigPage`): autosave on selection, Ctrl+N new folder.
- [x] 3.3 Confirm `InboundWebhooksConfigViewModel` enable + `ExecutionTimeoutSeconds` behavior and the no-routes advisory; route authoring stays CLI-owned (`netclaw webhooks set`).
- [x] 3.4 Confirm `SearchSectionSpec` progressive disclosure (backend selection reveals Brave/SearXNG field) and that Channels handles the Mattermost adapter.

## 4. Verify minor deltas

- [x] 4.1 security-posture-tui: confirm posture step ordering (after Provider; no ChatServices step), audience defaults applied by `SlackStepViewModel.OnLeave` from `WizardContext` posture, and the `SecurityAccessViewModel` posture cascade.
- [x] 4.2 feature-selection-wizard: confirm Personal posture omits Enabled flags (`FeatureSelectionStepViewModel.IsApplicable` + `LoadEnabledFeatures` default-true) and `SavePosture` auto-opens Enabled Features.
- [x] 4.3 inbound-webhooks: confirm `Webhooks.ExecutionTimeoutSeconds` (range 1–3600, default 300) and the no-routes advisory scenario.

## 5. Validate and sync

- [x] 5.1 `openspec validate reconcile-config-onboarding-specs --strict` passes (all deltas parse; MODIFIED headers match existing specs).
- [x] 5.2 `/opsx-verify` — confirm each delta still matches the cited code/tests.
- [x] 5.3 On merge with the implementation branch, `/opsx-sync` then `/opsx-archive` to fold the deltas into `openspec/specs/`.

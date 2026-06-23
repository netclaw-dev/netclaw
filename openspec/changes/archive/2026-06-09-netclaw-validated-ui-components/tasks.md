## 1. Discovery and migration inventory

- [ ] 1.1 Inventory every mutable TUI surface under `src/Netclaw.Cli/Tui` and classify each as `validated-component-required`, `display-only`, or `defer-with-reason`.
- [ ] 1.2 For each `netclaw config` leaf, document the downstream runtime consumer of its persisted data: daemon options, channel adapter, skill scanner/feed loader, search provider, webhook runtime, ACL/security policy, or other named consumer.
- [ ] 1.3 For each mutable action, document its static validation rule, dynamic validation policy, persistence writer, and post-commit reload/status behavior before code migration starts.
- [ ] 1.4 Create a deletion candidate list for old tests, helpers, page-level input handlers, and UI components; mark each candidate `delete-after-replacement-proof`, `keep`, or `defer`.
- [ ] 1.5 Run `openspec validate netclaw-validated-ui-components --type change` and keep it passing before implementation begins.

## 2. Core page-independent Netclaw UI commit primitives

- [x] 2.1 Add `NetclawUiCommit<TDraft>`, `NetclawUiCommitTrigger`, `NetclawUiCommitResult`, `NetclawUiValidationResult`, and failure/status result types in a page-independent TUI namespace.
- [x] 2.2 Add `NetclawUiDynamicCheck<TDraft>` with `Required(...)` and `NotApplicable(justification)`; reject empty `NotApplicable` justification.
- [x] 2.3 Implement `NetclawUiCommitPipeline` with ordering `ReadDraft -> static Validate -> DynamicCheck -> PersistAsync -> AfterCommit`.
- [x] 2.4 Ensure static validation failure prevents dynamic validation and persistence.
- [x] 2.5 Ensure dynamic validation failure prevents persistence unless the declared failure policy and user action explicitly choose save-anyway.
- [x] 2.6 Ensure persistence exceptions are caught by the pipeline and surface visible error status instead of silent failure.

## 3. Standard validated Netclaw UI components

- [x] 3.1 Add `INetclawUiComponent` or equivalent component contract for build, input handling, paste handling, and commit ownership.
- [x] 3.2 Add `NetclawValidatedTextField` using the existing boxed `TextInputNode` presentation, but requiring `NetclawUiCommit<string>` for acceptance.
- [x] 3.3 Add `NetclawValidatedAction<TDraft>` for completed actions such as add/remove, reset, token rotation, and save-anyway.
- [x] 3.4 Add `NetclawValidatedToggle` and `NetclawValidatedPicker<TValue>` for immediate completed actions.
- [ ] 3.5 Add `NetclawUiInputRouter` or `NetclawValidatedPage<TViewModel>` so pages delegate typed input, paste, backspace, `Enter`, `Space`, picker selection, and autosave triggers to validated components.
- [ ] 3.6 Prove the components still use standard Netclaw TUI chrome and do not introduce a parallel visual system.

## 4. Build enforcement against bypasses

- [ ] 4.1 Add Roslyn analyzer or build-failing architecture tests that reject raw mutable `TextInputNode` construction in TUI pages outside approved validated components.
- [ ] 4.2 Add enforcement that rejects page input handlers calling `Save`, `SaveAsync`, `ConfigAutosave`, or config writer methods directly for persisted mutable actions.
- [ ] 4.3 Add enforcement that rejects `ConsoleKey.Enter` branches that directly persist mutable config state.
- [ ] 4.4 Add enforcement that rejects direct `ConfigAutosave` use outside `NetclawUiCommitPipeline` or approved adapter code.
- [ ] 4.5 Add enforcement that rejects `NetclawUiDynamicCheck.NotApplicable` with empty or whitespace-only justification.
- [ ] 4.6 Add negative enforcement fixtures/tests proving each forbidden bypass fails the build/test gate.

## 5. Core component and pipeline tests

- [x] 5.1 Add pipeline tests proving static validation failure leaves config/secrets/sidecar files unchanged and does not call dynamic validation.
- [x] 5.2 Add pipeline tests proving dynamic validation failure leaves files unchanged and surfaces the declared error/warning.
- [x] 5.3 Add pipeline tests proving save-anyway persists only after structural validation passes and dynamic failure policy allows override.
- [x] 5.4 Add component tests proving typed input, paste input, backspace, and `Enter` acceptance flow through `NetclawUiCommitPipeline`.
- [ ] 5.5 Add component tests proving autosave, toggle, and picker actions use the same pipeline and trigger value as explicit acceptance.
- [ ] 5.6 Add component tests proving `Esc` cancels/navigates without committing incomplete drafts.

## 6. Skill Sources migration first

- [x] 6.1 Create `SkillSourcesCommitFactory` or equivalent adapters that produce commits for local path, local name, symlink toggle, remote URL, auth/token, remote name, rename, location change, enable toggle, token removal, token rotation, and source removal.
- [x] 6.2 Wire Skill Sources text entry screens through `NetclawValidatedTextField`; remove page-specific text draft rendering only after the standard component renders the same necessary field labels, placeholders, hints, and skill-server callout.
- [x] 6.3 Wire Skill Sources toggles/actions through `NetclawValidatedAction<TDraft>`, `NetclawValidatedToggle`, or `NetclawValidatedPicker<TValue>`.
- [ ] 6.4 Add headless Termina tests for Skill Sources local path: typed input, paste input, `Enter`, missing-directory static failure, unchanged config, success persistence, and `Esc` cancellation.
- [x] 6.5 Add headless Termina tests for Skill Sources remote URL: typed input, `Enter`, invalid URL static failure, fake probe dynamic failure, unchanged config, save-anyway path, successful canonical `SkillFeeds.Feeds` persistence, and token preserve/delete behavior.
- [x] 6.6 Add runtime consumer proof that local sources persist to `ExternalSkills.Sources` and remote skill servers persist to `SkillFeeds.Feeds` in the exact shapes consumed by runtime skill loading.
- [ ] 6.7 Delete old Skill Sources tests/components only if replacement tests cover their behavior through public user actions and no unique assertion is lost.

## 7. Remaining config leaf migrations

- [ ] 7.1 Migrate Telemetry & Alerting to validated components and prove invalid OTLP/webhook drafts block persistence before write.
- [ ] 7.2 Migrate Workspaces Directory to validated components and prove path validation, successful persistence, runtime path consumption, typed/paste input, `Enter`, and `Esc` cancellation.
- [ ] 7.3 Migrate Inbound Webhooks to validated components and prove timeout static validation, route-count diagnostics, enabled-state autosave, and unchanged persistence on invalid input.
- [ ] 7.4 Migrate Channels to validated components and prove Slack, Discord, and Mattermost dynamic validation failures block save before persistence through the same user action path.
- [ ] 7.5 Migrate Search to validated components without regressing provider-specific static and dynamic validation, probe warning/save-anyway behavior, and secret preservation.
- [ ] 7.6 Migrate Browser Automation to validated components and prove binary/profile validation and config-to-runtime consumer behavior.
- [ ] 7.7 Migrate Exposure Mode to validated components and prove non-local mode validation, pairing/orphaned-state behavior, inactive value preservation, and canonical `Daemon.ExposureMode` persistence.

## 8. Audit tests and obsolete artifact deletion

- [ ] 8.1 Update config editor audit tests so every visible mutable editor declares validated component coverage and dynamic validation policy coverage.
- [ ] 8.2 Update section-editor abstraction tests so mutable leaves without `NetclawUiCommit<TDraft>` coverage fail.
- [ ] 8.3 Replace render-only tests with component interaction tests only when the interaction tests also cover required rendering, accessibility-relevant labels, and user-action behavior.
- [ ] 8.4 Delete old page-level input handlers, helper components, and tests marked `delete-after-replacement-proof` only after `git grep` shows no production callers and replacement tests pass.
- [ ] 8.5 Preserve direct view-model/domain tests that still cover pure validation, mapping, serialization, or runtime binding behavior not duplicated by component tests.
- [ ] 8.6 Remove or encapsulate direct `ConfigAutosave` APIs so callers cannot bypass `NetclawUiCommitPipeline`.

## 9. Documentation and agent guidance

- [ ] 9.1 Update relevant developer docs or `docs/ui` material to describe `NetclawUiCommit<TDraft>`, dynamic validation policy, and the no-bypass rule for TUI pages.
- [ ] 9.2 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` if config/TUI operational guidance changes; bump `metadata.version` in the skill frontmatter.
- [ ] 9.3 If a system skill changes, run `./evals/run-evals.sh` and update eval expectations only for legitimate guidance changes.
- [ ] 9.4 Keep `openspec/changes/netclaw-config-command/tasks.md` aligned if this change supersedes or closes any remaining generalized autosave-validation tasks.

## 10. Validation gates

- [ ] 10.1 Run `dotnet build` after core components and after each major migration slice.
- [ ] 10.2 Run focused tests for each migrated area, including `dotnet test src/Netclaw.Cli.Tests/Netclaw.Cli.Tests.csproj`.
- [ ] 10.3 Run full `dotnet test` before marking the change complete.
- [ ] 10.4 Run `openspec validate netclaw-validated-ui-components --type change` after each artifact or behavior update.
- [ ] 10.5 Run native smoke for changed config surfaces: at minimum `./scripts/smoke/run-smoke.sh config-ops-surfaces`, `./scripts/smoke/run-smoke.sh config-channels`, and any additional migrated surface tapes.
- [ ] 10.6 Run `./scripts/smoke/run-smoke.sh light` before completion unless explicitly scoped to a narrower final validation with justification.
- [ ] 10.7 Run `dotnet slopwatch analyze` and fix any new violations.
- [ ] 10.8 Run `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`.
- [ ] 10.9 Run `git diff --check`.
- [ ] 10.10 Verify build enforcement catches representative bypass fixtures for raw text input, direct `Save`, direct `ConfigAutosave`, direct `ConsoleKey.Enter` persistence, and missing dynamic validation policy.

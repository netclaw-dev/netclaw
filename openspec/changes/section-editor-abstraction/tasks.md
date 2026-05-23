## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Confirm proposal, design, and spec deltas cover the
  `ISectionEditor` contract, registry, single-step orchestrator mode,
  exemption list, secret-handling rules, merge-on-save semantics,
  reentrant pre-population, and audit/test harness obligations.
- [ ] 1.2 Verify traceability references to `PRD-004` and `PRD-001` across
  change artifacts.
- [ ] 1.3 Run `openspec validate section-editor-abstraction --type change`
  and resolve all issues.

## 2. Core abstraction

- [ ] 2.1 Add `src/Netclaw.Cli/Tui/Sections/ISectionEditor.cs` with
  `SectionId`, `DisplayName`, `Category?`, `GetStatus`, `Summary`,
  `RelevantDoctorChecks`, `CreateEditor`.
- [ ] 2.2 Add `src/Netclaw.Cli/Tui/Sections/SectionStatus.cs` with the
  `Default | Configured | Warning | Error | Missing` enum.
- [ ] 2.3 Add `src/Netclaw.Cli/Tui/Sections/SectionContribution.cs` with
  `FieldAction.{Preserve, Replace, Remove}` and
  `SecretAction.{Preserve, Replace, Remove}` discriminated unions plus a
  contribution record carrying the per-field dictionaries.
- [ ] 2.4 Add `src/Netclaw.Cli/Tui/Sections/NoDoctorChecksAttribute.cs`
  carrying a required `justification` string for editors that genuinely
  have no relevant checks.

## 3. Registry and exemption list

- [ ] 3.1 Add `src/Netclaw.Cli/Tui/Sections/SectionEditorRegistry.cs` with
  `All()` and `Get(string sectionId)` methods. Construction fails fast on
  duplicate `SectionId`.
- [ ] 3.2 Add `services.AddSectionEditor<TEditor>()` DI extension on
  `IServiceCollection` registering the editor as `ISectionEditor`
  (transient) and as itself (for direct test resolution).
- [ ] 3.3 Add `src/Netclaw.Cli/Tui/Sections/SectionEditorExemptions.cs`
  with the documented exemption set and per-entry category metadata.
- [ ] 3.4 Wire `SectionEditorRegistry` and the three Change A editors
  (Provider, Identity, Posture) into the existing CLI DI composition root.

## 4. Single-step orchestrator mode

- [ ] 4.1 Add a single-step constructor to `WizardOrchestrator` accepting
  one `IWizardStepViewModel` and a `WizardContext`.
- [ ] 4.2 In single-step mode, `GoNext()` triggers save-and-exit;
  `GoBack()` / `Esc` triggers cancel-and-exit. Step-to-step filtering
  via `IsApplicable` is skipped.
- [ ] 4.3 Add orchestrator-level unit tests covering save-and-exit and
  cancel-and-exit single-step paths.

## 5. Merge-on-save plumbing

- [ ] 5.1 Refactor `WizardConfigBuilder.WriteConfigFile` to load existing
  `netclaw.json` via `ConfigFileHelper.LoadConfigFiles`, apply each
  step's `SectionContribution`, and write the merged dictionary back.
  Sections not contributed to remain byte-identical.
- [ ] 5.2 Refactor the wizard's secrets writer to load existing
  `secrets.json` and apply each contribution's `SecretAction`s. Blank
  on a secret-bearing field maps to `Preserve`; explicit
  `SecretAction.Remove` deletes the key.
- [ ] 5.3 Add `ConfigFileHelper.SecretPresent(paths, sectionId, key)` that
  inspects `secrets.json` for the key's existence without invoking the
  data-protection unprotect path. Unit-test against a fixture with both
  present and absent values.
- [ ] 5.4 Update `WizardOrchestrator.WriteConfig` to drive the new merge
  path. Existing first-run behavior remains observable-equivalent because
  the empty-existing path collapses to the previous overwrite shape.

## 6. ExistingConfig population at init entry

- [ ] 6.1 At the `netclaw init` entry point in `Netclaw.Cli.Program`, load
  `netclaw.json` via `ConfigFileHelper.LoadConfigFiles` and assign the
  parsed dictionary to `WizardContext.ExistingConfig`. Leave secrets out
  of the context entirely.
- [ ] 6.2 Remove the "Deferred — not implemented yet" comment block on
  `WizardContext.ExistingConfig` and document the populated-at-entry
  semantics.
- [ ] 6.3 Confirm the wizard's lifetime owns `ExistingConfig` for the
  duration of the run; the dictionary is read-only after entry.

## 7. Refactor three existing init step viewmodels

- [ ] 7.1 `ProviderStepViewModel`: implement `ISectionEditor`
  (SectionId `Providers`, `ShowInMenu = false` — covered by the
  existing `netclaw provider` CLI per D3 of the planning doc). Honor
  `ExistingConfig` in `OnEnter(direction)` for provider type, endpoint,
  auth method, model selection, and OAuth token expiry. API key field
  renders empty with "configured — leave blank to keep" hint when
  `SecretPresent` returns true.
- [ ] 7.2 `IdentityStepViewModel`: implement `ISectionEditor`
  (SectionId `Identity` as a synthetic identifier — Identity is NOT a
  top-level schema key; identity data spans `Workspaces`,
  `Notifications`, and identity files like `SOUL.md`. Add the
  synthetic ID `Identity` to `SectionEditorExemptions` with category
  `"synthetic-spans-multiple-sections"`. `ShowInMenu = false` — set
  once at init in MVP). Honor `ExistingConfig` for agent name, user
  name, timezone, comm style, workspaces directory, webhook URL. (Step
  is trimmed in the third change; this change keeps existing fields.)
- [ ] 7.3 `SecurityPostureStepViewModel`: implement `ISectionEditor`
  (SectionId `Security.Posture`, dotted path; `ShowInMenu = true` —
  surfaces in the dashboard in Change B). Honor `ExistingConfig` for
  the posture selection and posture-default cascade.
- [ ] 7.4 Each refactored editor declares non-empty
  `RelevantDoctorChecks` referencing the existing checks that scope to
  the editor's section.
- [ ] 7.5 Each refactored editor produces a `SectionContribution` from
  its viewmodel state on save; the orchestrator collects contributions
  and routes them through the new merge writer.

## 8. Round-trip test harness

- [ ] 8.1 Add
  `tests/Netclaw.Cli.Tests/Tui/Sections/SectionEditorTestBase.cs`
  abstract harness with the five canonical scenarios:
  `RoundTrip_NoOpEdit_PreservesConfig`,
  `RoundTrip_SingleFieldEdit_UpdatesOnlyThatField`,
  `Secrets_BlankSubmit_PreservesExistingSecret`,
  `Secrets_NonBlankSubmit_ReplacesSecret`,
  `Secrets_RemoveAction_DeletesSecret`.
- [ ] 8.2 Concrete test class for `ProviderSectionEditor` covering
  provider, endpoint, model, OAuth, and API-key paths.
- [ ] 8.3 Concrete test class for `IdentitySectionEditor`.
- [ ] 8.4 Concrete test class for `SecurityPostureSectionEditor`,
  including the posture-cascade write semantics.

## 9. Menu registry audit

- [ ] 9.1 Add
  `tests/Netclaw.Cli.Tests/Tui/Sections/MenuRegistryAuditTests.cs` with
  a single test that walks `SectionEditorRegistry.All()` and asserts:
  every registered editor has a `SectionEditorTestBase<TEditor>`
  subclass; every editor has non-empty `RelevantDoctorChecks` or
  `[NoDoctorChecks]`; and (gated by file existence, no error if absent
  in this change) a smoke tape at
  `tests/smoke/tapes/config-<sectionId-lower>.tape` exists when present.
- [ ] 9.2 Audit failure message lists all missing artifacts in one
  assertion message, naming each editor + missing piece.
- [ ] 9.3 Smoke-tape file existence is checked but not required at the
  audit level until the next change lands; comment in the test
  documents the cutover.

## 10. Existing test suite preservation

- [ ] 10.1 Run `./scripts/smoke/run-smoke.sh init-wizard` and confirm the
  existing init-wizard tape passes unchanged.
- [ ] 10.2 Run `./scripts/smoke/run-smoke.sh init-wizard-reverse-proxy`
  and confirm the existing reverse-proxy tape passes unchanged.
- [ ] 10.3 Run the full `./scripts/smoke/run-smoke.sh light` and confirm
  no regressions.

## 11. Quality gates

- [ ] 11.1 `dotnet build` clean across the solution.
- [ ] 11.2 `dotnet test` clean: all new round-trip tests pass; audit
  passes vacuously over the three registered editors; existing tests
  remain green.
- [ ] 11.3 `dotnet slopwatch analyze` reports no new violations.
- [ ] 11.4 `./scripts/Add-FileHeaders.ps1 -Verify` reports clean.
- [ ] 11.5 `openspec validate section-editor-abstraction --type change`
  passes.

## 12. Documentation and traceability

- [ ] 12.1 Update `PROJECT_CONTEXT.md` or `TOOLING.md` if the abstraction
  changes the way operators or contributors are expected to add editable
  sections (a one-liner pointing at `ISectionEditor` is sufficient at
  this stage).
- [ ] 12.2 Update PRD-004 with a forward reference to the
  `netclaw config` command landing in the next change; this change does
  not yet introduce it.
- [ ] 12.3 PR description closes #455 (reentrant init) and references this
  OpenSpec change ID.

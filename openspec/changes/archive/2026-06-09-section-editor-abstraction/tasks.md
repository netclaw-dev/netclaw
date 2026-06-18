## 1. OpenSpec planning artifacts and traceability

- [x] 1.1 Confirm proposal, design, and spec deltas describe a leaf-editor
  abstraction rather than a flat dashboard contract.
- [x] 1.2 Confirm the artifacts reflect the locked split: `init` owns
  bootstrap and Identity; `config` owns post-install editing.
- [x] 1.3 Run `openspec validate section-editor-abstraction --type change`
  and resolve issues.

## 2. Core abstraction

- [x] 2.1 Add `ISectionEditor` with `SectionId`, `DisplayName`,
  `Category?`, `ShowInMenu`, `GetStatus`, `Summary`,
  `RelevantDoctorChecks`, and `CreateEditor`.
- [x] 2.2 Add `SectionStatus`.
- [x] 2.3 Add `SectionContribution` with explicit field and secret
  actions.
- [x] 2.4 Add `[NoDoctorChecks]` justification support where truly needed.

## 3. Registry and exemption list

- [x] 3.1 Add `SectionEditorRegistry` with duplicate-ID fail-fast.
- [x] 3.2 Add `AddSectionEditor<TEditor>()` DI registration.
- [x] 3.3 Add `SectionEditorExemptions` entries for synthetic/init-owned
  surfaces, including Identity.
- [x] 3.4 Document that the registry is a leaf-editor registry and does
  not dictate the future dashboard IA.

## 4. Single-step orchestrator mode

- [x] 4.1 Add single-step hosting to `WizardOrchestrator`.
- [x] 4.2 Ensure save exits and cancel exits work without linear step-list
  navigation.
- [x] 4.3 Add unit tests for single-step save and cancel.

## 5. Semantic merge-on-save plumbing

- [x] 5.1 Refactor config writes to load existing config, apply
  contributions, and preserve unrelated sections semantically.
- [x] 5.2 Refactor secret writes to preserve blank submissions, replace on
  non-blank, and remove only on explicit delete.
- [x] 5.3 Preserve inactive values for exposure-mode and similar editors
  when they are not the active leaf being changed.
- [x] 5.4 Add `ConfigFileHelper.SecretPresent(...)` without decrypting
  stored values.

## 6. ExistingConfig population

- [x] 6.1 Populate `WizardContext.ExistingConfig` from on-disk config when
  init enters an editor flow that needs existing state.
- [x] 6.2 Keep secrets out of the context entirely.
- [x] 6.3 Document that this supports init-owned re-entry, not init as the
  main post-install editor.

## 7. Refactor bootstrap leaves

- [x] 7.1 Refactor Provider to implement `ISectionEditor`
  (`ShowInMenu = false`; owned by init / routed provider command).
- [x] 7.2 Refactor Identity to implement `ISectionEditor`
  (`ShowInMenu = false`; synthetic ID; init-owned).
- [x] 7.3 Refactor Security Posture to implement `ISectionEditor`
  (`ShowInMenu = true`; reusable under `Security & Access`).
- [x] 7.4 Refactor Enabled Features to implement `ISectionEditor`
  (`ShowInMenu = true`; separate from posture and audience profiles).
- [x] 7.5 Ensure each refactored editor declares meaningful validation
  checks and produces `SectionContribution` output.

## 8. Round-trip test harness

- [x] 8.1 Add `SectionEditorTestBase<TEditor>` with semantic round-trip,
  secret-preservation, and targeted update scenarios.
- [x] 8.2 Add Provider leaf tests.
- [x] 8.3 Add Identity leaf tests.
- [x] 8.4 Add Security Posture leaf tests.
- [x] 8.5 Add Enabled Features leaf tests.

## 9. Menu registry audit

- [x] 9.1 Add `MenuRegistryAuditTests` for registered leaf editors.
- [x] 9.2 Require round-trip tests and validation declarations for every
  registered leaf editor.
- [x] 9.3 Exempt `ShowInMenu = false` leaves from config smoke-tape
  existence checks.
- [x] 9.4 Document that routed handoff entries are tested separately in the
  config command change.

## 10. Existing test suite preservation

- [x] 10.1 Keep current init smoke coverage passing.
- [x] 10.2 Keep current reverse-proxy/init coverage passing until the later
  config and init changes intentionally move it.

## 11. Quality gates

- [x] 11.1 `dotnet build` clean.
- [x] 11.2 `dotnet test` clean.
- [x] 11.3 `dotnet slopwatch analyze` clean.
- [x] 11.4 `./scripts/Add-FileHeaders.ps1 -Verify` clean.
- [x] 11.5 `openspec validate section-editor-abstraction --type change`
  passes.

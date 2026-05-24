## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Confirm proposal, design, and spec deltas describe a leaf-editor
  abstraction rather than a flat dashboard contract.
- [ ] 1.2 Confirm the artifacts reflect the locked split: `init` owns
  bootstrap and Identity; `config` owns post-install editing.
- [ ] 1.3 Run `openspec validate section-editor-abstraction --type change`
  and resolve issues.

## 2. Core abstraction

- [ ] 2.1 Add `ISectionEditor` with `SectionId`, `DisplayName`,
  `Category?`, `ShowInMenu`, `GetStatus`, `Summary`,
  `RelevantDoctorChecks`, and `CreateEditor`.
- [ ] 2.2 Add `SectionStatus`.
- [ ] 2.3 Add `SectionContribution` with explicit field and secret
  actions.
- [ ] 2.4 Add `[NoDoctorChecks]` justification support where truly needed.

## 3. Registry and exemption list

- [ ] 3.1 Add `SectionEditorRegistry` with duplicate-ID fail-fast.
- [ ] 3.2 Add `AddSectionEditor<TEditor>()` DI registration.
- [ ] 3.3 Add `SectionEditorExemptions` entries for synthetic/init-owned
  surfaces, including Identity.
- [ ] 3.4 Document that the registry is a leaf-editor registry and does
  not dictate the future dashboard IA.

## 4. Single-step orchestrator mode

- [ ] 4.1 Add single-step hosting to `WizardOrchestrator`.
- [ ] 4.2 Ensure save exits and cancel exits work without linear step-list
  navigation.
- [ ] 4.3 Add unit tests for single-step save and cancel.

## 5. Semantic merge-on-save plumbing

- [ ] 5.1 Refactor config writes to load existing config, apply
  contributions, and preserve unrelated sections semantically.
- [ ] 5.2 Refactor secret writes to preserve blank submissions, replace on
  non-blank, and remove only on explicit delete.
- [ ] 5.3 Preserve inactive values for exposure-mode and similar editors
  when they are not the active leaf being changed.
- [ ] 5.4 Add `ConfigFileHelper.SecretPresent(...)` without decrypting
  stored values.

## 6. ExistingConfig population

- [ ] 6.1 Populate `WizardContext.ExistingConfig` from on-disk config when
  init enters an editor flow that needs existing state.
- [ ] 6.2 Keep secrets out of the context entirely.
- [ ] 6.3 Document that this supports init-owned re-entry, not init as the
  main post-install editor.

## 7. Refactor bootstrap leaves

- [ ] 7.1 Refactor Provider to implement `ISectionEditor`
  (`ShowInMenu = false`; owned by init / routed provider command).
- [ ] 7.2 Refactor Identity to implement `ISectionEditor`
  (`ShowInMenu = false`; synthetic ID; init-owned).
- [ ] 7.3 Refactor Security Posture to implement `ISectionEditor`
  (`ShowInMenu = true`; reusable under `Security & Access`).
- [ ] 7.4 Refactor Enabled Features to implement `ISectionEditor`
  (`ShowInMenu = true`; separate from posture and audience profiles).
- [ ] 7.5 Ensure each refactored editor declares meaningful validation
  checks and produces `SectionContribution` output.

## 8. Round-trip test harness

- [ ] 8.1 Add `SectionEditorTestBase<TEditor>` with semantic round-trip,
  secret-preservation, and targeted update scenarios.
- [ ] 8.2 Add Provider leaf tests.
- [ ] 8.3 Add Identity leaf tests.
- [ ] 8.4 Add Security Posture leaf tests.
- [ ] 8.5 Add Enabled Features leaf tests.

## 9. Menu registry audit

- [ ] 9.1 Add `MenuRegistryAuditTests` for registered leaf editors.
- [ ] 9.2 Require round-trip tests and validation declarations for every
  registered leaf editor.
- [ ] 9.3 Exempt `ShowInMenu = false` leaves from config smoke-tape
  existence checks.
- [ ] 9.4 Document that routed handoff entries are tested separately in the
  config command change.

## 10. Existing test suite preservation

- [ ] 10.1 Keep current init smoke coverage passing.
- [ ] 10.2 Keep current reverse-proxy/init coverage passing until the later
  config and init changes intentionally move it.

## 11. Quality gates

- [ ] 11.1 `dotnet build` clean.
- [ ] 11.2 `dotnet test` clean.
- [ ] 11.3 `dotnet slopwatch analyze` clean.
- [ ] 11.4 `./scripts/Add-FileHeaders.ps1 -Verify` clean.
- [ ] 11.5 `openspec validate section-editor-abstraction --type change`
  passes.

## ADDED Requirements

### Requirement: Section editor interface

The CLI SHALL define a `ISectionEditor` contract in
`Netclaw.Cli.Tui.Sections` that describes a single editable configuration
section. Each implementation SHALL declare a stable `SectionId` whose value
matches a schema key in `netclaw-config.v1.schema.json` (dotted-path form is
permitted for nested sections such as `Daemon.ExposureMode` and
`Tools.AudienceProfiles`), a user-facing `DisplayName`, an optional
`Category` grouping label, a `GetStatus` method returning
`SectionStatus.{Default, Configured, Warning, Error, Missing}` from current
on-disk config, a secret-redacting `Summary` for dashboard display, a
non-empty `RelevantDoctorChecks` collection (or an explicit
`[NoDoctorChecks]` justification attribute), and a `CreateEditor`
factory that returns an `IWizardStepViewModel`.

#### Scenario: Editor declares schema-keyed identity

- **WHEN** a class implements `ISectionEditor`
- **THEN** its `SectionId` resolves to a top-level or dotted-path key in
  `netclaw-config.v1.schema.json`
- **AND** the audit (defined under "Menu registry audit") fails if the
  identifier resolves to no schema key and the section is not on the
  documented exemption list

#### Scenario: Editor exposes status and summary without decrypting secrets

- **GIVEN** an `ISectionEditor` whose section owns a secret in `secrets.json`
- **WHEN** the editor produces `GetStatus(...)` and `Summary(...)`
- **THEN** the returned status reflects on-disk configured/default/error
  state
- **AND** the summary string contains no secret value or last-N characters
  of any secret

#### Scenario: Editor declares relevant doctor checks

- **WHEN** a class implements `ISectionEditor`
- **THEN** `RelevantDoctorChecks` contains at least one doctor check type,
  OR the implementing class is annotated with
  `[NoDoctorChecks(justification: "<reason>")]`
- **AND** the audit fails when neither condition holds

#### Scenario: Editor produces a step viewmodel that the orchestrator can run

- **GIVEN** an `ISectionEditor` and a `WizardContext`
- **WHEN** `CreateEditor(context)` is invoked
- **THEN** the returned `IWizardStepViewModel` is runnable inside the
  existing `WizardOrchestrator`
- **AND** it is also runnable in single-step orchestrator mode (see
  "Single-step orchestrator")

### Requirement: Section editor registry

The CLI SHALL provide a DI-discovered `SectionEditorRegistry` holding every
registered `ISectionEditor`. Registration SHALL occur via the extension
method `services.AddSectionEditor<TEditor>()`. The registry SHALL expose at
minimum `IReadOnlyList<ISectionEditor> All()` and
`ISectionEditor Get(string sectionId)`. Section identity SHALL be unique
within the registry.

#### Scenario: Editors are resolved via dependency injection

- **GIVEN** a DI container with `AddSectionEditor<ProviderSectionEditor>()`
  invoked at startup
- **WHEN** the container resolves `SectionEditorRegistry`
- **THEN** `registry.All()` returns a list containing the registered editor
- **AND** `registry.Get("Providers")` returns the same instance

#### Scenario: Duplicate section identity is rejected

- **GIVEN** two `ISectionEditor` implementations claiming the same
  `SectionId`
- **WHEN** the DI container builds the registry
- **THEN** registry construction fails fast with an exception naming the
  duplicate identifier

### Requirement: Section editor exemption list

The CLI SHALL maintain a documented exemption list at
`Netclaw.Cli.Tui.Sections.SectionEditorExemptions` enumerating schema
sections that intentionally have no TUI editor. Each entry SHALL carry a
machine-readable category (e.g. "internal-only", "set-once-at-install",
"covered by CLI subcommand", "covered by another editor", "out of MVP
scope"). The exemption list SHALL be the only mechanism by which an
unregistered schema section avoids audit failure.

#### Scenario: Schema section absent from registry and absent from exemptions

- **GIVEN** the schema declares a top-level section `Foo`
- **AND** no `ISectionEditor` implementation has `SectionId = "Foo"`
- **AND** `"Foo"` is not present in `SectionEditorExemptions`
- **WHEN** the menu registry audit runs
- **THEN** the audit fails with a message naming the section

#### Scenario: Schema section in exemption list

- **GIVEN** the schema declares a top-level section `Persistence`
- **AND** no editor exists for it
- **AND** `"Persistence"` is present in `SectionEditorExemptions` with
  category `"set-once-at-install"`
- **WHEN** the audit runs
- **THEN** the audit does not fail for `Persistence`

### Requirement: Single-step orchestrator mode

`WizardOrchestrator` SHALL support construction with a single
`IWizardStepViewModel` and a `WizardContext`, running that step
standalone without the linear-wizard step list. `GoNext()` from the step
SHALL invoke save-and-exit semantics; `GoBack()` or `Esc` SHALL invoke
cancel-and-exit semantics. `IsApplicable` filtering and step-to-step
navigation SHALL be skipped in this mode.

#### Scenario: Single step runs to save

- **GIVEN** a section editor's step viewmodel and a context
- **WHEN** a `WizardOrchestrator` is constructed in single-step mode
- **AND** the step invokes `GoNext()`
- **THEN** the orchestrator runs the save path
- **AND** returns control to the caller after disk write completes

#### Scenario: Single step cancels without saving

- **GIVEN** a section editor in single-step mode
- **WHEN** the step invokes `GoBack()` or the user presses Esc
- **THEN** the orchestrator returns without writing
- **AND** disk state is unchanged

### Requirement: Reentrancy contract

Every `ISectionEditor` SHALL honor the following reentrancy contract:
on `OnEnter(context, NavigationDirection.Forward)`, if
`context.ExistingConfig` is non-null, the editor SHALL read its slice
keyed by `SectionId` and pre-fill non-secret UI fields from that slice;
secret-bearing fields SHALL remain empty, with the documented hint text
indicating whether the underlying secret is present.

#### Scenario: Non-secret fields pre-fill from ExistingConfig

- **GIVEN** an editor with `SectionId = "Search"`
- **AND** `context.ExistingConfig["Search"]` contains
  `{ "Backend": "brave" }`
- **WHEN** the editor's step viewmodel enters in the Forward direction
- **THEN** the backend selector renders with `brave` as the
  current/selected value

#### Scenario: Secret-bearing fields render empty regardless of disk state

- **GIVEN** an editor with a secret-bearing field whose underlying value is
  stored encrypted in `secrets.json`
- **WHEN** the editor enters in the Forward direction
- **THEN** the secret input field renders empty
- **AND** the field hint reads "configured — leave blank to keep" when the
  underlying secret exists, or "(not set)" otherwise

### Requirement: Secret-handling contract

Section editors SHALL render every secret-bearing field as an empty masked
input. Blank-on-save SHALL preserve the existing encrypted secret value
without rewriting it. Non-blank-on-save SHALL replace the existing value
with the newly entered one. An explicit "Remove credential" action SHALL
be the only path that deletes a secret value from `secrets.json`. Under no
circumstance SHALL the decrypted value of a stored secret be displayed to
the user.

#### Scenario: Blank submit preserves existing secret

- **GIVEN** an editor with a secret-bearing field that has a stored value
- **WHEN** the user leaves the field empty and saves
- **THEN** the merge writer records `SecretAction.Preserve` for the field
- **AND** `secrets.json` is byte-identical for that key after the write

#### Scenario: Non-blank submit replaces stored secret

- **GIVEN** an editor with a secret-bearing field that has a stored value
- **WHEN** the user enters a new masked value and saves
- **THEN** the merge writer records `SecretAction.Replace(newValue)`
- **AND** `secrets.json` is rewritten with the new encrypted value at the
  corresponding key

#### Scenario: Remove credential deletes stored secret

- **GIVEN** an editor with a secret-bearing field that has a stored value
- **WHEN** the user activates "Remove credential" and confirms (default
  Cancel)
- **THEN** the merge writer records `SecretAction.Remove`
- **AND** the corresponding key is absent from the rewritten `secrets.json`

### Requirement: Merge-on-save semantics

Section editors SHALL produce a `SectionContribution` carrying explicit
`FieldAction.{Preserve, Replace, Remove}` per non-secret field and
`SecretAction.{Preserve, Replace, Remove}` per secret field. The merge
writer SHALL load existing `netclaw.json` and `secrets.json` as mutable
dictionaries, apply the contribution's actions to the editor's section,
and write the resulting documents. After a section save, every other
top-level section in both files SHALL be byte-identical to its pre-save
state.

#### Scenario: Editing one section preserves all others

- **GIVEN** `netclaw.json` contains sections `Providers`, `Slack`, `Search`,
  `ExposureMode`
- **WHEN** the user opens the Search editor, modifies the `Backend` field,
  and saves
- **THEN** `Providers`, `Slack`, `ExposureMode` are byte-identical in the
  resulting file
- **AND** only `Search` has changed

#### Scenario: Empty-array semantic distinct from missing key

- **GIVEN** an editor for a section containing a multi-value list
- **WHEN** the user removes all entries and saves
- **THEN** the resulting `netclaw.json` writes the list as an empty array
  `[]`
- **AND** the corresponding schema key is present and not removed

### Requirement: Existing-config population at init entry

When `netclaw init` launches, the entry point SHALL load
`netclaw.json` and `secrets.json` via `ConfigFileHelper.LoadConfigFiles`
and assign the parsed `netclaw.json` dictionary to
`WizardContext.ExistingConfig`. Secret values from `secrets.json` SHALL
NOT be loaded into the context; only an existence indicator (via
`ConfigFileHelper.SecretPresent(...)`) SHALL be queryable by editors.

#### Scenario: First-run leaves ExistingConfig null

- **GIVEN** no `netclaw.json` exists on disk
- **WHEN** `netclaw init` enters the wizard
- **THEN** `WizardContext.ExistingConfig` is `null`

#### Scenario: Re-run populates ExistingConfig

- **GIVEN** `netclaw.json` exists on disk
- **WHEN** `netclaw init` enters the wizard
- **THEN** `WizardContext.ExistingConfig` contains the parsed top-level
  dictionary
- **AND** no decrypted secret values are present anywhere in the context

### Requirement: Secret-presence lookup without decryption

`ConfigFileHelper` SHALL expose a method
`bool SecretPresent(NetclawPaths paths, string sectionId, string key)`
that returns whether the specified secret key exists in `secrets.json`
without decrypting or returning its value. The method SHALL be the sole
hint source for editors deciding between "configured — leave blank to
keep" and "(not set)" placeholders.

#### Scenario: Existing secret reports present

- **GIVEN** `secrets.json` contains an encrypted value at
  `Search.BraveApiKey`
- **WHEN** `SecretPresent(paths, "Search", "BraveApiKey")` is invoked
- **THEN** the result is `true`
- **AND** the decrypted value is never materialized in memory by this call

#### Scenario: Missing secret reports absent

- **GIVEN** `secrets.json` does not contain a value at
  `Search.BraveApiKey`
- **WHEN** `SecretPresent(paths, "Search", "BraveApiKey")` is invoked
- **THEN** the result is `false`

### Requirement: Round-trip test harness

The test project SHALL provide an abstract
`SectionEditorTestBase<TEditor>` carrying the canonical shared
reentrancy and merge scenarios: `RoundTrip_NoOpEdit_PreservesConfig`,
`RoundTrip_SingleFieldEdit_UpdatesOnlyThatField`,
`Secrets_BlankSubmit_PreservesExistingSecret`,
`Secrets_NonBlankSubmit_ReplacesSecret`,
`Secrets_RemoveAction_DeletesSecret`. Concrete subclasses SHALL exist for
every registered `ISectionEditor`.

#### Scenario: Base scenarios are inherited by every concrete subclass

- **WHEN** a developer adds a new `ISectionEditor` implementation and
  registers it
- **THEN** the project will not pass `dotnet test` until a corresponding
  subclass of `SectionEditorTestBase<TEditor>` exists
- **AND** the menu registry audit fails when the subclass is missing

#### Scenario: Round-trip no-op preserves config byte-for-byte

- **GIVEN** a stocked existing-config fixture
- **WHEN** the editor's step viewmodel runs `OnEnter`, makes no changes,
  and saves
- **THEN** the resulting `netclaw.json` and `secrets.json` are
  byte-identical to the fixture

### Requirement: Menu registry audit

The test project SHALL include `MenuRegistryAuditTests` that walks
`SectionEditorRegistry` and asserts, for every registered editor: a
matching concrete `SectionEditorTestBase<TEditor>` subclass exists, the
editor's `RelevantDoctorChecks` is non-empty (or the class is annotated
with `[NoDoctorChecks]`), and — once smoke tapes ship for the editor in
the next change — a matching tape file exists at
`tests/smoke/tapes/config-<section-lowercase>.tape`. The audit SHALL
report all failures in one assertion message naming each missing
artifact.

#### Scenario: Missing round-trip test class fails the audit

- **GIVEN** a registered `ISectionEditor` without a matching
  `SectionEditorTestBase<TEditor>` subclass
- **WHEN** `MenuRegistryAuditTests` runs
- **THEN** the test fails with a message naming the missing test class

#### Scenario: Empty RelevantDoctorChecks without justification fails the audit

- **GIVEN** a registered `ISectionEditor` whose `RelevantDoctorChecks`
  returns no entries
- **AND** whose class is not annotated with `[NoDoctorChecks]`
- **WHEN** `MenuRegistryAuditTests` runs
- **THEN** the test fails with a message naming the editor

#### Scenario: Vacuous registry passes the audit

- **GIVEN** a registry containing only the three Change A editors
  (Provider, Identity, Posture)
- **AND** each has a matching round-trip test class and non-empty
  `RelevantDoctorChecks`
- **WHEN** `MenuRegistryAuditTests` runs
- **THEN** the audit passes

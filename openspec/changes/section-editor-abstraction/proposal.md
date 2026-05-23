## Why

Netclaw's `netclaw init` wizard is a linear forward-pass over a hardcoded step
sequence with no reentrancy: re-running it over an existing install is
undefined, and changing one configuration knob requires editing
`netclaw.json` by hand. Existing single-section CLI editors
(`netclaw provider`, `netclaw model`, `netclaw mcp`) prove the load-merge-write
pattern works, but they duplicate logic with the wizard rather than sharing it.
This change introduces the shared abstraction that both the init wizard and a
forthcoming `netclaw config` command (next change) will compose, completes the
long-deferred reentrancy of `netclaw init` (#455), and makes future config
knobs reentrant by construction.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`, `PRD-001-netclaw-mvp.md`.

## What Changes

- Add a `ISectionEditor` interface in `Netclaw.Cli.Tui.Sections`. Each instance
  describes one editable configuration section: schema-keyed identity,
  dashboard summary, status badge computation, relevant doctor checks, and a
  factory that returns a `IWizardStepViewModel` runnable either by the wizard
  orchestrator or standalone.
- Add `SectionEditorRegistry`, `SectionStatus`, `SectionContribution`
  (carrying explicit `FieldAction` and `SecretAction` per field), and
  `SectionEditorExemptions` (documented opt-outs for schema sections that
  intentionally have no TUI editor).
- Add a single-step constructor to `WizardOrchestrator` so a section editor can
  be run outside the linear wizard with the same lifecycle, save, and cancel
  semantics.
- Populate `WizardContext.ExistingConfig` at `netclaw init` entry when an
  existing `netclaw.json` is present. Each refactored section editor's
  `OnEnter()` pre-fills non-secret fields from its slice.
- Switch `WizardConfigBuilder.WriteConfigFile()` from "build fresh +
  overwrite" to "load existing + merge + write," matching the pattern already
  used by `ProviderCredentialWriter`. Apply the same load-merge-write rule to
  the secrets writer.
- Refactor three existing init step viewmodels — Provider, Identity,
  SecurityPosture — to implement `ISectionEditor`. Behavior inside the linear
  init wizard is unchanged for first-run; reentrant pre-population is gained
  for the next change's config command.
- Establish day-one reentrancy contracts in code: secrets never rehydrate
  to screen (masked input with "leave blank to keep" semantics), and
  section saves preserve every other top-level section in `netclaw.json` and
  `secrets.json` byte-for-byte.
- Add a `MenuRegistryAuditTests` xUnit test that walks the registry and
  asserts each registered editor declares non-empty `RelevantDoctorChecks`
  (or carries an explicit `[NoDoctorChecks]` justification attribute), has a
  registered round-trip test class, and — once the config command lands in
  the next change — has a matching smoke tape. In this change the audit runs
  vacuously over a registry containing the three refactored editors.
- Add a `SectionEditorTestBase<TEditor>` xUnit harness with shared round-trip
  scenarios: `RoundTrip_NoOpEdit_PreservesConfig`,
  `RoundTrip_SingleFieldEdit_UpdatesOnlyThatField`,
  `Secrets_BlankSubmit_PreservesExistingSecret`,
  `Secrets_NonBlankSubmit_ReplacesSecret`,
  `Secrets_RemoveAction_DeletesSecret`. Concrete subclasses for the three
  refactored editors are included.
- Add `ConfigFileHelper.SecretPresent(paths, section, key)` so editors can
  render "configured — leave blank to keep" hints without decrypting the
  secret value (#455 contract: never rehydrate secrets to the screen).
- Closes #455 (`netclaw init` reentrancy gap).

**In scope (MVP):** the abstraction, registry, exemption list, audit and
round-trip test harnesses, single-step orchestrator mode, merge-on-save for
both `netclaw.json` and `secrets.json`, `ExistingConfig` population at init
entry, and refactor of Provider/Identity/Posture to implement the contract.

**Out of scope:** the new `netclaw config` command itself (next change), the
remaining nine section editors (next change), simplification of the init
wizard step list (third change), and hot-reload of the running daemon on
config changes.

## Capabilities

### New Capabilities

- `section-editor-abstraction`: contract requirements for the reusable
  editable-section abstraction — `ISectionEditor`, registry semantics,
  reentrancy contract, secret-handling contract, merge-on-save semantics,
  and audit obligations for every registered editor.

### Modified Capabilities

- `netclaw-onboarding`: `netclaw init` SHALL populate `WizardContext.ExistingConfig`
  at entry from on-disk config, and section editors SHALL pre-fill non-secret
  fields from it in `OnEnter()` while leaving secret fields empty with the
  documented "configured" hint. The wizard's terminal write SHALL be a merge
  over existing config, not an overwrite.

## Impact

**Affected systems:**

- CLI init wizard wiring (`Netclaw.Cli.Program`,
  `Netclaw.Cli.Tui.Wizard.WizardOrchestrator`,
  `Netclaw.Cli.Tui.Wizard.WizardConfigBuilder`,
  `Netclaw.Cli.Tui.Wizard.WizardContext`).
- Three init step viewmodels (`ProviderStepViewModel`, `IdentityStepViewModel`,
  `SecurityPostureStepViewModel`) gain `ISectionEditor` implementations.
- Config merge helper (`Netclaw.Cli.Config.ConfigFileHelper`) gains
  `SecretPresent(...)`.
- New test surface under `tests/Netclaw.Cli.Tests/Tui/Sections/` covering the
  abstraction and the three refactored editors.

**Security and operational impact:**

- Secrets are never re-rendered to the TUI; the new `SecretPresent` lookup
  returns existence only, never the decrypted value. This preserves the
  default-deny posture for credential display.
- Merge-on-save replaces overwrite-on-save. The contract guarantee is
  byte-equality of all other top-level sections in `netclaw.json` and
  `secrets.json`. Round-trip tests enforce the guarantee.
- Re-running `netclaw init` over an existing config is no longer undefined;
  in this change the wizard pre-fills fields and merges on save. Explicit
  "existing-config refusal" UX lands in the third change.
- No new network surface, no new persistence schema, no new daemon
  contract changes.

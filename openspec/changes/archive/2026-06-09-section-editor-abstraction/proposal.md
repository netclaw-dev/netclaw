## Why

Netclaw needs one reusable editing contract for bootstrap-only init flows
and for the heavier post-install `netclaw config` command, but the product
split is now locked:

- `netclaw init` is first-run bootstrap and then rarely used again.
- `netclaw config` is the main post-install settings surface.
- Identity remains `netclaw init` owned.

That means the shared abstraction cannot assume a flat dashboard, cannot
assume every section is menu-editable, and cannot promise byte-identical
JSON preservation as a product contract. It needs to support reusable leaf
editors, routed handoffs, semantic merge-on-save, and init-owned surfaces
that are intentionally absent from `netclaw config`.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`,
`PRD-001-netclaw-mvp.md`.

## What Changes

- Add an `ISectionEditor` contract in `Netclaw.Cli.Tui.Sections` for
  reusable leaf editors. Each editor describes one editable leaf surface:
  stable identity, status/summary, relevant validation checks, and a
  factory that returns an `IWizardStepViewModel` runnable either from
  `netclaw init` or from `netclaw config`.
- Keep the registry flat at the leaf-editor level, but explicitly DO NOT
  make the registry shape the `netclaw config` IA contract. The next change
  is free to build a domain-oriented dashboard with grouped pages and
  routed handoffs on top of the leaf registry.
- Add `SectionEditorRegistry`, `SectionStatus`, `SectionContribution`, and
  `SectionEditorExemptions` so schema-backed leaves, dotted-path leaves,
  and synthetic init-owned editors can all participate without pretending
  everything is a top-level config page.
- Add single-step `WizardOrchestrator` hosting so one editor can run
  standalone without the full init step list.
- Populate `WizardContext.ExistingConfig` from on-disk config when an
  init-owned flow needs existing state, so init-owned editors can re-enter
  with non-secret fields prefilled.
- Switch wizard/config persistence from overwrite semantics to semantic
  merge-on-save. Unrelated sections and inactive per-mode values are
  preserved semantically; formatting and property ordering are not part of
  the contract.
- Refactor four existing bootstrap editors to implement `ISectionEditor`:
  Provider, Identity, Security Posture, and Enabled Features. Identity
  remains `ShowInMenu = false` because it stays init-owned. Security
  Posture and Enabled Features become reusable leaf editors for the next
  change's `Security & Access` area.
- Keep the secret-handling contract: secrets never rehydrate to screen;
  masked inputs use "leave blank to keep" semantics; explicit removal is
  the only delete path.
- Add `MenuRegistryAuditTests` and `SectionEditorTestBase<TEditor>` so
  registered leaf editors require meaningful round-trip coverage and
  validation declarations. Routed handoff entries in the next change are
  covered separately and do not pretend to be leaf editors.

**In scope (MVP):** the abstraction, registry, exemption list, audit and
round-trip harnesses, single-step orchestrator mode, semantic merge-on-save
for `netclaw.json` and `secrets.json`, `ExistingConfig` population, and the
refactor of Provider / Identity / Security Posture / Enabled Features.

**Out of scope:** the `netclaw config` command itself, the domain-oriented
dashboard IA, routed handoff nodes for `netclaw provider` / `netclaw model`
 / `netclaw mcp permissions`, simplification of the init flow, and daemon
hot-reload.

## Capabilities

### New Capabilities

- `section-editor-abstraction`: reusable leaf-editor contract for init and
  config, including reentrancy, secret handling, semantic merge-on-save,
  and audit obligations.

### Modified Capabilities

- `netclaw-onboarding`: init-owned editable flows SHALL load existing
  config state when needed, prefill non-secret fields, keep secrets masked,
  and write via semantic merge-on-save.

## Impact

**Affected systems:**

- CLI wizard infrastructure (`Program`, `WizardOrchestrator`,
  `WizardConfigBuilder`, `WizardContext`).
- Bootstrap editors (`ProviderStepViewModel`, `IdentityStepViewModel`,
  `SecurityPostureStepViewModel`, `FeatureSelectionStepViewModel` / its
  Enabled Features successor naming).
- Config merge helper (`ConfigFileHelper`).
- Test surface under `tests/Netclaw.Cli.Tests/Tui/Sections/`.

**Security and operational impact:**

- Secrets remain non-rehydratable in the UI.
- Merge behavior preserves meaning, not file bytes.
- The abstraction now matches the locked product split instead of
  implying that `netclaw init` is the long-term editor for ongoing
  settings.

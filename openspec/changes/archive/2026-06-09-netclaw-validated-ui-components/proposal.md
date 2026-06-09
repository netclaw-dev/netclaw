## Why

Recent `netclaw config` regressions prove that validation is still a
convention instead of an architectural constraint: pages can render an input,
handle `Enter`, call save/autosave directly, and bypass static or dynamic
validation. The fix must move validation and commit behavior into reusable,
page-independent Netclaw UI components so missing validation fails at compile
or build time instead of relying on repeated human reminders.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`,
`PRD-001-netclaw-mvp.md`, `PRD-002-gateway-security-envelope.md`.

## What Changes

- Add page-independent Netclaw TUI commit components named with `NetclawUi*`
  and `NetclawValidated*`, not `Config*`, so the validation contract can be
  reused by config, onboarding, provider, model, MCP, and future operator UI
  surfaces.
- Introduce one mandatory mutation contract, `NetclawUiCommit<TDraft>`, that
  carries draft access, static validation, explicit dynamic validation policy,
  persistence, and post-commit behavior.
- Introduce `NetclawUiCommitPipeline` as the only path for persisting mutable
  UI actions. `Enter`, save/apply actions, autosave, toggles, pickers, delete,
  reset, token rotation, and confirmed destructive actions all go through this
  pipeline.
- Introduce `NetclawUiDynamicCheck<TDraft>` with two explicit states:
  `Required(...)` or `NotApplicable(justification)`. Dynamic validation can no
  longer be silently absent.
- Introduce standard components such as `NetclawValidatedTextField`,
  `NetclawValidatedAction<TDraft>`, `NetclawValidatedToggle`, and
  `NetclawValidatedPicker<TValue>` that require a `NetclawUiCommit<TDraft>` in
  their constructors.
- Introduce a validated page/input router so pages render and compose
  components, while standard components own key handling for typed input,
  paste, `Enter`, `Space`, picker selection, and autosave triggers.
- Add build-time enforcement, preferably a Roslyn analyzer with architecture
  tests as a backstop, that fails when mutable TUI pages bypass the standard
  components or commit pipeline.
- Migrate `netclaw config` pages to the standard Netclaw UI components,
  starting with Skill Sources, Telemetry & Alerting, Workspaces Directory,
  Inbound Webhooks, Channels, Search, Browser Automation, and Exposure Mode.
- Delete old tests, helper components, page-level input handlers, and UI
  helpers only when they are no longer needed: the replacement component must
  cover the same behavior, no callers may remain, and focused tests must prove
  the replacement path.

**BREAKING internal architecture change:** config pages and view models SHALL
NOT persist mutable UI actions by calling `Save`, `SaveAsync`, `ConfigAutosave`,
or config writers directly from page input handlers. Those paths must move to
`NetclawUiCommitPipeline` or become rejected by build enforcement.

**In scope (MVP):** page-independent validated TUI commit primitives,
config-surface migration, enforcement against direct save/autosave bypasses,
replacement tests, native smoke coverage for migrated config flows, and removal
of obsolete tests/components proven redundant by the migration.

**Out of scope:** visual redesign of the config IA, broad init simplification,
new persisted config shape, new runtime capabilities unrelated to validation,
and deleting still-needed tests or components merely because they predate this
change.

## Capabilities

### New Capabilities

- `netclaw-validated-ui-components`: page-independent TUI mutation components,
  commit pipeline, dynamic validation policy, autosave/Enter unification, and
  build-time bypass enforcement.

### Modified Capabilities

- `netclaw-config-command`: config leaf editors must consume the validated
  Netclaw UI components for mutable input and completed actions, and must prove
  static validation, dynamic validation, autosave, persistence, and runtime
  consumer contracts through the same user-action paths.
- `section-editor-abstraction`: leaf editor hosting must support validated UI
  component composition without implying that pages can hand-roll input/save
  behavior.

## Impact

**Affected code and APIs:**

- New reusable TUI component namespace, expected under `Netclaw.Cli.Tui` with
  names such as `NetclawUiCommit<TDraft>`, `NetclawUiCommitPipeline`, and
  `NetclawValidatedTextField`.
- Existing config pages under `src/Netclaw.Cli/Tui/Config/*ConfigPage.cs`.
- Existing config view models that currently expose direct `Save`, `SaveAsync`,
  `ActivateSelected`, `AppendText`, `Backspace`, and autosave entry points.
- Existing helpers such as `WorkflowViewComponents`, `NetclawTuiChrome`, raw
  `TextInputNode` usage, and `ConfigAutosave` call sites.
- Headless Termina tests, config editor audit tests, native smoke tapes, and
  semantic assertion scripts.

**Security and operational impact:**

- Invalid config, unresolved runtime references, bad credentials, unreachable
  dependencies, and malformed secret changes are blocked before persistence.
- Dynamic validation failures cannot disappear by accident; each mutable action
  declares a required dynamic check or an explicit not-applicable reason.
- Autosave no longer has a separate bypass path from explicit save/apply.
- Runtime-bound config writes continue to require consumer-facing proof that
  the persisted shape is canonical and daemon/runtime code can consume it.
- Operators get consistent behavior across config leaves: completed actions
  save after validation, incomplete drafts do not persist, and failures are
  visible.

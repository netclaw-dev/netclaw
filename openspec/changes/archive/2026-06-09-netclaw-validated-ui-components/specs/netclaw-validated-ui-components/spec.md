## ADDED Requirements

### Requirement: Mutable UI actions require a Netclaw UI commit contract

Every mutable Netclaw TUI action SHALL be represented by a
`NetclawUiCommit<TDraft>` or an equivalent standard contract with no
constructor path that omits static validation, dynamic validation policy,
persistence, and post-commit behavior.

The contract SHALL be page-independent and named with `NetclawUi*` or
`NetclawValidated*` terminology, not config-specific names. Config pages MAY
adapt domain validators and writers into the contract, but the reusable UI
component contract SHALL NOT depend on config page types.

#### Scenario: Editable field cannot be constructed without validation hooks

- **WHEN** a developer adds a mutable text field to a TUI page
- **THEN** the standard field constructor requires a `NetclawUiCommit<string>`
- **AND** the code cannot compile using only a label, current value, and raw
  save callback

#### Scenario: Toggle cannot persist without a commit contract

- **WHEN** a developer adds a boolean toggle that changes persisted state
- **THEN** the toggle component requires a `NetclawUiCommit<bool>`
- **AND** persistence cannot be wired directly from the page input handler

### Requirement: Dynamic validation policy is explicit

Every `NetclawUiCommit<TDraft>` SHALL declare a dynamic validation policy.
The policy SHALL be either required dynamic validation or explicitly not
applicable with a non-empty justification. Silent omission of dynamic
validation SHALL be rejected by construction or by build-time enforcement.

#### Scenario: Dynamic check is required for remote probes

- **GIVEN** a mutable action edits a remote skill server URL
- **WHEN** the action is declared
- **THEN** its commit contract declares a required dynamic check that probes
  the skill feed discovery endpoint before persistence

#### Scenario: Not-applicable dynamic check requires justification

- **GIVEN** a mutable action changes a purely local display preference
- **WHEN** the action is declared without a live probe
- **THEN** its commit contract uses `NotApplicable` with a non-empty reason
- **AND** an empty justification fails validation or build enforcement

### Requirement: One commit pipeline owns Enter, save, autosave, and completed actions

The `NetclawUiCommitPipeline` SHALL be the only persistence path for mutable
TUI actions. The pipeline SHALL accept a trigger that identifies whether the
commit came from `Enter`, save/apply, autosave, toggle, picker selection,
delete, reset, token rotation, or another completed action.

The pipeline SHALL run static validation before dynamic validation and SHALL
run all validation before persistence. Failed validation SHALL leave config,
secrets, and sidecar files unchanged.

#### Scenario: Enter and autosave use the same validation pipeline

- **GIVEN** a text field and a toggle both persist runtime-consumed settings
- **WHEN** the text field is accepted with `Enter`
- **AND** the toggle autosaves after selection
- **THEN** both actions run through `NetclawUiCommitPipeline`
- **AND** both actions run static validation before dynamic validation before
  persistence

#### Scenario: Static validation failure writes nothing

- **GIVEN** a mutable action has an invalid local path draft
- **WHEN** the action is committed
- **THEN** static validation fails
- **AND** dynamic validation is not called
- **AND** no persisted file is modified

#### Scenario: Dynamic validation failure writes nothing before override

- **GIVEN** a mutable action has a structurally valid remote URL
- **AND** its required probe fails
- **WHEN** the action is committed
- **THEN** persistence is blocked
- **AND** no persisted file is modified
- **AND** the result can expose a save-anyway path only when the action's
  failure policy allows runtime/probe override

### Requirement: Standard components own mutable input handling

Standard Netclaw validated components SHALL own mutable key handling for their
controls, including typed characters, paste, backspace, `Enter`, `Space`,
picker selection, and autosave triggers. TUI pages SHALL compose components
and render layout; pages SHALL NOT implement persistence behavior in key
handlers.

#### Scenario: Text input uses standard component handling

- **WHEN** a page renders a mutable text field
- **THEN** typed characters, paste, backspace, and `Enter` are handled by
  `NetclawValidatedTextField` or the standard validated input router
- **AND** accepting the field invokes `NetclawUiCommitPipeline`

#### Scenario: Page-level Enter save bypass is rejected

- **WHEN** a config page handles `ConsoleKey.Enter` and calls `Save`,
  `SaveAsync`, `ConfigAutosave`, or a config writer directly
- **THEN** build enforcement fails
- **AND** the implementation must move the action behind a validated component
  and `NetclawUiCommit<TDraft>`

### Requirement: Build enforcement rejects validation bypasses

The build SHALL include enforcement that detects mutable TUI bypasses. The
preferred enforcement is a Roslyn analyzer; architecture tests MAY be used as
a backstop but SHALL NOT be the only long-term protection if analyzer coverage
is feasible.

Build enforcement SHALL reject raw mutable `TextInputNode` construction,
direct save/autosave calls, direct config writer calls, and direct
`ConsoleKey.Enter` persistence handling in mutable TUI pages unless the code
is inside the standard Netclaw validated component layer or commit pipeline.

#### Scenario: Raw input construction fails outside standard components

- **WHEN** a mutable TUI page instantiates `TextInputNode` directly for a
  persisted field
- **THEN** build enforcement fails
- **AND** the page must use `NetclawValidatedTextField` or an approved
  standard component

#### Scenario: Direct autosave call fails outside commit pipeline

- **WHEN** a mutable TUI page or view model calls `ConfigAutosave` directly for
  a persisted action
- **THEN** build enforcement fails unless the call is part of the approved
  `NetclawUiCommitPipeline` implementation

### Requirement: Obsolete UI artifacts are deleted only after replacement proof

Old tests, helper components, and page-specific input handlers SHALL be
removed only when they are not needed. A removal is allowed only after the
replacement standard component covers the behavior, no production caller
remains, and tests prove the replacement path through the public user action.

#### Scenario: Obsolete render-only test is replaced by interaction proof

- **GIVEN** an old test checks only that an input label renders
- **WHEN** a validated component test covers typed input, paste, `Enter`,
  failed validation, and unchanged persistence
- **THEN** the render-only test MAY be deleted if no unique visual contract is
  lost

#### Scenario: Still-needed helper remains until callers migrate

- **GIVEN** an old UI helper still has production callers not yet migrated
- **WHEN** the cleanup phase runs
- **THEN** the helper remains
- **AND** deletion is deferred until caller migration and replacement coverage
  are complete

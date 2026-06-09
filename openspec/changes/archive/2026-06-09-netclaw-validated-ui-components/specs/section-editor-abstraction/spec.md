## ADDED Requirements

### Requirement: Section editor hosting supports validated component composition

Section editor hosting SHALL support composing page-independent validated
Netclaw UI components. The section editor abstraction SHALL NOT imply that a
leaf page may hand-roll mutable input, direct save behavior, or autosave
persistence outside the standard commit pipeline.

#### Scenario: Leaf editor supplies validated components to host

- **GIVEN** a leaf editor exposes mutable fields or completed actions
- **WHEN** the editor is hosted by init, config, or another page shell
- **THEN** mutable controls are represented by standard validated Netclaw UI
  components or by declarations that the host adapts to those components

#### Scenario: Host navigation does not own persistence

- **GIVEN** a section editor is hosted in the config dashboard
- **WHEN** the operator presses navigation keys such as `Esc`
- **THEN** the host routes navigation
- **AND** persistence remains owned only by validated commit actions

### Requirement: Leaf editor audits include validated commit coverage

Leaf editor audit tests SHALL require every mutable section editor to declare
validated commit coverage. The audit SHALL distinguish replacement coverage
from obsolete coverage so that old tests are deleted only when they are no
longer needed.

#### Scenario: Mutable leaf without commit coverage fails audit

- **GIVEN** a registered leaf editor has a mutable field
- **WHEN** the audit cannot find a corresponding validated component or
  `NetclawUiCommit<TDraft>` declaration
- **THEN** the audit fails

#### Scenario: Obsolete tests are removed only after replacement coverage

- **GIVEN** a legacy section-editor test covers a behavior through a direct
  view-model call
- **AND** a new validated component test covers the same behavior through the
  public user-action path
- **WHEN** no unique assertion remains in the legacy test
- **THEN** the legacy test MAY be deleted as no longer needed

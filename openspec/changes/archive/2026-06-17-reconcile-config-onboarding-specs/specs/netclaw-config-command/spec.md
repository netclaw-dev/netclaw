## MODIFIED Requirements

### Requirement: Config command launches a domain-oriented dashboard

`netclaw config` SHALL launch a domain-oriented settings dashboard. The
root SHALL be navigation-first and SHALL NOT be a flat list of every
registered leaf editor.

The root SHALL include:

- `Inference Providers`
- `Models`
- `Channels`
- `Inbound Webhooks`
- `Skill Sources`
- `Search`
- `Browser Automation`
- `Telemetry & Alerting`
- `Security & Access`
- `Workspaces Directory`

#### Scenario: Root dashboard shows domain entries

- **GIVEN** a configured install
- **WHEN** the operator runs `netclaw config`
- **THEN** the root dashboard opens with the documented domain entries
- **AND** `Workspaces Directory` appears as the tenth entry, after
  `Security & Access`
- **AND** it does not render a flat dump of every registered leaf editor

## ADDED Requirements

### Requirement: Channels area supports Slack, Discord, and Mattermost adapters

The `Channels` domain area SHALL support three channel adapters: Slack,
Discord, and Mattermost. Each adapter SHALL be independently enabled,
configured, and managed from the same Channels editor.

#### Scenario: Mattermost adapter is available alongside Slack and Discord

- **GIVEN** the operator opens the Channels config area
- **WHEN** the adapter list is rendered
- **THEN** Slack, Discord, and Mattermost each appear as configurable
  adapter entries
- **AND** enabling Mattermost leads to credential entry (server URL and
  bot token) followed by channel resolution

### Requirement: Directory pickers use an interactive file-picker widget

The Skill Sources local-folder add flow and the Workspaces Directory editor SHALL use an interactive directory picker.

The Skill Sources "add a local folder" flow and the Workspaces Directory
editor SHALL present a Termina `FilePickerNode` directory picker instead
of a typed path field. The picker SHALL be scoped to directories only and
SHALL fill the content area.

Selecting a directory in the picker SHALL save immediately
(autosave-on-selection) without requiring a separate confirm step.

A `Ctrl+N` affordance SHALL be available throughout both pickers. When
activated, it SHALL open an inline naming overlay that lets the operator
name and create a new folder inside the currently focused picker
directory. On successful creation the folder SHALL be selectable
immediately without restarting the picker. On `Esc` the naming overlay
SHALL be dismissed and the picker SHALL remain active.

#### Scenario: Selecting a directory in the Workspaces Directory picker saves immediately

- **GIVEN** the operator opens the Workspaces Directory editor
- **WHEN** the operator navigates the picker and confirms a directory
- **THEN** the selected path is saved to `Workspaces.Directory`
  immediately
- **AND** no separate save key is required

#### Scenario: Ctrl+N creates a new folder from within the directory picker

- **GIVEN** the operator is in a directory picker (Skill Sources or
  Workspaces Directory)
- **WHEN** the operator presses `Ctrl+N`, enters a folder name, and
  confirms with `Enter`
- **THEN** the folder is created inside the currently focused directory
- **AND** the naming overlay is dismissed
- **AND** the new folder is available for selection in the same picker
  session

#### Scenario: Esc cancels new-folder naming without affecting the picker

- **GIVEN** the operator has opened the new-folder naming overlay via
  `Ctrl+N`
- **WHEN** the operator presses `Esc`
- **THEN** the naming overlay is dismissed
- **AND** the directory picker remains active with no folder created

### Requirement: Inbound Webhooks editor manages global enablement and execution timeout

The Inbound Webhooks editor SHALL provide two editable settings:

- A global `Enabled` boolean toggle that persists to
  `Webhooks.Enabled`.
- An `ExecutionTimeoutSeconds` integer field (1–3600 seconds) that
  persists to `Webhooks.ExecutionTimeoutSeconds`.

Route authoring SHALL remain owned by the `netclaw webhooks` CLI
(`netclaw webhooks set|list|validate`). The editor SHALL NOT create,
edit, or delete route files. It SHALL display a live route summary
(total, enabled, disabled, invalid counts) so the operator can assess
configuration health without leaving the TUI.

Enabling the global toggle with no valid routes present SHALL still
persist `Webhooks.Enabled = true`. The editor SHALL surface a
non-blocking advisory directing the operator to run `netclaw webhooks
set` to add routes; it SHALL NOT block the save or require routes to
exist before enabling.

Saving SHALL be blocked only when `ExecutionTimeoutSeconds` contains a
structurally invalid value (non-integer, or outside 1–3600).

#### Scenario: Toggling Enabled with no routes persists true and shows advisory

- **GIVEN** the Inbound Webhooks editor is open
- **AND** no valid webhook routes exist
- **WHEN** the operator toggles `Enabled` to true and saves
- **THEN** `Webhooks.Enabled = true` is written to config
- **AND** a non-blocking advisory is shown instructing the operator to
  add a route with `netclaw webhooks set`
- **AND** the save is not blocked

#### Scenario: Invalid execution timeout blocks save

- **GIVEN** the operator has entered a non-integer or out-of-range value
  in the execution timeout field
- **WHEN** the operator saves
- **THEN** an error is shown describing the valid range
- **AND** no config file is modified

#### Scenario: Route summary reflects current route state without editor ownership

- **GIVEN** routes have been authored via `netclaw webhooks set`
- **WHEN** the operator opens the Inbound Webhooks editor
- **THEN** the summary row displays the current total, enabled,
  disabled, and invalid route counts
- **AND** the editor offers no affordance to create or modify route
  files directly

### Requirement: Search editor uses progressive disclosure per backend

The Search editor SHALL reveal only the configuration field relevant to
the selected backend:

- Selecting `Brave` SHALL reveal the Brave API key field (stored in
  `secrets.json`) and hide the SearXNG endpoint field.
- Selecting `SearXNG` SHALL reveal the SearXNG instance URL field
  (stored in `netclaw.json`) and hide the Brave API key field.
- Selecting `DuckDuckGo` SHALL hide both backend-specific fields, as
  DuckDuckGo requires no additional configuration.

Fields for inactive backends SHALL NOT be rendered in the editor or
prompted for input.

#### Scenario: Selecting Brave reveals only the Brave API key field

- **GIVEN** the operator opens the Search editor
- **WHEN** the operator selects `Brave` as the backend
- **THEN** the Brave API key input field is shown
- **AND** the SearXNG endpoint field is not shown

#### Scenario: Selecting SearXNG reveals only the SearXNG endpoint field

- **GIVEN** the operator opens the Search editor
- **WHEN** the operator selects `SearXNG` as the backend
- **THEN** the SearXNG instance URL field is shown
- **AND** the Brave API key field is not shown

#### Scenario: Selecting DuckDuckGo shows no backend-specific field

- **GIVEN** the operator opens the Search editor
- **WHEN** the operator selects `DuckDuckGo` as the backend
- **THEN** no backend-specific credential or endpoint field is shown
- **AND** saving requires no further input

## Purpose

Define the post-install `netclaw config` dashboard, its domain-oriented
navigation model, and the rules for how configuration editing routes or saves.
## Requirements
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

### Requirement: Missing install refuses before TUI startup

`netclaw config` SHALL detect a missing install/config before starting the
TUI. It SHALL print ``No configuration found. Run `netclaw init` first.``
to stderr and exit non-zero.

#### Scenario: No install refusal renders no TUI

- **GIVEN** `~/.netclaw/config/netclaw.json` does not exist
- **WHEN** the operator runs `netclaw config`
- **THEN** the command prints the refusal message to stderr
- **AND** exits non-zero
- **AND** no partial TUI is rendered

### Requirement: Routed handoffs are first-class config outcomes

The config dashboard SHALL allow specific domain entries to route into
existing commands instead of re-hosting the full editor inline. In this
branch, `Inference Providers` SHALL route to `netclaw provider` and
`Models` SHALL route to `netclaw model`.

#### Scenario: Inference Providers routes to provider command

- **GIVEN** the operator selects `Inference Providers`
- **WHEN** the handoff is activated
- **THEN** the flow routes to `netclaw provider`
- **AND** no config-dashboard back-stack refactor is required

### Requirement: Security & Access separates posture, features, profiles, and exposure

The `Security & Access` area SHALL contain separate entries for Security
Posture, Enabled Features, Audience Profiles, and Exposure Mode.

Security Posture, Enabled Features, and Audience Profiles SHALL remain
distinct concepts:

- Security Posture selects the deployment stance.
- Enabled Features controls deployment-wide runtime enablement.
- Audience Profiles edits curated per-audience high-level access rules.

#### Scenario: Team posture continues into enabled-features flow

- **GIVEN** the operator changes Security Posture to `Team`
- **WHEN** the posture change flow completes
- **THEN** the config flow continues into Enabled Features

#### Scenario: Personal posture skips enabled-features continuation

- **GIVEN** the operator changes Security Posture to `Personal`
- **WHEN** the posture change flow completes
- **THEN** the config flow does not force an Enabled Features continuation

### Requirement: Audience Profiles is curated and excludes MCP editing

The Audience Profiles editor SHALL be a curated high-level editor. It SHALL
focus on:

- Tool Access (non-MCP)
- File Access
- Incoming Attachments
- Reset to posture default

It SHALL NOT expose:

- per-audience runtime feature toggles
- per-audience shell mode
- MCP grants/access editing
- raw approval-policy editing

MCP access/grants/approval editing SHALL route to `netclaw mcp permissions`.

#### Scenario: Audience Profiles omits per-audience feature toggles

- **WHEN** the operator opens Audience Profiles
- **THEN** the UI does not offer per-audience runtime feature toggles
- **AND** runtime enablement remains owned by Enabled Features

#### Scenario: Reset to posture default resets full underlying profile

- **GIVEN** an audience has customized visible settings and hidden MCP or
  approval settings
- **WHEN** the operator activates `Reset to posture default`
- **THEN** the full underlying audience profile is reset to posture
  defaults
- **AND** hidden MCP and approval settings for that audience are reset as
  well

### Requirement: Exposure Mode preserves current config shape

The Exposure Mode editor SHALL keep the existing `Daemon` config shape. It
SHALL use `Daemon.ExposureMode` as the single active selector and SHALL NOT
introduce per-mode active flags.

Supported explicit modes are:

- `Local`
- `Reverse Proxy`
- `Tailscale Serve`
- `Tailscale Funnel`
- `Cloudflare Tunnel`

Each non-local mode SHALL use its own mode-specific dialog. `Local`
requires no extra setup. Inactive old values SHALL be preserved and ignored
when inactive.

#### Scenario: Switching modes preserves inactive values

- **GIVEN** the config contains previously saved Cloudflare Tunnel values
- **AND** `Daemon.ExposureMode` is currently `Reverse Proxy`
- **WHEN** the operator edits Reverse Proxy settings and saves
- **THEN** the inactive Cloudflare values remain preserved in config
- **AND** the active mode remains determined only by `Daemon.ExposureMode`

### Requirement: First non-local exposure enablement may bootstrap pairing

The flow SHALL auto-pair the current configuring client when the operator
first enables a non-local exposure mode from `netclaw config` and no
bootstrap/pairing state exists.

If bootstrap state is orphaned or mismatched, the flow SHALL block and
direct the operator to `netclaw doctor`, formal docs, and issue `#875`.

#### Scenario: Missing bootstrap state auto-pairs current client

- **GIVEN** the operator enables `Tailscale Serve`
- **AND** no bootstrap or pairing state exists yet
- **WHEN** the save flow runs
- **THEN** the current configuring client is auto-paired before the mode is
  finalized

#### Scenario: Orphaned bootstrap state blocks save

- **GIVEN** the operator enables a non-local exposure mode
- **AND** existing bootstrap state is orphaned or mismatched
- **WHEN** the save flow validates exposure setup
- **THEN** the save is blocked
- **AND** the operator is directed to `netclaw doctor`, formal docs, and
  issue `#875`

### Requirement: Leaf validation is generalized

Every config leaf editor SHALL validate what it edits before save.
Validation SHALL cover local structural validity and any relevant probes
such as paths, URIs, auth, binary presence, or remote reachability.

Structurally invalid config SHALL block save without override.
Runtime/probe failures MAY present `Save anyway`.

#### Scenario: Structural error blocks save with no override

- **GIVEN** a leaf editor contains an invalid URI or malformed config
  reference
- **WHEN** the operator saves
- **THEN** save is blocked
- **AND** no `Save anyway` affordance is shown

#### Scenario: Probe failure offers Save anyway

- **GIVEN** a leaf editor is structurally valid
- **AND** a remote reachability or runtime probe fails
- **WHEN** the operator saves
- **THEN** the editor may show `Save anyway`
- **AND** the operator can choose to persist the structurally valid config

### Requirement: Inline config editors autosave completed actions consistently

Every inline `netclaw config` leaf editor SHALL use a shared autosave
interaction contract. The UI SHALL NOT require an explicit save key for
ordinary config edits.

Completed actions SHALL save immediately after validation. Completed actions
include accepted text or multi-field forms, toggles, audience changes,
enable/disable actions, add/remove actions, and confirmed reset actions.
Incomplete text input SHALL remain an in-memory draft until accepted with
`Enter` or an equivalent Apply action.

`Esc` SHALL only navigate back or cancel incomplete input. It SHALL NOT save
pending edits and SHALL NOT be required to complete a save.

All autosaves SHALL be atomic: validation SHALL complete before files are
written, and failed validation SHALL leave persisted config and secrets
unchanged.

#### Scenario: Completed toggle autosaves immediately

- **GIVEN** an inline config leaf editor contains a boolean toggle
- **WHEN** the operator toggles the setting
- **THEN** the editor validates the resulting state
- **AND** persists the change immediately when validation succeeds
- **AND** shows a saved status without asking the operator to press a save key

#### Scenario: Esc cancels draft text without persisting

- **GIVEN** an inline config leaf editor contains a text field
- **AND** the operator has typed a draft value but has not accepted it
- **WHEN** the operator presses `Esc`
- **THEN** the editor navigates back or cancels the draft
- **AND** the persisted config is unchanged

#### Scenario: Invalid completed action writes nothing

- **GIVEN** an inline config leaf editor contains a structurally invalid draft
- **WHEN** the operator accepts the action
- **THEN** validation fails
- **AND** no config or secrets file is modified
- **AND** the UI shows the validation error

### Requirement: Inline config persistence is section-preserving

Inline config leaf editors SHALL persist only the sections, providers,
fields, and sidecar files they own. Saving one provider or sub-area SHALL NOT
delete or reset unrelated providers, inactive values, secrets, audiences, or
sidecar files.

Disable actions SHALL preserve dormant configuration and secrets while writing
only the runtime-enabled flag. Destructive removal SHALL require an explicit
reset/confirm action and SHALL be scoped to the confirmed target.

#### Scenario: Disabling one channel provider preserves its dormant setup

- **GIVEN** Slack has saved channels, audiences, allowed users, and secrets
- **WHEN** the operator disables Slack from the Channels config area
- **THEN** Slack `Enabled` is persisted as `false`
- **AND** Slack channels, audiences, allowed users, and secrets remain
  persisted

#### Scenario: Saving one channel provider does not wipe another provider

- **GIVEN** Slack and Discord both have saved channel configuration
- **WHEN** the operator adds a Discord channel and the action autosaves
- **THEN** the Discord addition is persisted
- **AND** the saved Slack configuration remains present and unchanged except
  for any explicit Slack action the operator completed

#### Scenario: Reset is the only provider-destructive action

- **GIVEN** a provider has saved channel configuration and secrets
- **WHEN** the operator confirms reset for that provider
- **THEN** only that provider's config and secrets are removed
- **AND** other providers remain unchanged

### Requirement: Root dashboard summarizes each area's live state

The root dashboard SHALL display, for each domain entry, a short live status
summary read fresh from the current configuration (for example the configured
search backend, the deployment posture with enabled-feature count, the count of
configured channels, or the count of outbound webhooks). Status summaries SHALL
NOT render secret values. The focused entry's description SHALL be shown as a
help line.

#### Scenario: Dashboard summarizes configured state without secrets

- **GIVEN** a configured install with a search backend and channels set
- **WHEN** the operator opens `netclaw config`
- **THEN** each area row shows its current state summary
- **AND** no secret value (API key, bearer token, channel token) appears in any
  summary

### Requirement: Channels resolve a target before adding it

When the operator adds a channel to a configured adapter, `netclaw config` SHALL
resolve the channel against that adapter (confirming it exists / is visible to
the bot) BEFORE persisting it. A channel that does not resolve SHALL NOT be
added. A resolved channel SHALL be added at the deployment posture's default
audience and SHALL remain editable afterward.

#### Scenario: Non-resolving channel is rejected

- **GIVEN** the operator types a channel the adapter cannot resolve
- **WHEN** they confirm the add
- **THEN** an error is shown
- **AND** the channel is not written to config

#### Scenario: Resolved channel is added at the default audience

- **GIVEN** the operator types a channel the adapter resolves
- **WHEN** they confirm the add
- **THEN** the resolved channel is added at the deployment posture's default
  audience

### Requirement: Telemetry exposes multiple outbound webhooks

The Telemetry & Alerting area SHALL edit the full list of outbound webhooks
(`Notifications.Webhooks`) — add, edit, and remove — rather than a single
webhook. Each entry SHALL carry a name, URL, and an optional authorization
header (masked on display), with the webhook format auto-detected from the URL
and shown read-only.

#### Scenario: Multiple webhooks round-trip

- **GIVEN** the operator adds two outbound webhooks
- **WHEN** the editor saves and is reopened
- **THEN** both webhooks are present with their names, URLs, and detected
  formats

### Requirement: Config selection uses a uniform highlight bar

The config dashboard and its sub-editor lists SHALL indicate the focused row
with one uniform full-width highlight bar style, applied consistently across
areas rather than a mix of marker glyphs.

#### Scenario: Focused row is highlighted consistently

- **WHEN** the operator navigates any config list
- **THEN** the focused row is shown with the uniform highlight bar

### Requirement: Coverage follows leaf ownership

Leaf editors SHALL receive substantive round-trip and smoke coverage.
Routed handoffs SHALL receive shallow routing coverage only. Preservation
assertions SHALL be semantic, not byte-identical.

#### Scenario: Routed handoff does not require leaf round-trip suite

- **GIVEN** `Inference Providers` routes to `netclaw provider`
- **WHEN** coverage is defined for the config dashboard
- **THEN** the handoff requires routing coverage
- **AND** it does not require a duplicate leaf-editor round-trip suite in
  this change

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


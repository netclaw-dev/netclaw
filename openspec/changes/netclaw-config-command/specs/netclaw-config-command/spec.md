## ADDED Requirements

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

#### Scenario: Root dashboard shows domain entries

- **GIVEN** a configured install
- **WHEN** the operator runs `netclaw config`
- **THEN** the root dashboard opens with the documented domain entries
- **AND** it does not render a flat dump of every registered leaf editor

### Requirement: Missing install refuses before TUI startup

`netclaw config` SHALL detect a missing install/config before starting the
TUI. It SHALL print `No configuration found. Run \`netclaw init\` first.`
to stderr and exit non-zero.

#### Scenario: No install refusal renders no TUI

- **GIVEN** `~/.netclaw/config/netclaw.json` does not exist
- **WHEN** the operator runs `netclaw config`
- **THEN** the command prints the refusal message to stderr
- **AND** exits non-zero
- **AND** no partial TUI is rendered

### Requirement: Routed handoffs SHALL be first-class config outcomes

The config dashboard SHALL treat routed handoffs as first-class config
outcomes and MAY route specific domain entries into existing commands
instead of re-hosting the full editor inline. In this branch, `Inference
Providers` SHALL route to `netclaw provider` and `Models` SHALL route to
`netclaw model`.

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

### Requirement: First non-local exposure enablement SHALL bootstrap pairing when needed

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

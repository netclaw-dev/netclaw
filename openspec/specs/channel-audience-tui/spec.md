# channel-audience-tui Specification

## Purpose

Define the interactive TUI step for per-channel audience assignment with
dynamic channel add/remove and keyboard-driven audience cycling.
## Requirements
### Requirement: Channel list with audience cycling

The wizard SHALL present a channel list where each row shows the channel
name, Slack ID, and current audience. ←/→ keys SHALL cycle the audience
value on the focused row.

#### Scenario: Cycle audience with arrow keys

- **GIVEN** the focused channel row shows audience "Team"
- **WHEN** the user presses →
- **THEN** the audience changes to "Personal"
- **AND** pressing → again changes to "Public"
- **AND** pressing → again wraps to "Team"

#### Scenario: Left arrow cycles in reverse

- **GIVEN** the focused channel row shows audience "Team"
- **WHEN** the user presses ←
- **THEN** the audience changes to "Public"

#### Scenario: Enter advances to next step

- **WHEN** the user presses Enter on the channel list
- **THEN** the wizard advances to the next step
- **AND** the current audience assignments are preserved

### Requirement: Channel removal

The wizard SHALL allow removing channels by pressing `d` on the focused row.

#### Scenario: Remove channel

- **GIVEN** the focused row is #dev-ops
- **WHEN** the user presses `d`
- **THEN** #dev-ops is removed from the list
- **AND** focus moves to the nearest remaining row

#### Scenario: Cannot remove DMs row

- **GIVEN** DMs are enabled and the focused row is "DMs"
- **WHEN** the user presses `d`
- **THEN** nothing happens (DMs cannot be removed, only toggled in
  ChatServices)

### Requirement: DMs row present when enabled

The channel list SHALL include a "DMs" row when direct messages are enabled
in ChatServices. The DMs row audience is editable via ←/→.

#### Scenario: DMs row with posture default

- **GIVEN** DMs are enabled and posture is Personal
- **WHEN** the Channels step is shown
- **THEN** a "DMs" row appears with audience "Personal"

### Requirement: Skip when Slack disabled

The Channels step SHALL be skipped entirely when Slack is disabled in
ChatServices. No `ChannelAudiences` are written to config.

#### Scenario: Slack disabled skips Channels

- **GIVEN** Slack is disabled in the ChatServices step
- **WHEN** the wizard advances past SecurityPosture and ACL
- **THEN** the Channels step is skipped
- **AND** no `ChannelAudiences` section is written to config

### Requirement: Block on API failure with actionable error

A channel save SHALL block only on a genuine probe failure, never on a merely-unresolved channel name.

If a channel probe reports a **genuine failure** (invalid or expired token,
missing scope, network error, or any other condition that sets a non-empty
`ErrorMessage` on the resolution result), the save SHALL be blocked with an
actionable error message and no data SHALL be persisted. The user must fix the
credential or scope and retry before the save is accepted.

If the probe call **succeeds** (no `ErrorMessage`) but one or more channel
names or IDs could not be resolved (the probe's `Unresolved` list is
non-empty), the save SHALL proceed: the entire adapter persists (token + all
channel entries, with resolved names rewritten to their canonical IDs and
unresolved entries kept verbatim). The unresolved entries are flagged
non-blockingly with a warning status message identifying each unresolved entry.

Security invariant: an unresolved name or ID that persists verbatim in the
`AllowedChannelIds` list is inert — the runtime ACL matches against canonical
channel IDs, so an unresolved name grants access to no real channel. It is a
harmless placeholder until the bot can see the channel, at which point the
background label refresh will canonicalize it automatically.

The distinction between a blocking failure and a non-blocking unresolved entry
is determined solely by the presence of a non-empty `ErrorMessage` on the
resolution result, NOT by the result's `Success` flag. `Success` is false
whenever any entry failed to resolve (including the non-blocking case), so
checking `Success` alone would incorrectly block saves where only some names
are unresolved.

#### Scenario: Probe fails with invalid auth — blocks save, persists nothing

- **GIVEN** Slack is enabled with a valid-format bot token and at least one channel name configured
- **WHEN** the save is attempted and the Slack probe returns `ErrorMessage = "invalid_auth"` (with `Success = false`)
- **THEN** the save returns false and `IsSaved` remains false
- **AND** the status message is `"Slack channel lookup failed: invalid_auth"` at `Error` tone
- **AND** the config file and secrets file are unchanged from before the save

#### Scenario: Probe fails with missing scope — blocks save, persists nothing

- **GIVEN** the Slack token lacks `channels:read` scope
- **WHEN** the Channels step save is attempted
- **THEN** an error status is shown with the scope failure reason
- **AND** the user cannot advance until the credential is corrected or they navigate back

#### Scenario: Probe succeeds but one name does not resolve — saves with warning

- **GIVEN** Slack has channels `"openclaw, fake-channel"` configured and the bot token is valid
- **WHEN** the probe resolves `"openclaw"` to `"C99"` and returns `"fake-channel"` in `Unresolved` (with `Success = false`, `ErrorMessage = null`)
- **THEN** the save returns true and `IsSaved` is true
- **AND** the status tone is `Warning` and the message identifies `#fake-channel` as unresolved
- **AND** the persisted `AllowedChannelIds` contains `["C99", "fake-channel"]` (resolved name replaced with its ID; unresolved name kept verbatim)
- **AND** the unresolved channel row is marked `IsUnresolved = true` in the channel permission list

#### Scenario: Probe succeeds and all names resolve — saves cleanly

- **GIVEN** all configured channel names resolve successfully
- **WHEN** the save is attempted
- **THEN** the save returns true at `Success` tone with no unresolved warning
- **AND** all channel names are rewritten to their canonical IDs before persistence

#### Scenario: Network error reaching Slack API — blocks save

- **GIVEN** the Slack API is unreachable and the probe surfaces a non-empty `ErrorMessage`
- **WHEN** the save is attempted
- **THEN** the save is blocked with the failure reason in the error status
- **AND** nothing is persisted

---

### Requirement: Audience defaults from posture

Channel audiences SHALL be pre-populated based on the deployment posture
selected in the SecurityPosture step. Users can override per-channel.

#### Scenario: Posture defaults applied to new channels

- **GIVEN** posture is Team
- **WHEN** the user adds a new channel
- **THEN** the new channel's audience defaults to Team

### Requirement: Single-entry resolve-before-add channel flow

Adding a channel SHALL open a single free-text input (`ChannelsConfigScreen.AddChannel`)
where the operator types a channel name or ID. The typed entry is resolved
against the live adapter before it is added to the channel list. A non-resolving
entry SHALL be rejected at add time with an error status; the operator stays on
the add screen. A successfully resolved entry SHALL be added to the channel
list at the deployment-posture default audience, its row SHALL be focused in
the channel permission list, and the change SHALL be autosaved immediately.

For Slack: if the typed value matches the canonical channel ID format
(`C…` or `G…` followed by uppercase alphanumerics), it is accepted directly
without a name-lookup probe call. If the typed value is a channel name, the
probe is called; a successful resolution returns the canonical ID. A
non-resolving name is rejected.

Duplicate entries (where the resolved ID is already in the channel list) SHALL
be rejected with a status message indicating the channel is already configured.

#### Scenario: Add channel by ID (Slack — skips probe)

- **GIVEN** the operator is on the AddChannel screen for Slack
- **WHEN** the operator types `"C09"` (a valid Slack channel ID format) and confirms
- **THEN** no probe call is made
- **AND** `"C09"` is added to the channel list at the default audience
- **AND** the screen advances to `ChannelPermissions` with the new row focused
- **AND** the change is autosaved

#### Scenario: Add channel by name (Slack — probe resolves to ID)

- **GIVEN** the operator is on the AddChannel screen for Slack
- **WHEN** the operator types `"netclaw-support"` and confirms
- **THEN** the probe is called once with `["netclaw-support"]` and the bot token
- **AND** the probe returns resolved ID `"C09"` for that name
- **AND** `"C09"` is added at the default audience and the new row is focused
- **AND** the change is autosaved and `IsSaved` is true

#### Scenario: Add channel by name — probe finds no match

- **GIVEN** the operator types `"ghost"` on the AddChannel screen
- **WHEN** the probe returns `"ghost"` in `Unresolved` and `ErrorMessage` is null
- **THEN** the save does NOT occur and the screen stays on `AddChannel`
- **AND** the status shows `"Slack channel not found: #ghost"` at `Error` tone
- **AND** the channel list and persisted config are unchanged

#### Scenario: Add channel already in list — rejected

- **GIVEN** `"C01"` is already in the Slack channel list
- **WHEN** the operator types `"C01"` on the AddChannel screen and confirms
- **THEN** the channel is not duplicated
- **AND** the status message indicates `"C01 is already configured"` at `Error` tone

#### Scenario: Escape from AddChannel screen discards draft

- **GIVEN** the operator has typed a partial entry in the AddChannel input
- **WHEN** the operator presses Esc
- **THEN** the screen returns to `ChannelPermissions`
- **AND** no config or secrets files are modified

### Requirement: Lazy Slack channel name-to-ID normalization on label refresh

Stored Slack channel names SHALL be canonicalized to channel IDs lazily during the background label refresh.

When the channel permission list is opened and a background label refresh is
triggered for Slack, the refresh SHALL detect any stored entries that are
channel names (not canonical IDs) that now resolve to a canonical ID and SHALL
rewrite them to their ID in-place. The rewritten entries and their audience
assignments SHALL be persisted immediately (without requiring a manual save) and
`IsSaved` SHALL be set to true. If all stored entries are already canonical IDs,
no write occurs.

Security rationale: the runtime Slack ACL (`SlackAclPolicy`) matches
`AllowedChannelIds` against the Slack channel ID, not the channel name. A name
stored verbatim in the allow-list is inert and grants access to no channel. Once
the bot can see the channel, the normalization step makes the ACL effective
without operator intervention.

Audience assignments travel with the ID rewrite: the audience keyed under the
old name is moved to the new canonical ID key, and the stale name key is
removed.

#### Scenario: Background refresh normalizes stored name to ID and persists

- **GIVEN** the config contains `AllowedChannelIds: ["C01", "netclaw-test"]` where `"netclaw-test"` is a name, not an ID
- **AND** the channel audience for `"netclaw-test"` is `"public"`
- **WHEN** the operator opens channel permissions and the background refresh runs
- **AND** the probe resolves `"netclaw-test"` to `"C99"`
- **THEN** the persisted `AllowedChannelIds` becomes `["C01", "C99"]`
- **AND** the audience for `"C99"` is `"public"` and the `"netclaw-test"` audience key is removed
- **AND** the channel row renders as `"#netclaw-test"` (display name from probe result)
- **AND** `IsSaved` is true without a manual save

#### Scenario: Background refresh does not rewrite already-canonical IDs

- **GIVEN** all entries in `AllowedChannelIds` are already canonical Slack channel IDs
- **WHEN** the background refresh completes successfully
- **THEN** the config file is not modified

### Requirement: Credential blank-preserve on re-edit

A blank credential field on re-edit SHALL preserve the existing stored secret rather than clearing it.

When an operator re-edits a channel adapter's credentials (via the rotate
credentials screen) and leaves a secret field blank, the existing stored secret
for that field SHALL be preserved — the blank input SHALL NOT overwrite or clear
the persisted secret. Only a non-blank typed value replaces the existing secret.

This applies to all adapter secret fields: Slack bot token, Slack app token,
Discord bot token, and Mattermost bot token. Non-secret fields (Mattermost
server URL, callback URL) are updated unconditionally from the typed value.

The credential field display SHALL show a hint (`"configured - leave blank to
keep"`) for any field that has a persisted secret, so the operator knows the
current state without the secret value being shown.

#### Scenario: Rotate credentials — blank field preserves existing secret

- **GIVEN** Slack is configured with a persisted bot token `"xoxb-test"` and app token `"xapp-test"`
- **WHEN** the operator opens rotate credentials, types `"xoxb-new"` for the bot token, and leaves the app token field blank
- **AND** the operator confirms and saves
- **THEN** the persisted bot token is `"xoxb-new"`
- **AND** the persisted app token remains `"xapp-test"` (blank input did not clear it)

#### Scenario: Rotate credentials — both fields blank keeps both existing secrets

- **GIVEN** Slack has persisted bot and app tokens
- **WHEN** the operator opens rotate credentials and confirms without typing anything
- **AND** the operator saves
- **THEN** both existing tokens are preserved unchanged

#### Scenario: Credential field hint shown for persisted secret

- **GIVEN** a Slack bot token is already persisted for the adapter
- **WHEN** the operator opens the rotate credentials screen
- **THEN** the bot token field displays the hint `"configured - leave blank to keep"`
- **AND** the app token field displays the same hint if an app token is also persisted


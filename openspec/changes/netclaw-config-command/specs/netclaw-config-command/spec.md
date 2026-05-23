## ADDED Requirements

### Requirement: Config command launches dashboard

`netclaw config` SHALL launch Termina with a dashboard page rendering every
registered `ISectionEditor` from `SectionEditorRegistry`, plus a "Run full
doctor" item and a "Quit" item at the dashboard tail. The command SHALL
operate offline (no daemon connection required) and SHALL read/write
local config files only.

#### Scenario: Dashboard renders all registered editors

- **GIVEN** the CLI is configured with the day-one editor registry
  (Search, Slack, Discord, Mattermost, ExposureMode, SecurityPosture,
  AudienceProfiles, OutboundWebhooks, InboundWebhooks, ExternalSkills,
  SkillFeeds, BrowserAutomation)
- **WHEN** the operator runs `netclaw config`
- **THEN** Termina opens with a dashboard listing every editor, with
  status badges computed per editor
- **AND** the tail shows a "Run full doctor" item and a "Quit" item

#### Scenario: Config command does not require daemon

- **GIVEN** the Netclaw daemon is not running
- **WHEN** the operator runs `netclaw config`
- **THEN** the command starts and renders the dashboard normally
- **AND** no daemon RPC or HTTP call is made

### Requirement: Refuse when no config exists

`netclaw config` SHALL detect a missing `netclaw.json` at startup and
refuse to render the dashboard. The command SHALL print
`No configuration found. Run \`netclaw init\` first.` to stderr and exit
with a non-zero exit code.

#### Scenario: No config refusal exits non-zero

- **GIVEN** `~/.netclaw/config/netclaw.json` does not exist
- **WHEN** the operator runs `netclaw config`
- **THEN** the command prints `No configuration found. Run \`netclaw init\` first.`
  to stderr
- **AND** exits with a non-zero exit code
- **AND** does not render any Termina UI

### Requirement: Dashboard status badges

The dashboard SHALL render a status badge for every section editor by
computing `GetStatus(currentConfig)` and running the editor's
`RelevantDoctorChecks` against the on-disk config at dashboard entry.
The badge vocabulary SHALL be: `✓` configured (all checks pass),
`⚠` configured but at least one check warns, `✗` configured but at
least one check errors, and `–` not set / default. Badges SHALL be
recomputed on return from a section editor save.

#### Scenario: Configured-and-passing section shows checkmark

- **GIVEN** the Search section is configured with backend `duckduckgo`
- **AND** `ConfigSchemaDoctorCheck` and `SearchBackendDoctorCheck`
  both pass
- **WHEN** the dashboard renders
- **THEN** the Search row shows `✓`

#### Scenario: Configured-and-warning section shows warning glyph

- **GIVEN** the Search section is configured with backend `brave` and a
  rate-limited API key
- **AND** `SearchBackendDoctorCheck` returns WARN
- **WHEN** the dashboard renders
- **THEN** the Search row shows `⚠`

#### Scenario: Unset section shows dash

- **GIVEN** the Outbound Webhooks section has no configured webhooks
- **WHEN** the dashboard renders
- **THEN** the Outbound Webhooks row shows `–`

### Requirement: Sub-grouping by category

Section editors that declare the same `Category` value SHALL be grouped
visually in the dashboard under that category label. The label itself
SHALL be unselectable; only the editor rows underneath it accept focus.
Grouping SHALL NOT affect the registry's flat enumeration or the audit's
per-editor checks.

#### Scenario: Chat-channels group renders three siblings

- **GIVEN** the Slack, Discord, and Mattermost editors declare
  `Category = "Chat Channels"`
- **WHEN** the dashboard renders
- **THEN** the three rows render under a "Chat Channels" group label
- **AND** the group label cannot be selected or activated
- **AND** the dashboard registry audit still treats the three as
  independent registered editors

### Requirement: Section editor hosting

Opening a section from the dashboard SHALL launch the editor's
`IWizardStepViewModel` (produced by `CreateEditor(context)`) inside a
single-step `WizardOrchestrator`. The orchestrator SHALL drive save and
cancel semantics exactly as in the linear wizard, then return control
to the dashboard. The dashboard SHALL refresh the affected section's
status before re-rendering.

#### Scenario: Open editor, save, return

- **GIVEN** the dashboard is displayed with the Search row focused
- **WHEN** the operator presses Enter
- **THEN** the Search section editor opens in single-step mode
- **AND** the editor's UI matches the section editor contract (pre-filled
  non-secret fields, masked empty secret fields)
- **AND** on Save the orchestrator writes via the merge layer and returns
  to the dashboard
- **AND** the dashboard re-renders with the updated Search status badge

#### Scenario: Open editor, cancel, return without write

- **GIVEN** the dashboard is displayed with the Search row focused
- **WHEN** the operator opens the editor, changes the backend selector,
  and presses Esc
- **THEN** the editor shows the unsaved-changes discard confirm dialog
- **AND** on confirm-discard, control returns to the dashboard
- **AND** no `netclaw.json` write occurred
- **AND** the dashboard re-renders with the unchanged Search status badge

### Requirement: Doctor blessing on section save

When a section editor saves, the host SHALL build a candidate merged
config in memory, resolve the editor's `RelevantDoctorChecks`, and run
each check against the candidate. If any check returns ERROR, the
host SHALL block the save, surface an inline error banner, and keep
focus inside the editor. If any check returns WARN (and no ERROR), the
host SHALL render an inline warning banner with a `Save anyway`
affordance and a `Cancel` affordance. If all checks pass, the host
SHALL write the merged candidate to disk and return to the dashboard.

#### Scenario: Error-level check blocks save

- **GIVEN** the Search editor is open with backend `brave` selected and
  the API key field left blank (no stored key)
- **WHEN** the operator saves
- **THEN** `SearchBackendDoctorCheck` returns ERROR
- **AND** the inline error banner displays the check's message
- **AND** the Save button is disabled until the error condition is
  cleared

#### Scenario: Warn-level check surfaces banner with override

- **GIVEN** the Skill Feeds editor is open with a feed whose URL is
  currently unreachable
- **WHEN** the operator saves
- **THEN** `SkillFeedsDoctorCheck` returns WARN
- **AND** the inline warning banner displays the check's message
- **AND** the host renders `[ Save anyway ]` and `[ Cancel ]`
- **AND** activating Save anyway writes the merged candidate to disk

#### Scenario: Clean checks write to disk

- **GIVEN** the Search editor is open with backend `duckduckgo` and no
  required API key
- **WHEN** the operator saves
- **THEN** all relevant checks pass
- **AND** the merge writer produces a new `netclaw.json` with only the
  Search section changed
- **AND** control returns to the dashboard

### Requirement: Run full doctor item

The dashboard SHALL include a "Run full doctor" item at the tail that
invokes `DoctorRunner` against the on-disk config and renders results
on a doctor results page. The results page SHALL list each check's
status (PASS/WARN/ERROR/SKIPPED) with summary text. Pressing Esc or
activating the page's "Back to dashboard" action SHALL return to the
dashboard with no config write performed.

#### Scenario: Full doctor lists every check

- **GIVEN** the dashboard is displayed and the daemon-restart status
  is irrelevant
- **WHEN** the operator selects "Run full doctor"
- **THEN** `DoctorRunner` runs every registered check against on-disk
  config
- **AND** the results page renders one row per check with PASS/WARN/ERROR
  status and check name

#### Scenario: Full doctor does not modify config

- **GIVEN** the dashboard's "Run full doctor" item runs
- **WHEN** results render and the operator returns to the dashboard
- **THEN** no config file write has occurred
- **AND** the dashboard's per-section status badges reflect the same
  on-disk state as before

### Requirement: Daemon-restart nudge at exit

`netclaw config` SHALL print a stderr nudge at exit instructing the
operator to restart the daemon for changes to take effect, when (a) at
least one config or secrets write occurred during the session AND (b)
the daemon is currently running. If either condition is false, the
nudge SHALL be omitted.

#### Scenario: Daemon running plus config change emits nudge

- **GIVEN** the daemon is running
- **AND** the operator saved at least one section during the session
- **WHEN** the operator quits the dashboard
- **THEN** the stderr nudge `Config saved. Restart the daemon to apply
  changes: netclaw daemon stop && netclaw daemon start` is printed
- **AND** the command exits with status 0

#### Scenario: Daemon not running suppresses nudge

- **GIVEN** the daemon is not running
- **AND** the operator saved at least one section during the session
- **WHEN** the operator quits the dashboard
- **THEN** no nudge is printed
- **AND** the command exits with status 0

#### Scenario: No writes suppresses nudge regardless of daemon state

- **GIVEN** the operator opened the dashboard, browsed editors, but
  saved nothing
- **WHEN** the operator quits
- **THEN** no nudge is printed regardless of daemon state

### Requirement: Generic list editor component

The CLI SHALL provide a generic `ListEditor<T>` Termina component
parameterized by an `IItemEditor<T>` describing the item shape. The
component SHALL render an Add row at the bottom (`+ Add <noun>`), an
inline-or-sub-page edit affordance per item depending on
`IItemEditor.RequiresSubPage`, an inline delete affordance keyed to
`d` with single-key confirmation for low-stakes deletes, and overall
Save / Cancel affordances. The list editor SHALL preserve item
identity across edit by consulting `IItemEditor.KeyOf(item)` so that
in-place renames (rather than delete + add) round-trip correctly.

#### Scenario: Inline edit for simple items

- **GIVEN** an `ExternalSkills.Sources` list with three path entries
- **WHEN** the operator presses Enter on a focused row
- **THEN** an inline single-line input overlay replaces the row
- **AND** Enter saves the edit to in-memory list state
- **AND** Esc cancels without modifying state

#### Scenario: Sub-page edit for complex items

- **GIVEN** an `Notifications.Webhooks` list with two configured
  webhooks
- **WHEN** the operator presses Enter on a focused row
- **THEN** a sub-page form opens showing every webhook field
- **AND** Save on the sub-page returns to the list with the in-memory
  webhook updated
- **AND** Cancel on the sub-page returns to the list with no change

#### Scenario: Delete confirmation prevents accidental removal

- **GIVEN** a focused list item
- **WHEN** the operator presses `d`
- **THEN** an inline `Remove? [y/N]` prompt replaces the row's display
- **AND** pressing `y` removes the item from in-memory state
- **AND** any other key cancels the deletion

#### Scenario: Item identity preserved on in-place rename

- **GIVEN** a webhook list with an entry whose `KeyOf` returns
  `"critical-pager"`
- **WHEN** the operator edits the entry and changes its name to
  `pagerduty-prod`
- **THEN** the list save records a single update (not a delete + add)
- **AND** the underlying `Notifications.Webhooks` array contains exactly
  one entry with the new name and the preserved auth header
  (per the secret-handling contract)

### Requirement: Search Provider editor

The dashboard SHALL include a `SearchSectionEditor`
(`SectionId = "Search"`) for editing the search backend and its
credentials. The editor SHALL present a single-selection list among
`Brave`, `DuckDuckGo`, `SearXng (self-hosted)`. Backend-dependent
fields SHALL render: Brave shows an API key input (secret-handling
contract); SearXng shows an instance URL input; DuckDuckGo shows no
additional fields. The editor SHALL declare `RelevantDoctorChecks` =
`{ConfigSchemaDoctorCheck, SearchBackendDoctorCheck}`.

#### Scenario: Switching to DuckDuckGo preserves stored Brave key

- **GIVEN** the Search section is configured with backend `brave` and a
  stored Brave API key
- **WHEN** the operator switches the backend to `duckduckgo` and saves
- **THEN** `netclaw.json` records `Search.Backend = "duckduckgo"`
- **AND** `secrets.json` retains the Brave API key encrypted at its
  original location

#### Scenario: Brave without key blocks save

- **GIVEN** the Search section is unconfigured
- **WHEN** the operator selects `brave`, leaves the key empty, and saves
- **THEN** `SearchBackendDoctorCheck` returns ERROR
- **AND** the save is blocked

### Requirement: Chat channel editors

The dashboard SHALL include three independently-registered chat-channel
section editors: `SlackSectionEditor` (`SectionId = "Slack"`),
`DiscordSectionEditor` (`SectionId = "Discord"`), and
`MattermostSectionEditor` (`SectionId = "Mattermost"`). Each editor
SHALL declare `Category = "Chat Channels"` for menu grouping. Each
editor SHALL surface its platform's authentication tokens
(per-platform secret-handling contract), an allowed-channels list,
an allowed-users list, the DMs-enabled toggle, the channel audience
profile selector, and a Test Connection affordance that runs the
existing per-platform probe and renders results in an inline banner.

#### Scenario: Slack editor exposes both bot and app tokens with leave-blank-to-keep

- **GIVEN** the Slack section has both bot and app tokens stored
- **WHEN** the operator opens the Slack section editor
- **THEN** both token fields render empty with "configured — leave blank
  to keep" hint
- **AND** saving with both fields blank preserves both stored tokens

#### Scenario: Discord editor exposes single token

- **GIVEN** the Discord section is unconfigured
- **WHEN** the operator opens the Discord section editor
- **THEN** one token field is displayed with "(not set)" hint
- **AND** no app-token field exists (Discord uses a single bot token)

#### Scenario: Mattermost editor exposes server URL plus token

- **GIVEN** the Mattermost section is unconfigured
- **WHEN** the operator opens the Mattermost section editor
- **THEN** a Server URL text field is displayed in addition to the token
  field

#### Scenario: Test Connection renders inline banner

- **GIVEN** the Slack editor is open with valid tokens entered
- **WHEN** the operator activates Test Connection
- **THEN** the existing Slack probe runs in-process
- **AND** results render in an inline banner with workspace name and
  channel-access summary

### Requirement: Exposure Mode editor

The dashboard SHALL include an `ExposureModeSectionEditor`
(`SectionId = "Daemon.ExposureMode"`) that lets the operator select
among `Local`, `Reverse Proxy`, `Tailscale`, `Cloudflare Tunnel`. The
editor SHALL surface mode-conditional sub-forms: Reverse Proxy
requires an external base URL plus a trusted-proxy CIDR list; Tailscale
requires an auth-key secret plus hostname; Cloudflare Tunnel requires a
tunnel-token secret plus optional access-policy email domain. The
editor SHALL also surface daemon host and port. `RelevantDoctorChecks`
SHALL include `ConfigSchemaDoctorCheck` and the existing
`ExposureModeDoctorCheck`.

#### Scenario: Local mode requires no sub-form

- **GIVEN** the Exposure Mode editor is open with `Local` selected
- **WHEN** the operator saves
- **THEN** `Daemon.ExposureMode = "Local"` is written
- **AND** no trusted-proxy or tunnel configuration is required

#### Scenario: Reverse Proxy without trusted proxies blocks save

- **GIVEN** the Exposure Mode editor is open with `Reverse Proxy`
  selected
- **AND** the trusted-proxy list is empty
- **WHEN** the operator saves
- **THEN** `ExposureModeDoctorCheck` returns ERROR
- **AND** the save is blocked

### Requirement: Security Posture editor

The dashboard SHALL include a `SecurityPostureSectionEditor`
(`SectionId = "Security.Posture"`) presenting `Personal`, `Team`,
`Enterprise` posture choices with descriptive subtitles. When the
operator changes posture and the existing `Tools.AudienceProfiles`
section has been customized away from the prior posture's defaults,
the editor SHALL surface a three-option cascade dialog: cancel,
apply posture with overwrite, or apply posture preserving custom
profiles.

#### Scenario: Cascade dialog presents three options

- **GIVEN** the current posture is `Personal` and the Team audience
  profile has been customized in `Tools.AudienceProfiles`
- **WHEN** the operator selects `Team` and saves
- **THEN** the cascade dialog opens with default focus on `Cancel`
- **AND** options are: `Cancel — keep current posture`,
  `Apply new posture, overwrite profiles`,
  `Apply new posture, keep custom profiles`

#### Scenario: Default focus prevents accidental overwrite

- **GIVEN** the cascade dialog is open
- **WHEN** the operator presses Enter or Esc
- **THEN** the dialog cancels the posture change
- **AND** `Tools.AudienceProfiles` is unchanged

### Requirement: Audience Profiles editor

The dashboard SHALL include an `AudienceProfilesSectionEditor`
(`SectionId = "Tools.AudienceProfiles"`) replacing the init wizard's
feature-selection step. The editor SHALL render an audience picker for
`Personal`, `Team`, `Public`. Opening an audience SHALL display a
per-audience editor with one toggleable row per feature
(`memory`, `search`, `skills`, `scheduling`, `sub-agents`,
`webhooks`), a shell-mode selector for that audience, an approval
policy selector, and a `Reset to posture default` affordance. Arrow
keys SHALL navigate rows; `Space` SHALL toggle the focused checkbox;
`Enter` on a checkbox row SHALL also toggle (alternative to Space).
`RelevantDoctorChecks` SHALL include `ConfigSchemaDoctorCheck` and
`ToolAudienceProfilesDoctorCheck`.

#### Scenario: Down-arrow then Space toggles second row

- **GIVEN** the Team audience editor is open
- **AND** initial focus is on the first feature row (`memory`,
  currently enabled)
- **WHEN** the operator presses `↓` then `Space`
- **THEN** focus moves to the second row (`search`)
- **AND** the `search` toggle flips (off if it was on, on if it was
  off)
- **AND** the change is reflected in `Tools.AudienceProfiles.Team`
  when the editor saves

#### Scenario: Reset to posture default replaces all toggles

- **GIVEN** the Team audience editor is open with several custom
  toggle states
- **WHEN** the operator activates `Reset to posture default`
- **THEN** every toggle and the shell-mode selector revert to the
  current posture's default mapping for the Team audience

### Requirement: Outbound Webhooks editor

The dashboard SHALL include an `OutboundWebhooksSectionEditor`
(`SectionId = "Notifications.Webhooks"`) presenting the existing
multi-value array via the generic `ListEditor<T>` with the
`WebhookItemEditor` sub-page form. Each webhook SHALL be editable
with name, URL, optional auth-header value (secret-handling contract),
and optional event filter. Add/edit/remove SHALL produce a correctly
merged `Notifications.Webhooks` array.

#### Scenario: Add second webhook preserves first

- **GIVEN** `Notifications.Webhooks` contains one entry `ops-alerts`
- **WHEN** the operator opens the editor, adds a new webhook
  `critical-pager`, and saves
- **THEN** `Notifications.Webhooks` is a two-entry array
- **AND** the first entry is byte-identical to its pre-save state

### Requirement: Inbound Webhooks editor

The dashboard SHALL include an `InboundWebhooksSectionEditor`
(`SectionId = "Webhooks"`) presenting the feature-flag toggle plus
the request-timeout integer field. Route file editing SHALL remain
file-based and out of this editor's scope. `RelevantDoctorChecks`
SHALL include `ConfigSchemaDoctorCheck` and the existing
`InboundWebhookRoutesDoctorCheck`.

#### Scenario: Enabling inbound webhooks with no routes surfaces warning

- **GIVEN** `~/.netclaw/config/webhooks/` contains zero route files
- **WHEN** the operator enables inbound webhooks and saves
- **THEN** `InboundWebhookRoutesDoctorCheck` returns WARN
- **AND** the inline warning banner explains routes must be added via
  files
- **AND** Save anyway writes `Webhooks.Enabled = true`

### Requirement: External Skill Directories editor

The dashboard SHALL include an `ExternalSkillsSectionEditor`
(`SectionId = "ExternalSkills"`) presenting the existing path array
via the generic `ListEditor<T>` with the `PathItemEditor` inline-edit
shape. The editor SHALL validate each path on save: existence,
directory-ness, readability. Errors SHALL render inline below the
relevant row. `RelevantDoctorChecks` SHALL include
`ConfigSchemaDoctorCheck` and the new
`ExternalSkillSourcesDoctorCheck`.

#### Scenario: Non-existent path blocks save

- **GIVEN** the External Skills editor is open with a newly-added path
  pointing at a non-existent directory
- **WHEN** the operator saves
- **THEN** `ExternalSkillSourcesDoctorCheck` returns ERROR
- **AND** the row renders the error inline
- **AND** the save is blocked

### Requirement: Skill Feeds editor

The dashboard SHALL include a `SkillFeedsSectionEditor`
(`SectionId = "SkillFeeds"`) presenting the existing feed array via
the generic `ListEditor<T>` with the `SkillFeedItemEditor` sub-page
form. Each feed SHALL expose name, URL, optional Bearer API key
(secret-handling contract), and a Test Connection affordance.
`RelevantDoctorChecks` SHALL include `ConfigSchemaDoctorCheck` and
the new `SkillFeedsDoctorCheck` (WARN-only on reachability so transient
remote outages do not lock operators out of editing).

#### Scenario: Unreachable feed surfaces warning but allows save

- **GIVEN** the Skill Feeds editor is open with a feed pointing at an
  unreachable URL
- **WHEN** the operator saves
- **THEN** `SkillFeedsDoctorCheck` returns WARN
- **AND** the inline warning banner displays "feed unreachable"
- **AND** activating Save anyway writes the merged config

### Requirement: Browser Automation editor

The dashboard SHALL include a `BrowserAutomationSectionEditor`
(`SectionId = "BrowserAutomation"`) presenting the feature-flag toggle
and a status indicator showing whether Playwright is installed and at
which version. If Playwright is not installed, the toggle SHALL be
disabled and an "Install instructions" sub-page SHALL be reachable
from the editor footer. The installation itself SHALL NOT be invoked
from inside the TUI; the sub-page SHALL print platform-appropriate
shell commands and instruct the operator to re-open the editor after
installing. `RelevantDoctorChecks` SHALL include
`ConfigSchemaDoctorCheck` and the new
`BrowserAutomationDoctorCheck`.

#### Scenario: Toggle disabled when Playwright absent

- **GIVEN** the Browser Automation editor is open
- **AND** Playwright is not installed on the host
- **WHEN** the editor renders
- **THEN** the `Browser automation enabled` toggle is disabled
- **AND** the editor footer shows `[ Install instructions → ]`

#### Scenario: Enabling without Playwright blocks save

- **GIVEN** the Browser Automation editor is open
- **AND** Playwright is not installed
- **AND** the editor is somehow holding `Enabled = true` (e.g. from a
  hand-edited file)
- **WHEN** the operator saves
- **THEN** `BrowserAutomationDoctorCheck` returns ERROR
- **AND** the save is blocked with remediation guidance

### Requirement: Smoke tape per editor and the no-init refusal

The smoke-test harness SHALL include a tape per registered section
editor at `tests/smoke/tapes/config-<section-lowercase>.tape` plus a
matching assertion script at
`tests/smoke/assertions/config-<section-lowercase>.sh`. The harness
SHALL also include `config-no-init.tape` and its assertion exercising
the refuse-when-no-config path. Each section-editor tape SHALL
pre-stage existing `netclaw.json` and `secrets.json` fixtures,
exercise at least one save round-trip, and the assertion SHALL verify
the modified field changed and all other top-level sections are
byte-identical.

#### Scenario: Audit fails when an editor lacks a tape

- **GIVEN** a newly-added `ISectionEditor` registered in the menu
- **AND** no tape file at `tests/smoke/tapes/config-<sectionid>.tape`
- **WHEN** `MenuRegistryAuditTests` runs
- **THEN** the test fails with a message naming the missing tape path

#### Scenario: Audience tape exercises arrow nav and toggle

- **WHEN** `config-audience.tape` runs
- **THEN** the tape sends `↓`, `Space`, `↑`, `Space` keystrokes within
  the Team audience editor
- **AND** the assertion verifies the per-feature toggle state in
  `Tools.AudienceProfiles.Team`

#### Scenario: No-config refusal exits non-zero

- **GIVEN** the smoke test harness stages a `NETCLAW_HOME` containing
  no `config/netclaw.json`
- **WHEN** `config-no-init.tape` runs `netclaw config`
- **THEN** the command exits with non-zero status
- **AND** the assertion observes the refusal message on stderr

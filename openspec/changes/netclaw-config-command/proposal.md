## Why

After the `section-editor-abstraction` change lands, Netclaw has the
machinery to share editable sections between the init wizard and any new
command — but no command actually consumes it. Operators still have no way
to change live configuration (search provider, exposure mode, channels,
webhooks, skill feeds, external skill directories, Playwright, audience
profiles, security posture) without hand-editing `netclaw.json`. This
change introduces `netclaw config`, a menu-driven TUI editor that composes
the abstraction's section editors into a single dashboard with reentrant
section-by-section editing, doctor-blessed save, and a CI-enforced audit
that prevents the menu and the editors from drifting apart over time.

This change also retires the buggy team/public feature-toggle screen in
the existing init wizard (#1150) by replacing it with the new Audience
Profiles section editor, which exercises arrow navigation and toggle
keystrokes under a smoke tape rather than relying on undertested
hand-coded input handling.

Source PRDs: `PRD-004-cli-onboarding-and-config.md`,
`PRD-001-netclaw-mvp.md`, `PRD-002-gateway-security-envelope.md`.

## What Changes

- Add a new `netclaw config` top-level CLI command that launches Termina
  with a `ConfigDashboardPage` rendering every entry in
  `SectionEditorRegistry`. The dashboard computes per-section status
  (`✓` configured / `⚠` warning / `✗` error / `–` default) by running
  each editor's `RelevantDoctorChecks` on entry. Selecting a section
  opens its editor in single-step orchestrator mode; on save the
  section's checks run inline and either block (on errors), render a
  "Save anyway" affordance (on warnings), or accept the write (on
  clean). Returning from an editor refreshes the affected section's
  status.
- Add a "Run full doctor" item at the dashboard's tail that invokes the
  existing `DoctorRunner` with the same exit-code semantics as
  `netclaw doctor`, plus a "Quit" item.
- Add the dashboard's existing-config refusal: if `netclaw.json` is
  absent, `netclaw config` prints "No configuration found. Run
  `netclaw init` first." and exits non-zero. The dashboard does not
  render against a default skeleton.
- Add a generic `ListEditor<T>` Termina component and a per-shape
  `IItemEditor<T>` contract. Day-one item-editor implementations:
  `PathItemEditor` (External Skill Directories), `WebhookItemEditor`
  (Outbound Webhooks — sub-page form with name + URL + auth header),
  `SkillFeedItemEditor` (Skill Feeds — sub-page form with name + URL +
  Bearer token), and `IdentifierItemEditor` (channel IDs, user IDs,
  trusted-proxy CIDRs). Simple items edit inline; complex items open
  sub-pages. Multi-value sections gain a uniform Add / Edit / Remove
  affordance with default-Cancel destructive confirms.
- Add ten new `ISectionEditor` implementations registered in the menu:
  Search Provider, Slack Channels, Discord Channels, Mattermost
  Channels, Exposure Mode (covering Daemon host/port, trusted proxies,
  and per-mode sub-forms for Reverse Proxy / Tailscale / Cloudflare),
  Security Posture, Audience Profiles, Outbound Webhooks, Inbound
  Webhooks, External Skill Directories, Skill Feeds, Browser
  Automation. Slack/Discord/Mattermost share a `"Chat Channels"`
  category for menu grouping; the registry treats them as three
  independent editors.
- Add the Audience Profiles section editor as the replacement for the
  init wizard's broken feature-selection step. The editor SHALL exercise
  `↑/↓` navigation between audience tiers, `Space` to toggle individual
  per-audience feature flags, and explicit `Reset to posture default`
  affordance. A dedicated smoke tape (`config-audience.tape`) drives
  these keystrokes and asserts the resulting `Tools.AudienceProfiles`
  state.
- Add the Exposure Mode section editor with mode-conditional sub-forms.
  Trusted Proxies multi-value list, Reverse Proxy external base URL,
  Tailscale auth key (secret), and Cloudflare Tunnel token (secret) are
  all reachable from one editor. The editor migrates the responsibility
  previously covered by `init-wizard-reverse-proxy.tape` from init into
  the config command.
- Add four new doctor checks invoked by the new editors:
  `SearchBackendDoctorCheck` (backend-key pairing),
  `ExternalSkillSourcesDoctorCheck` (each path is a readable
  directory), `SkillFeedsDoctorCheck` (reachability, warn-only — remote
  endpoints are allowed to be transiently down), and
  `BrowserAutomationDoctorCheck` (Playwright binary present when
  feature is enabled).
- Add a new top-level schema section
  `BrowserAutomation { Enabled: bool, PlaywrightVersion?: string }` and
  the matching `BrowserAutomationConfig.cs`. Schema sync per CLAUDE.md
  rule. `"Enabled"` defaults to `false` so `SchemaFixResolver` can
  auto-insert on upgrade.
- Add twelve new smoke tapes (`config-search.tape`,
  `config-slack.tape`, `config-discord.tape`, `config-mattermost.tape`,
  `config-exposure-mode.tape`, `config-posture.tape`,
  `config-audience.tape`, `config-outbound-webhooks.tape`,
  `config-inbound-webhooks.tape`, `config-external-skills.tape`,
  `config-skill-feeds.tape`, `config-browser-automation.tape`) and a
  `config-no-init.tape` that asserts the refusal path. Each tape has a
  matching assertion script that checks the modified field changed and
  unrelated sections are byte-identical to the pre-stage fixture.
- Add round-trip xUnit test classes for all ten new section editors,
  derived from `SectionEditorTestBase<TEditor>` introduced in the prior
  change. The Change A test pattern carries forward unchanged.
- Activate the `MenuRegistryAuditTests` smoke-tape existence check
  (gated as soft-warn in Change A) into a hard fail: any registered
  editor without `tests/smoke/tapes/config-<section-lower>.tape`
  fails the audit.
- Closes #1150 (feature toggles broken for team/public dispositions —
  the buggy screen is removed and its responsibility moves to Audience
  Profiles).

**In scope (MVP):** the `netclaw config` command, the dashboard,
single-step editor hosting, ten new section editors, four new doctor
checks, the new `BrowserAutomation` schema section, generic list and
item editors, twelve new smoke tapes + the no-init refusal tape, ten
new round-trip xUnit test classes, the hardened audit, and a stderr
"daemon restart required to apply changes" nudge when the daemon is
running at config-command exit.

**Out of scope:** simplification of `netclaw init` (third change),
hot-reload of the running daemon on config change, export/import config
bundle, factory reset, route-file editing for inbound webhooks,
identity beyond what init sets (renaming the agent post-install remains
a file-edit task), telemetry/logging/memory/session/sub-agent/scheduling
config knobs (file-edit only), shell hard-deny patterns (file-edit
only), Playwright installation from within the TUI (instructions
sub-page only), and refactor of `netclaw provider`/`model`/`mcp` CLI
subcommands.

## Capabilities

### New Capabilities

- `netclaw-config-command`: contract for the `netclaw config` command —
  command-level lifecycle, dashboard rendering, per-section status
  computation, single-step editor hosting, doctor blessing on save,
  refusal when no config exists, daemon-restart nudge at exit,
  list/item editor framework, and the ten section editors' shared
  obligations.

### Modified Capabilities

- `netclaw-cli`: add `netclaw config` to the operator CLI surface; add
  the `Quit` and `Run full doctor` dashboard items as standard
  affordances.
- `feature-selection-wizard`: remove the feature-selection step from
  `netclaw init`. The deployment-wide feature toggles previously written
  by that step move to the Audience Profiles section editor in
  `netclaw config`, exposed per audience and per feature with the
  keystroke contract required by #1150.
- `channel-audience-tui`: re-host the existing channel-audience
  cycling behavior as the per-channel-editor sub-screen, retaining
  the requirement that audience defaults derive from posture but
  letting the operator override per-channel from the config command.

## Impact

**Affected systems:**

- CLI command surface (`Netclaw.Cli.Program` routing,
  `Netclaw.Cli.Config.ConfigCommand` new class).
- Termina TUI (`Netclaw.Cli.Tui.Sections.ConfigDashboardPage`,
  `ConfigDashboardViewModel`, `ListEditor<T>`, four item editors).
- Ten new section editors under
  `src/Netclaw.Cli/Tui/Sections/{Search,Channels/{Slack,Discord,Mattermost},ExposureMode,SecurityPosture,AudienceProfiles,Webhooks/{Outbound,Inbound},ExternalSkills,SkillFeeds,BrowserAutomation}/`.
- Doctor system gains four checks under
  `src/Netclaw.Cli/Doctor/Checks/`.
- Schema (`netclaw-config.v1.schema.json`) gains the `BrowserAutomation`
  top-level section.
- Configuration types (`src/Netclaw.Configuration/BrowserAutomationConfig.cs`).
- Test surface gains twelve smoke tapes, ten round-trip test classes,
  and a hardened menu registry audit.

**Security and operational impact:**

- Secret-handling contract from Change A applies to every secret-bearing
  field across the ten new editors. No new secret display surface is
  introduced; "Remove credential" is the only path that deletes a
  secret value.
- Doctor checks scoped to each editor run inline on save; cross-section
  checks remain gated to the dashboard's "Run full doctor" action. No
  network-probing check blocks save by default (`SkillFeedsDoctorCheck`
  is warn-only) so transient outages do not lock operators out of
  editing.
- The hardened audit prevents the menu and editors from drifting:
  adding a new menu entry without its tape or round-trip test fails
  CI immediately.
- Existing daemon does not hot-reload. A stderr nudge at config-command
  exit instructs operators to restart the daemon to apply changes when
  the daemon is detected as running; otherwise the nudge is omitted.
- The feature-selection step's removal is a behavioral change for
  operators on non-Personal postures who re-run `netclaw init` over
  existing config: they no longer see the step. Its responsibility
  moves to `netclaw config → Audience Profiles`. PRD-004 is updated
  in this change to reflect the new shape.
- No persistence schema changes. No new actor or session contract
  changes. No external network dependencies introduced.

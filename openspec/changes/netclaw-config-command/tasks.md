## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Confirm proposal, design, and spec deltas cover the
  `netclaw config` command, the dashboard, ten section editors, the
  generic list/item editor framework, the four new doctor checks, the
  schema addition for `BrowserAutomation`, twelve smoke tapes plus the
  no-init refusal tape, ten round-trip xUnit test classes, and the
  hardened menu registry audit.
- [ ] 1.2 Verify traceability references to `PRD-004`, `PRD-001`, and
  `PRD-002` across change artifacts.
- [ ] 1.3 Run `openspec validate netclaw-config-command --type change`
  and resolve all issues.

## 2. Schema and configuration types

- [ ] 2.1 Add a `BrowserAutomation` top-level section to
  `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`
  with `Enabled` (bool, default `false`) and `PlaywrightVersion`
  (string, optional). Use `additionalProperties: false`.
- [ ] 2.2 Add `src/Netclaw.Configuration/BrowserAutomationConfig.cs`
  matching the schema.
- [ ] 2.3 Update existing exemption list / schema-fix entries as needed
  so `SchemaFixResolver` can auto-insert `BrowserAutomation` on
  upgrade.

## 3. Dashboard scaffolding

- [ ] 3.1 Add `src/Netclaw.Cli/Config/ConfigCommand.cs` as the
  top-level command class wired into `Netclaw.Cli.Program` routing.
- [ ] 3.2 Add `src/Netclaw.Cli/Tui/Sections/ConfigDashboardPage.cs` and
  `ConfigDashboardViewModel.cs` rendering each `ISectionEditor` from
  the registry, plus "Run full doctor" and "Quit" items.
- [ ] 3.3 Implement per-section status badge computation at dashboard
  entry (runs each editor's `RelevantDoctorChecks` against on-disk
  config and caches results until the editor saves).
- [ ] 3.4 Implement category grouping (siblings sharing `Category`
  render under a single unselectable label).
- [ ] 3.5 Implement no-config refusal path: detect missing
  `netclaw.json` at startup, print refusal to stderr, exit non-zero.
- [ ] 3.6 Implement daemon-restart nudge: detect running daemon at
  exit; print stderr line only when (a) at least one section saved
  during the session AND (b) the daemon is running.

## 4. Generic list/item editor framework

- [ ] 4.1 Add `src/Netclaw.Cli/Tui/Sections/Components/IItemEditor.cs`
  with `DisplayRow`, `KeyOf`, `RequiresSubPage`,
  `CreateSubPageEditor`, `EditInline`, `AddInline`.
- [ ] 4.2 Add `src/Netclaw.Cli/Tui/Sections/Components/ListEditor.cs`
  implementing add (inline `+ Add` row), edit (inline or sub-page
  depending on item editor), remove (single-key `d` then `[y/N]`
  prompt), Save / Cancel, in-place rename via `KeyOf` semantics.
- [ ] 4.3 Add `PathItemEditor` (inline string edit; validates path
  existence/readability lazily on parent save).
- [ ] 4.4 Add `IdentifierItemEditor` (inline string edit; used by
  channel-ID lists, user-ID lists, trusted-proxy CIDR list).
- [ ] 4.5 Add `WebhookItemEditor` (sub-page form: name, URL, optional
  auth-header secret-handling, optional event filter).
- [ ] 4.6 Add `SkillFeedItemEditor` (sub-page form: name, URL,
  optional Bearer API key secret-handling, Test Connection
  affordance).

## 5. Shared editor components

- [ ] 5.1 Add `ValidationBanner` component for the inline
  errors-and-warnings band above the action row.
- [ ] 5.2 Add `DiscardChangesPrompt` (used on Esc-with-dirty-state in
  any editor).
- [ ] 5.3 Add `RemoveCredentialPrompt` (default-Cancel modal confirm
  for any secret removal).

## 6. Section editors — single-value

These editors REUSE existing step viewmodels where possible. Each
existing step viewmodel is REFACTORED to implement `ISectionEditor`
(per Change A's contract) and is moved into the new folder structure
under `src/Netclaw.Cli/Tui/Sections/<Section>/`. No new duplicate
classes are created for sections that today have an init step
viewmodel; the same class serves both init (when in the trimmed step
list, post Change C) and `netclaw config` (single-step mode).

- [ ] 6.1 `SearchSectionEditor` (`SectionId = "Search"`,
  `ShowInMenu = true`): refactor of existing `SearchStepViewModel`.
  Backend selector + conditional API key / SearXng URL fields. Honor
  `ExistingConfig`. `RelevantDoctorChecks`:
  `{ConfigSchemaDoctorCheck, SearchBackendDoctorCheck}`.
- [ ] 6.2 `SecurityPostureSectionEditor`
  (`SectionId = "Security.Posture"`, `ShowInMenu = true`): refactored
  to `ISectionEditor` in Change A; this change adds the cascade dialog
  (Cancel | Overwrite | Keep custom) when changing posture over
  customized `Tools.AudienceProfiles`.
- [ ] 6.3 `AudienceProfilesSectionEditor`
  (`SectionId = "Tools.AudienceProfiles"`, `ShowInMenu = true`): NEW
  editor (no init-step equivalent — the buggy `FeatureSelectionStepViewModel`
  is replaced by this editor). Audience picker (Personal | Team | Public)
  opening per-audience editor with toggleable feature rows,
  shell-mode selector, approval policy selector, and "Reset to
  posture default" affordance. MUST exercise arrow nav + Space toggle
  (#1150 contract).
- [ ] 6.4 `InboundWebhooksSectionEditor` (`SectionId = "Webhooks"`,
  `ShowInMenu = true`): NEW editor. Feature-flag toggle + request
  timeout integer.
- [ ] 6.5 `BrowserAutomationSectionEditor`
  (`SectionId = "BrowserAutomation"`, `ShowInMenu = true`): refactor
  of existing `BrowserAutomationStepViewModel`. Feature-flag toggle
  with Playwright detection at entry; install-instructions sub-page
  when Playwright absent.

## 7. Section editors — multi-value (compose ListEditor)

- [ ] 7.1 `OutboundWebhooksSectionEditor`
  (`SectionId = "Notifications.Webhooks"`, `ShowInMenu = true`): NEW
  editor. Uses `WebhookItemEditor`.
- [ ] 7.2 `ExternalSkillsSectionEditor`
  (`SectionId = "ExternalSkills"`, `ShowInMenu = true`): refactor of
  existing `ExternalSkillsStepViewModel`. Uses `PathItemEditor`.
- [ ] 7.3 `SkillFeedsSectionEditor` (`SectionId = "SkillFeeds"`,
  `ShowInMenu = true`): refactor of existing `SkillFeedsStepViewModel`.
  Uses `SkillFeedItemEditor`.

## 8. Section editors — chat channels (composite)

- [ ] 8.1 `SlackSectionEditor` (`SectionId = "Slack"`,
  `Category = "Chat Channels"`, `ShowInMenu = true`): refactor of
  existing `SlackStepViewModel`. Bot token + app token, allowed
  channels list, allowed users list, DMs toggle, audience profile
  selector, Test Connection. Reuses `channel-audience-tui` cycling
  component for the channel list.
- [ ] 8.2 `DiscordSectionEditor` (`SectionId = "Discord"`,
  `Category = "Chat Channels"`, `ShowInMenu = true`): refactor of
  existing `DiscordStepViewModel`. Single bot token, same affordances
  otherwise.
- [ ] 8.3 `MattermostSectionEditor` (`SectionId = "Mattermost"`,
  `Category = "Chat Channels"`, `ShowInMenu = true`): refactor of
  existing `MattermostStepViewModel`. Server URL + bot token, same
  affordances otherwise.

## 9. Section editor — exposure mode (composite)

- [ ] 9.1 `ExposureModeSectionEditor`
  (`SectionId = "Daemon.ExposureMode"`, `ShowInMenu = true`): refactor
  of existing `ExposureModeStepViewModel`. Mode selector (Local |
  Reverse Proxy | Tailscale | Cloudflare Tunnel), daemon host/port
  fields, mode-conditional sub-forms.
- [ ] 9.2 Reverse Proxy sub-form: external base URL + trusted
  proxies list (via `ListEditor<T>` + `IdentifierItemEditor`).
- [ ] 9.3 Tailscale sub-form: auth key (secret) + hostname.
- [ ] 9.4 Cloudflare Tunnel sub-form: tunnel token (secret) +
  optional access-policy email domain.
- [ ] 9.5 Add `Daemon` to `SectionEditorExemptions` with category
  `"covered by another editor's dotted-path SectionId"` naming
  `Daemon.ExposureMode` as the owner. The non-exposure parts of
  `Daemon` (host, port, trusted proxies) are part of the
  ExposureModeSectionEditor's surface.
- [ ] 9.6 Add `Security` to `SectionEditorExemptions` with category
  `"covered by another editor's dotted-path SectionId"` naming
  `Security.Posture`.
- [ ] 9.7 Add `Tools` to `SectionEditorExemptions` with category
  `"covered by another editor's dotted-path SectionId"` naming
  `Tools.AudienceProfiles`.

## 10. New doctor checks

- [ ] 10.1 `SearchBackendDoctorCheck` (validates backend ↔ required
  credential pairing; ERROR when Brave/SearXng configured without
  required field).
- [ ] 10.2 `ExternalSkillSourcesDoctorCheck` (validates each path is
  an existing readable directory).
- [ ] 10.3 `SkillFeedsDoctorCheck` (validates URL reachability;
  WARN-only — transient outages don't block saves).
- [ ] 10.4 `BrowserAutomationDoctorCheck` (ERROR when
  `BrowserAutomation.Enabled = true` and Playwright binary not
  resolvable from PATH).
- [ ] 10.5 Register each new check via the existing doctor
  registration extensions so they participate in
  `netclaw doctor` runs.

## 11. DI wiring

- [ ] 11.1 Register all ten new editors via
  `services.AddSectionEditor<TEditor>()` in the CLI DI composition
  root.
- [ ] 11.2 Confirm registry construction fails fast on any duplicate
  `SectionId`.
- [ ] 11.3 Wire `ConfigCommand` into the CLI top-level command
  dispatch.

## 12. Round-trip xUnit tests (Layer 2)

- [ ] 12.1 `SearchSectionEditorTests` covering single-value path and
  the DuckDuckGo ↔ Brave backend switch preserves Brave key
  scenario.
- [ ] 12.2 `SlackSectionEditorTests` covering reentrancy across
  channel-list + user-list + secret-handling for both tokens.
- [ ] 12.3 `DiscordSectionEditorTests`.
- [ ] 12.4 `MattermostSectionEditorTests` (incl. server URL field).
- [ ] 12.5 `ExposureModeSectionEditorTests` covering all four mode
  sub-forms.
- [ ] 12.6 `SecurityPostureSectionEditorTests` covering all three
  cascade options.
- [ ] 12.7 `AudienceProfilesSectionEditorTests` covering toggle
  rount-trip and posture-default reset.
- [ ] 12.8 `OutboundWebhooksSectionEditorTests` covering add /
  edit / remove / in-place rename preserves item identity.
- [ ] 12.9 `InboundWebhooksSectionEditorTests`.
- [ ] 12.10 `ExternalSkillsSectionEditorTests` (incl. invalid-path
  inline validation).
- [ ] 12.11 `SkillFeedsSectionEditorTests` (incl. WARN-only reachability
  behavior).
- [ ] 12.12 `BrowserAutomationSectionEditorTests` (incl.
  toggle-disabled-when-absent behavior).

## 13. Smoke tapes (Layer 1)

- [ ] 13.1 `config-search.tape` + assertion: pre-stage Brave + key,
  switch to DuckDuckGo, save, assert backend=duckduckgo and Brave
  key preserved.
- [ ] 13.2 `config-slack.tape` + assertion: pre-stage tokens + 2
  channels, add 1 channel, save, assert 3 channels and tokens
  unchanged.
- [ ] 13.3 `config-discord.tape` + assertion.
- [ ] 13.4 `config-mattermost.tape` + assertion (incl. URL + token +
  channel).
- [ ] 13.5 `config-exposure-mode.tape` + assertion: pre-stage Local,
  switch to Reverse Proxy, add CIDR, save, assert mode and CIDR
  changes plus byte-equal unrelated sections. Migrates coverage
  from former `init-wizard-reverse-proxy.tape`.
- [ ] 13.6 `config-posture.tape` + assertion: change Personal →
  Team, accept cascade, save, assert posture and audience-default
  changes.
- [ ] 13.7 `config-audience.tape` + assertion: exercise `↓`,
  `Space`, `↑`, `Space` keystrokes on Team audience editor, save,
  assert `Tools.AudienceProfiles.Team` toggle state. This tape is
  the #1150 regression guard.
- [ ] 13.8 `config-outbound-webhooks.tape` + assertion: pre-stage 1
  webhook, add 2nd via sub-page, save, assert array length 2 and
  first byte-identical.
- [ ] 13.9 `config-inbound-webhooks.tape` + assertion.
- [ ] 13.10 `config-external-skills.tape` + assertion: pre-stage 1
  path, add 1 + remove the original via `d`, save, assert single
  remaining new entry.
- [ ] 13.11 `config-skill-feeds.tape` + assertion: pre-stage empty,
  add 1 feed with Bearer key via sub-page, save, assert feed in
  config + key in secrets.
- [ ] 13.12 `config-browser-automation.tape` + assertion: pre-stage
  Playwright absent, open install instructions, exit without save,
  assert no config write.
- [ ] 13.13 `config-no-init.tape` + assertion: stage empty
  `NETCLAW_HOME`, run `netclaw config`, assert non-zero exit and
  stderr refusal message.

## 14. Menu registry audit promotion

- [ ] 14.1 In `MenuRegistryAuditTests`, flip the smoke-tape
  existence check from soft-warn to hard-fail. The test asserts a
  matching tape file at `tests/smoke/tapes/config-<sectionid>.tape`
  for every registered editor.
- [ ] 14.2 Update the audit's failure-message text to name (a) the
  editor's `SectionId`, (b) the missing artifact path, (c) the
  remediation step ("add a tape" / "add a test class" / "declare
  `RelevantDoctorChecks` or `[NoDoctorChecks]`").

## 15. PRD-004 update

- [ ] 15.1 Update `docs/prd/PRD-004-cli-onboarding-and-config.md`:
  replace the "reentrant init dashboard" wording with the
  simplified-init + `netclaw config` split. List the ten section
  editors as the menu surface.
- [ ] 15.2 Cross-reference issues #455 (closed in Change A) and
  #1150 (closed in this change).

## 16. Quality gates

- [ ] 16.1 `dotnet build` clean.
- [ ] 16.2 `dotnet test` clean: all round-trip tests pass; audit
  passes (every registered editor has tape + test class + doctor
  checks); existing tests remain green.
- [ ] 16.3 `./scripts/smoke/run-smoke.sh light` clean (all 12 new
  config tapes plus the no-init refusal tape pass).
- [ ] 16.4 `dotnet slopwatch analyze` reports no new violations.
- [ ] 16.5 `./scripts/Add-FileHeaders.ps1 -Verify` reports clean.
- [ ] 16.6 `openspec validate netclaw-config-command --type change`
  passes.

## 17. Documentation

- [ ] 17.1 Update CLI `--help` text for `netclaw config` so the
  command is discoverable from `netclaw --help`.
- [ ] 17.2 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  per CLAUDE.md system-skills sync rule, adding a section that
  describes `netclaw config` and the ten editable sections. Bump
  `metadata.version`.
- [ ] 17.3 PR description closes #1150 and references this OpenSpec
  change ID.

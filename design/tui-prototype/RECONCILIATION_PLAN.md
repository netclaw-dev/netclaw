# Reconciliation Plan — `/opsx` to Done for `netclaw init` + `netclaw config`

Companion to `FINDINGS.md`. `FINDINGS.md` says *what the UX is*; this says *how we
land it in OpenSpec + Termina C# and archive the changes*. Grounded in the real task
tallies on the `init-reentrant` line (read on 2026-06-09).

---

## 🎯 Goal (north star)

> Ship the prototype-proven `netclaw init` and `netclaw config` terminal UX as the
> real product. Every in-scope OpenSpec change is reconciled to `FINDINGS.md`,
> implemented in Termina, `/opsx-verify`'d, its delta specs `/opsx-sync`'d into the
> main specs, and `/opsx-archive`'d — with the legacy 11-step init reduced to the
> 5-step bootstrap and the **lighter** validation infrastructure in place.

## ✅ "opsx completed for both" — Definition of Done (checkable)

- [ ] All four in-scope changes reach **archived** state (`/opsx-verify` → `/opsx-sync` → `/opsx-archive`).
- [ ] `openspec/specs/` reflects the prototype UX for init + config (intent == proven design).
- [ ] **Init:** wizard reduced 11→5 steps; existing-install menu + reset/double-confirm shipped; Personal skips Features.
- [ ] **Config:** status-summary dashboard, unified selection bar, multi-webhook, channels first-time-setup-in-config + resolve-before-add, probe-driven credential disclosure all live.
- [ ] **Infra is light:** commit pipeline kept as the single seam; **no analyzer**; nullable dynamic check (no `NotApplicable` ceremony); Skill Sources inline (factory retired); typed probe result.
- [ ] **Project DoD gates green:** `dotnet slopwatch analyze`; copyright headers; smoke tapes for every TUI surface; eval suite for identity/skill/tool changes; mapped system skills updated.

## Current state (grounded)

| Change | Tasks | Role | Disposition |
|---|---|---|---|
| `simplify-netclaw-init` | **0 / 30** | init | Build it — fully defined by prototype |
| `netclaw-validated-ui-components` | **19 / 63** | config infra | **Revise lighter**, cancel ~half, then finish |
| `netclaw-config-command` | **60 / 67** | config UX | Finish 7 + add prototype deltas |
| `section-editor-abstraction` | **42 / 42** | config infra | Done — confirm still valid (likely no-op) |

---

## Step 1 — `simplify-netclaw-init`  → *init done* (do first)

Isolated, fully defined, fast win, and it runs the full `/opsx` lifecycle once as a
template for the rest.

**Reconcile (`/opsx-continue` / `/opsx-ff`)** — the change is design-only; make the
prototype the spec. The 30 tasks already match the target; confirm/adjust the deltas:
- §2 First-run bootstrap → 5 steps **Provider → Identity → Posture → Features → Health**; Personal skips Features.
- §3 Existing-install menu → `Redo identity setup` / `Open configuration editor` / `Start over from scratch` / `Cancel`; "Open configuration editor" routes to `netclaw config`.
- §4 Start-over → `Reset setup only` / `Full reset` / `Cancel`, **double-confirm**, destructive on red; remove all `--force` planning.
- §5 Identity stays **owned by init**.
- §6 Post-flight → `netclaw chat` / `netclaw config` nudge.

**Apply (`/opsx-apply`)** — `src/Netclaw.Cli/Tui/`:
- `InitWizardViewModel.cs` — the `steps` list (~L103–115) and the view dictionary
  (~L145–155) register **11** steps today: Provider, SecurityPosture, FeatureSelection,
  ChannelPicker, Channels, Search, BrowserAutomation, Identity, ExternalSkills,
  SkillFeeds, HealthCheck. Reduce to **5** and reorder: **Provider, Identity,
  SecurityPosture, FeatureSelection, HealthCheck**. Drop ChannelPicker, Channels,
  Search, BrowserAutomation, ExternalSkills, SkillFeeds from init registration (they
  become config-only).
- The dropped `*StepView`/`*StepViewModel` classes: **verify references before
  deleting** — config may reuse the patterns. Default: leave the classes, just remove
  them from init's registration; delete only if genuinely unreferenced.
- Gate Features on posture (`Personal` → skip `FeatureSelectionStep`).
- Step indicator → "Step N of 5".
- New: existing-install detection + 4-option menu page.
- New: reset flow (scope dialog + double confirm).

**Gates:** `init-wizard.tape` + new `existing-install` / `reset` tapes
(`./scripts/smoke/run-smoke.sh init-wizard`); **eval suite** (identity templates in scope).

**Close:** `/opsx-verify` → `/opsx-sync` → `/opsx-archive`.

---

## Step 2 — `netclaw-validated-ui-components`  → *config infra done* (do second)

It's the contract the config UX writes through — settle the seam before reworking
pages on top of it. **Revise the change artifacts first** (`/opsx-continue` to amend
`design.md` + `tasks.md`; never hand-edit), to the lighter contract:

- **§2 Core primitives — keep, but delete the union.** In `NetclawUiCommit.cs` remove
  `NetclawUiDynamicCheck<TDraft>` + `Required` / `NotApplicable(justification)` (~L75–116,
  ~30 lines); the `is RequiredCheck` branch becomes `is not null`. Dynamic check is now
  **nullable/optional** (absent = static-only, the 90% case).
- **§3 Validated components — keep as light optional wrappers.** `NetclawValidatedTextField`
  / `Picker` survive only where async validation earns it (probe-driven combined forms); slim them (~200 lines edited).
- **§4 Build enforcement — CANCEL entirely.** The Roslyn analyzer was never written;
  don't write it. Enforce the single seam by **encapsulation** (config writer reachable
  only through `NetclawUiCommitPipeline`).
- **§6 Skill Sources — REWORK.** Normalize `SkillSourcesConfigPage`/VM to the inline
  `ConfigEditorSession` style the other pages use; **retire `SkillSourcesCommitFactory.cs`
  (~278 lines)**. (This is also the Skill Sources delta for Step 3 — do it once here.)
- **§7 Remaining leaf migrations — CANCEL.** Channels/Telemetry/Security/Search already
  validate inline; that lighter style *is* the target. No mandatory commit object retrofit.
- **§8 Audit/deletion — keep, trimmed.** Obsolete-artifact deletion now = the factory + the union.
- **Typed probe result:** dynamic checks, when present, return `{ reason: ok |
  auth-required | unreachable, facts }`; editors branch on `reason` (powers probe-driven disclosure).
- **Keep wholesale:** `NetclawUiCommitPipeline` (~48 lines, *is* the seam), `NetclawValidationDialog`, the result/tone records.

**Net:** ~50 lines deleted, ~200 reworked, factory (~278) retired, ~44 unbuilt tasks cancelled.

**Scope discovery (read 2026-06-09 — concrete code map):**
- The union to delete is `NetclawUiCommit.cs` **L75–116** (`NetclawUiDynamicCheck<TDraft>` +
  `RequiredCheck`/`NotApplicableCheck`). The consuming branch is the pipeline at **L176–190**
  (`is …RequiredCheck required`) → becomes a nullable-validator check. `NetclawUiCommit<TDraft>`
  (L118–160) drops its non-null `DynamicCheck` ctor guard.
- **`SkillSourcesCommitFactory.cs` is 278 lines / ~14 factory methods**; the page has **54**
  factory/validated-component call-sites; the VM is **2125 lines** with **15+ direct-write
  `Save*` methods** (each `ConfigFileHelper.LoadJsonDict` → mutate → `WriteConfigFile`).
  The target (`ChannelsConfigViewModel`) uses `ConfigEditorSession` + `SectionContribution`
  + a `_mapper.BuildContribution`. **Full normalization is far larger than the "~200 lines"
  estimate** — it is a real VM refactor, not a lightening-in-place.
- **Recommended phasing for Step 2** (keeps it shippable): (1) delete the union + make the
  dynamic check nullable + typed probe result + cancel §4/§7 in the artifacts — the
  high-value, bounded "lighter contract" core; (2) retire the factory by inlining its builders
  at the call sites (removes the indirection); (3) treat the full `ConfigEditorSession`
  normalization of the 2125-line VM as a **separate, optional consistency pass** — flag for the
  user before committing to it, since it is internal-only (no UX change) and high-churn.

**Apply** the deletions/rework → **gates** (slopwatch, headers, `config-skills` tape) →
`/opsx-verify` → `/opsx-sync` → `/opsx-archive`.

---

## Step 3 — `netclaw-config-command`  → *config UX done* (do third)

7 tasks remain + the prototype deltas. Add deltas with `/opsx-continue` where the
prototype changed intent; spin a small `/opsx-new` change only where a feature is
genuinely net-new (default to deltas):

- **§3 Root dashboard IA** — status-summary column (`Label  <status>`, e.g. `Search  ✓ Brave`,
  `Security & Access  Team · 4/6 enabled`) with focused item's description as a dim help
  line. Replaces the static-description column. → `ConfigDashboardViewModel`/`Page`.
- **Unified selection bar** — sub-editors use a `▶`-marker today; unify on the full-width
  teal bar everywhere. → config-page selection rendering.
- **§5 Channels** — channels-in-config + **first-time adapter setup** (config-native linear:
  adapter creds → probe → optional first channel → lands in that adapter's menu) +
  **resolve-before-add** (resolve against adapter before save; add at **system-default
  audience**; `←/→` to tune on the list) + **generalize active adapter** (was Slack-hardcoded)
  for Slack/Discord/Mattermost. Slack = bot + app token (Socket Mode, **no signing secret**);
  Discord = bot token; Mattermost = server URL + bot token. → `ChannelsConfigViewModel`/`Page`,
  `ChannelsEditorModel`.
- **§7 Telemetry & Alerting** — multi-webhook list editor (**Name / URL / one Authorization
  header**; **Format auto-detected** from `hooks.slack.com`, read-only). Backing type already
  exists: `NotificationsConfig.Webhooks : List<WebhookTarget>`. Delivery policy parked.
  → `TelemetryAlertingConfigViewModel`/`Page`.
- **Inbound Webhooks** — diagnostic ordering fix: enable endpoint first, *then* add routes
  with `netclaw webhooks set`; fail closed until one route exists. → wording in the inbound page.
- **Search + Skill Sources** — probe-driven disclosure (endpoint → probe → 401 reveals secret
  on a combined endpoint+secret form, `↑/↓` or `Tab`). Search → `SearchConfigEditor`; Skill
  Sources page already normalized in Step 2.

**Apply** → **gates** (`config-*.tape` per surface, slopwatch, headers, evals if tool/skill
content changed) → `/opsx-verify` → `/opsx-sync` → `/opsx-archive`.

---

## Step 4 — `section-editor-abstraction`  → confirm (do last)

42/42, deployed. Confirm the uniform-leaf abstraction still holds after the Step 2
revision (`FINDINGS.md` §4: formalize that *variant* editors are bespoke pages that
still write through the one seam). Likely a one-line clarifying delta folded into
config-command's design, or a no-op. Don't re-open unless behavior changes.

---

## Final cross-surface gate (both done)

- [ ] `dotnet slopwatch analyze` — no new violations
- [ ] `./scripts/Add-FileHeaders.ps1 -Verify`
- [ ] `./scripts/smoke/run-smoke.sh light` — init-wizard + config-* + new existing-install/reset tapes
- [ ] `./evals/run-evals.sh` — identity/skills/tools changes
- [ ] System skills updated + version-bumped: `netclaw-operations` (config/doctor/CLI/webhooks),
      `netclaw-identity` (init identity flow)
- [ ] All four changes archived; `openspec/specs/` reflects the prototype

## Sequencing rationale

Init first (isolated, fully defined, template run of the full `/opsx` lifecycle) →
infra second (settle the write-seam before building config pages on it; Skill Sources
normalization happens here, once) → config UX third (builds on the settled seam) →
section-editor last (confirm-only).

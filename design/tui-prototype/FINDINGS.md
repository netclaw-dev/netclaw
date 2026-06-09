# Netclaw TUI Prototype — Findings & Decisions

Bridge artifact between the browser prototype (`design/tui-prototype/`) and the
C# / OpenSpec work on the `init-reentrant` line. The prototype is now the **design
source of truth** for the `netclaw init` + `netclaw config` terminal UX. This doc
captures the validated decisions, the corrections it surfaced, the infrastructure
stance we landed on, and the agreed process — so reconciliation and translation can
proceed (and survive context compaction).

Branch: `claude-wt-netclaw-config-tui-design`. Run: `python3 design/tui-prototype/serve.py`
then open `index.html` (no-cache server; reloads always pick up latest).

---

## 1. Status of the design surface (on `init-reentrant`)

| OpenSpec change | State before prototype | What the prototype changes |
|---|---|---|
| `simplify-netclaw-init` | 0/30 (design-only; legacy 10-step ships) | **Fully defined now** — ready to implement |
| `netclaw-config-command` | ~60/67 (mostly built) | Refinements (status column, unified bar, multi-webhook, channels-in-config, resolve-before-add) |
| `netclaw-validated-ui-components` | ~19/63 (in progress) | **Revise, don't archive** — keep invariants, lighten enforcement (§4) |
| `section-editor-abstraction` | 42/42 (deployed) | Stays valid for uniform leaves; formalize bespoke variant editors |
| `docs/ui/TUI-002/003/005` | wireframes | Superseded/refined by the prototype |

---

## 2. Validated design language (applies everywhere)

- **Terminal-faithful rendering.** Catppuccin Mocha; fixed char grid (~156×50); box-drawing
  borders rendered **per-cell at full row height** so they fuse uniformly (font 14 in 16px
  rows for leading). The translator should mirror Termina `BorderStyle.Rounded`.
- **One interaction model, no modes:**
  - `↑/↓` moves the cursor *everywhere* — menus, lists, toggles, **and** form fields.
  - `Tab`/`Shift+Tab` is a **free alias** for `↑/↓` on multi-field forms (form muscle memory).
  - `←/→` cycles an option in place (and autosaves).
  - `Space` toggles; `Enter` applies/advances; `Esc` goes back and **never saves**.
  - **Autosave on completed actions** (toggles, cycles, picks, deletes); incomplete text drafts
    stay in memory until `Enter`. **Reentrancy:** back out and return with state intact.
- **Unified selection style.** A full-width teal highlight bar **everywhere**. (Real code mixes a
  bar on the dashboard with a `▶`-marker in sub-editors — unify on the bar.)
- **Dashboard = scannable status column.** `Label  <status summary>` (e.g. `Search  ✓ Brave`,
  `Security & Access  Team · 4/6 enabled`) with the focused item's description as a dim help
  line — *not* the current static-description column.
- **Uniform leaves vs bespoke variants.** Genuinely uniform leaf editors (single value / toggle /
  cycle / routed handoff) share ONE small row editor. Genuinely *variant* editors (Search,
  Exposure, Channels, Skill Sources, Provider) are first-class **bespoke pages**. This is the
  concrete answer to the "universal framework" wart — don't force variations through one shape.
- **Probe-driven credential disclosure (house style).** When a credential's necessity is a
  *runtime* property of the target (not a static field flag): ask for the endpoint → probe →
  on **401** reveal the secret on a **combined endpoint + secret form** (`↑/↓` or `Tab`) → re-probe.
  Open targets never see the secret field. Used for SearXNG (API key) and Skill Sources (bearer
  token); identical mechanics, different label. **Probe always runs with credentials in hand.**

---

## 3. Per-area findings & corrections

- **Channels**
  - **Resolve before assign:** the add-channel screen asks only for the channel; it's resolved
    against the adapter (exists? bot can see it?) *before* saving. A non-resolving channel errors
    instead of being saved.
  - **Add at the system-default audience** (deployment posture), focus the new row, then tune with
    `←/→` on the list. No audience picker during add. (Matches real behavior.)
  - **First-time setup lives in config** (the simplified init defers channels). Config-native linear
    flow: adapter-specific credentials → probe → optional first channel → lands in that adapter's
    management menu. The **active adapter is generalized** (was hardcoded to Slack), so the whole
    management surface works for Slack/Discord/Mattermost.
  - **Credentials are adapter-specific:** Slack = bot + app token (Socket Mode, **no signing
    secret**); Discord = bot token; Mattermost = server URL + bot token.
- **Skill Sources** — unified inventory (Local folders + Remote skill servers) + add/rescan;
  source detail with per-source actions; add-local (path → symlinks security → name); add-remote
  uses the probe-driven disclosure (URL → probe → 401 reveals bearer-token form). **Bespoke page,
  validates inline** (see §4 — normalize off the commit factory).
- **Telemetry & Alerting** — expose **multiple** outbound webhooks (config already has
  `NotificationsConfig.Webhooks : List<WebhookTarget>`; the TUI under-exposed it). List editor +
  add/edit form: **Name, URL, one Authorization-style header**; **Format auto-detected** from the
  URL (`hooks.slack.com` → Slack) and shown read-only. **Delivery policy** (dedup/retries/timeout)
  intentionally **parked**.
- **Inbound Webhooks** — **diagnostic ordering fix:** enable the endpoint first, *then* add routes
  with `netclaw webhooks set`; requests fail closed until one exists. (Real C# wording implies the
  reverse — carry the fix back.)
- **Exposure Mode** — mode picker → mode-specific sub-forms (reverse-proxy bind/proxies/notice;
  Tailscale-serve notice; funnel/cloudflare high-risk confirm); inactive-mode values retained.
- **Security & Access** — posture (inline + cascade), enabled features (Space toggle), audience
  profiles (tool toggles + `←/→` cycle selectors + reset; MCP grants is an `[Open]` handoff),
  exposure mode (routed). Destructive options on a **red** bar.
- **Simplified `netclaw init`** — 5 steps with a `Step N of 5` indicator: Provider → Identity
  (4-field form) → Security Posture → Enabled Features (**Personal skips**) → Health Check
  (post-flight summary + `netclaw chat` / `netclaw config` nudge). Plus the **existing-install
  menu** (Redo identity / Open config / Start over / Cancel) and **reset** (scope: setup-only vs
  full → double-confirm, destructive on red).

---

## 4. Infrastructure stance — `netclaw-validated-ui-components`

**The goals are right; the mechanism was over-opinionated.** Keep the invariants, lighten the
enforcement. The over-opinionated machinery barely shipped (one screen + a never-built analyzer),
so this is mostly *not building the rest* + a small simplification — not a teardown.

**Keep (invariants):** static validation on every data input; **one persistence seam** (no raw
writers; section-preserving merges); dynamic validation **where the value is runtime-dependent**.
The prototype reinforces all three (autosave/probe are validated commits).

**Lighten (mechanism):**
- **Dynamic check becomes optional/nullable** — absent = static-only (the 90% case, zero ceremony);
  present = an async validator + failure policy. **Delete** `NetclawUiDynamicCheck<TDraft>` with its
  `Required` / `NotApplicable(justification)` union (`NetclawUiCommit.cs` lines ~75–116, ~30 lines)
  and the `is RequiredCheck` branch becomes `is not null`. No more justification ceremony.
- **No Roslyn analyzer.** It was **never written** — don't write it. Enforce the single seam by
  **encapsulation** (the config writer is reachable only through the pipeline), not by an analyzer
  policing every component shape.
- **No mandatory commit object everywhere.** Validation lives **with the editor**: the shared row
  editor carries it for uniform leaves; bespoke editors validate inline. A checkbox doesn't need a
  generic `NetclawUiCommit<TDraft>`.
- **Typed probe result.** Dynamic checks, when present, return `{ reason: ok | auth-required |
  unreachable, facts }`; the editor branches on `reason` (this is what powers probe-driven disclosure).

**Keep / rework / delete / cancel tally (production code):**
- **Keep wholesale:** `NetclawUiCommitPipeline` (~48 lines — *is* the single seam), `NetclawValidationDialog`,
  the result/tone records. `NetclawValidatedTextField`/`Picker` stay as **light optional wrappers**
  where async validation genuinely earns it (probe-driven combined forms).
- **Delete:** ~50 lines — the dynamic-check union + `NotApplicable` call-sites.
- **Rework (edit):** ~200 lines — slim the validated components; **and Skill Sources (decided):**
  **normalize it to the inline `ConfigEditorSession` style** the other config pages use and
  **retire `SkillSourcesCommitFactory.cs` (~278 lines) entirely** — match the prototype's bespoke
  inline-validating page. (More churn than lightening in place, but consistent, which is the call.)
- **Cancel (don't build the remaining ~44 tasks):** the analyzer + the cross-screen retrofit of the
  mandatory commit object. Channels/Telemetry/Security/Search already validate inline — that lighter
  style *is* the target.

`section-editor-abstraction` (deployed) stays valid for uniform leaves; just formalize that variant
editors are bespoke pages that still write through the one seam.

---

## 5. Process & next steps (agreed)

1. **This findings doc**, then **just-in-time reconciliation** per area (not a big upfront pass).
2. **Merge `design/tui-prototype/` into the `init-reentrant` branch** (where the C# + OpenSpec
   changes live); do reconciliation + translation there with the prototype as the in-repo reference.
3. **Reconcile via `/opsx` skills (never hand-edit OpenSpec artifacts):**
   - `simplify-netclaw-init` — update design/spec to the prototype (it's 0% and fully defined), then implement.
   - `netclaw-config-command` — add deltas: status-column dashboard, unified bar, multi-webhook,
     channels-in-config + first-time setup, channel resolve-before-add.
   - `netclaw-validated-ui-components` — **revise** to the lighter contract above; shrink the task list.
4. **Translate to Termina C#** screen-by-screen, carrying the §3 corrections.

---

## 6. Prototype commit log (this branch)

```
cca2d892 Channels: resolve channel before add; add at system-default audience
e454bcff Channels: add first-time adapter setup; generalize active adapter
54210344 Telemetry: expose multiple outbound webhooks (list editor)
14a29cee Add simplified netclaw init flow — completes the prototype
88aedf82 Unify skill-server remote flow with Search's probe-driven disclosure
c57af21b Add Skill Sources editor — completes the netclaw config surface
f454aca7 Fix Slack credentials: Socket Mode bot+app tokens, no signing secret
47a4fbba Add Channels multi-step adapter editor
df6482f7 Add probe-driven API-key disclosure to Search; fix inbound webhook hint
340a09f4 Add uniform leaf config editors (Inbound/Browser/Telemetry/Workspaces)
60120d8c Add netclaw config tracer + Security & Access to TUI prototype
9fabeef3 Add browser-based terminal-faithful TUI prototype for init/config UX
```

Files: `index.html`, `theme.css`, `serve.py`, `engine/{screen,widgets}.js`,
`mock/{store,initctx}.js`, `screens/*.js` (init-* and config-*).

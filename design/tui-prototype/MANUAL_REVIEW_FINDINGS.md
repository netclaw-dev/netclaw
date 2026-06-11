# Manual TUI Review — Findings Log

Findings from the interactive Docker-sandbox review of `netclaw config` / `netclaw init`
against the prototype-proven design (`FINDINGS.md`, `RECONCILIATION_PLAN.md`).
Driver: real terminal via `docker exec -it netclaw-config-poc-local …`.

## Fixed this session

1. **`/config` Termina host skipped `ConfigureNativeSelection`** — it was the only host
   (vs `init`/`provider`/`model`/`chat`/…) without raw input, so it emitted mouse-tracking
   (`\e[?1000h`) and broke native terminal drag-select. Fixed: `Program.cs` `/config` host now
   calls `ConfigureNativeSelection(t)` like every other host. *Tape follow-up:* re-validate the
   `config-*` smoke tapes under raw input before push.

2. **Inbound Webhooks could not be enabled without a route (backwards gate).** Editor blocked
   `Webhooks.Enabled=true` until a route existed; doctor mirrored it as a hard `Error`. But the
   spec (`inbound-webhooks/spec.md:14`) says `Webhooks.Enabled` is *only* the feature toggle and
   the runtime 404s every request with no routes (inert, default-deny). Fixed: enable-first now
   persists + shows an advisory; doctor downgraded `Error`→`Warning`; editor/doctor tests + the
   `config-surfaces` tape updated.

3. **Telemetry webhook form placeholders read like entered values.** The form hand-rolled rows
   via `ConfigSelectionRow` + plain strings (unlike Search/Channels which use `TextInputNode`),
   so `(optional)` / the example URL rendered in the same colour as real input. Fixed:
   `ConfigSelectionRow.CreateLabeled` two-tone (bright label, dim-gray placeholder, bright value);
   URL example prefixed `e.g.`.

4. **Skill Sources auth flow violated probe-driven disclosure.** It showed an upfront `AddRemoteAuth`
   choice screen ("No auth required / Bearer token") *before* probing, contradicting the house style
   (`FINDINGS.md:48-51`, `RECONCILIATION_PLAN.md:147`, prototype commit `88aedf82`). Fixed to match
   SearXNG: **enter URL → probe with no auth → reveal the bearer-token field only on `401/403` →
   re-probe → save.** Open servers never see the token field. `SkillFeedReachabilityResult` now carries
   `RequiresAuth`; the `AddRemoteAuth` screen + its 7 VM/page handlers were removed; VM + Task1
   page-integration tests rewritten to the new flow.
   - **Two latent bugs surfaced + fixed while wiring the "save anyway" override for an unreachable
     *open* server:** (a) `ContinueAddRemoteUrl` cleared `_saveAnywayFingerprint` on every Enter
     (it's already cleared at flow start by `ClearPendingFlow`), so the second Enter never matched;
     (b) `SkillSourcesConfigPage.CommitCurrentTextScreen` re-staged the input on every Enter, and
     `ReplaceDraft → MarkDirty` cleared the fingerprint through the real Termina pipeline — guarded to
     only re-stage when the text actually changed. Without these an unreachable open feed could not be
     force-added at all.

9. **(INPUT DATA LOSS — FIXED) Pasting into a non-empty channel text field dropped the paste.**
   Typing one Slack user ID then pasting a second only kept a single char (or none). **Root cause
   (confirmed via headless repro + instrumentation):** Termina auto-routes a bracketed paste straight
   into the focused `TextInputNode` and *consumes* the event, so the page's `PasteEvent` handler never
   fires. Each adapter input is rebuilt and re-seeded from the view-model on every render, so a paste
   that landed only in the node was wiped by the next reseed (typed chars survived because each
   keystroke stages back to the view-model; the auto-routed paste did not). Tokens "worked" only
   because Enter→`Submitted` reads the node before a reseed. **Fix:** every Slack/Discord/Mattermost
   text input now subscribes to `TextChanged` (new `WizardStepHelpers.SyncInputToViewModel`, reusing
   each view's `StageFocusedInput`) so keystrokes *and* pastes sync to the view-model the instant they
   land — render-independent. Two headless regressions (type-then-paste for the user-IDs field and the
   token field). NOT a Termina bug — the auto-route is documented behavior `SkillSourcesConfigPage`
   already works around. Full Cli suite green.

10. **(RUNTIME ACL GAP — FIXED) A channel saved as a name never became runtime-valid after the bot
    joined it.** Follow-on from #8's "save all, flag invalid": an unresolved Slack channel persists as
    a literal *name* in `AllowedChannelIds`, but `SlackAclPolicy.IsAllowedChannel` matches incoming
    messages by channel **ID** (`StringComparer.Ordinal`). So once the bot was added to the channel,
    the stored name stayed inert — the bot silently would not respond there — until the operator
    happened to re-save. The missing `#` on the row (the operator's reported symptom) was the visible
    tell. **Fix (owner decision: normalize on re-open + persist):** when management re-opens and a
    stored name now resolves, `RefreshSlackChannelLabelsAsync` rewrites it to its canonical ID, moves
    its audience, and writes the config (`NormalizeSlackChannelNamesToIds` + shared
    `WriteChannelConfigToDisk`). Slack-only — Discord/Mattermost store canonical IDs already. Guarded
    against spurious writes (already-canonical configs are not rewritten). Two regression tests.

## Pending (logged, batch before push)

8. **(DATA LOSS — FIXED) Unresolved channel names blocked the entire adapter save.** Distinct
   second mechanism from #7. When channel names were entered where some don't resolve (`netclaw-test`,
   `fake-channel` alongside a valid `openclaw`), the sub-flow completion autosave's
   `ValidateSlack/Discord/MattermostChannelsAsync` returned `ChannelAccessOutcome.Blocked`, so
   `SaveAsync` returned false and **nothing** persisted — not the valid channel, not the bot token —
   and Escape discarded the in-memory editor. **Root cause (confirmed via live-binary instrumentation
   after two wrong fixes):** each validator had a `if (!result.Success) return Blocked(...)` guard, but
   the probe sets `Success = (EVERY name resolved)` — i.e. `Success` is false whenever *any* name is
   merely not-found, with `ErrorMessage == null`. So a single unverifiable channel name made `Success`
   false and dropped the whole adapter. (The first hypothesis — a `Unresolved.Count > 0` block — was
   the same symptom via the wrong line.) The unit tests masked it because their **fake probes set
   `Success = true` with a non-empty `Unresolved` list**, which the real probes never do. **Fix (owner
   decision: "save all, flag invalid"):** removed the `!result.Success` guard from all three save-path
   validators — only a genuine probe failure (`ErrorMessage` set: auth/scope/network/timeout) blocks now.
   Unresolved names persist verbatim (inert in the allow-list until the channel exists) with a
   non-blocking warning; rows render red with a `✗` (`ChannelPermissionRow.IsUnresolved` from each
   adapter's `LastChannelResolution.Unresolved`). The `+ Add channel` resolve-before-add path stays
   strict (an explicit single-channel add must resolve). Test fakes corrected to the real
   `Success = (all resolved)` semantics so the invariant tests now actually reproduce the bug; hard
   mixed-valid/invalid-persists-everything invariant + per-adapter probe-failure-blocks tests.
   Full Cli suite 1054 green.

7. **(DATA LOSS — FIXED) Channels save reported "saved" but persisted nothing for an enabled adapter.**
   User configured Slack (by name) + Discord (by id) with **real tokens**, saw green **"…saved"**, but
   `netclaw.json` had no channel sections and `secrets.json` no bot tokens — confirmed via the live
   Termina trace (status showed "saved") + on-disk state. **Root cause:** the save used two different
   "is this adapter enabled?" sources — dynamic validation gated on `Step.IsAdapterEnabled` (the picker
   dict), but `BuildContribution`/`AddSlackContribution` gated on the sub-VM's `SlackEnabled` flag. When
   those disagree, the save validates + probes the adapter as enabled, then the contribution emits only
   `Enabled=false` (dropping `AllowedChannelIds`/audiences) while `session.Save()` runs and "saved"
   still shows — a success-reporting silent half-write. The happy-path fake-probe tests passed because
   the flags stayed synced there. **Fix:** `BuildContribution` now reads the single source of truth
   `step.IsAdapterEnabled(type)` (same as validation) and threads `enabled` into the per-adapter
   contributions; the sub-VM `*Enabled` flags remain reload-sync targets but no longer decide
   persistence. Invariant test added (`Save_true_for_picker_enabled_adapter_persists_section_even_if_child_flag_desyncs`,
   proven load-bearing) + an end-to-end navigation regression mirroring the trace
   (`Channels_EnableSlackByName_thenDiscordById_persistsBothSectionsAndSecrets`). Full suite 1043 green.

5. **`config-*` smoke tape re-validation under raw input** (from finding 1) + the updated
   `config-surfaces` tape — run `./scripts/smoke/run-smoke.sh light` and fix any breakage before push.

6. **(Minor/optional, not a bug) Skill feed shows "0 skills" right after adding it.** Remote feed
   fetching is owned by the daemon (`ServerFeedSkillSyncService`, synced on config hot-reload via
   `ConfigWatcherService`, then every `SyncIntervalMinutes`); the config TUI only re-reads local
   state (`RescanAll → ReloadSources`), and `Rescan all` is correctly scoped to local source status.
   So a freshly-added remote feed reads "0 skills" until the daemon reloads — which looks like
   failure. Consider an editor hint ("Skills sync when the daemon reloads this config") or a
   sync-status line. Verified the add flow + feed are correct (server returned HTTP 200 with a real
   index); the sandbox simply has no `netclawd` running.

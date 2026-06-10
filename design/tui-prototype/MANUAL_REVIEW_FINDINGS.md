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

## Pending (logged, batch before push)

8. **(DATA LOSS — FIXED) Unresolved channel names blocked the entire adapter save.** Distinct
   second mechanism from #7: when channel names were entered where some don't resolve (`netclaw-test`,
   `fake-channel` alongside a valid `openclaw`), the sub-flow completion autosave's
   `ValidateSlack/Discord/MattermostChannelsAsync` returned an `Error` on `Unresolved.Count > 0`, so
   `SaveAsync` returned false and **nothing** persisted — not the valid channel, not the bot token —
   and Escape discarded the in-memory editor. **Fix (owner decision: "save all, flag invalid"):** the
   validation no longer blocks on unresolved channels — it persists the whole adapter (token +
   resolved IDs + unresolved names kept verbatim, inert in the allow-list until the channel exists)
   and surfaces a non-blocking warning. Unresolved rows render red with a `✗` (`ChannelPermissionRow.
   IsUnresolved` from each adapter's `LastChannelResolution.Unresolved`). Genuine probe failures
   (bad token / unreachable) still block. The `+ Add channel` resolve-before-add path stays strict.
   Hard invariant test (mixed valid/invalid persists everything) + per-adapter probe-failure-blocks
   tests added. Full Cli suite 1054 green.

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

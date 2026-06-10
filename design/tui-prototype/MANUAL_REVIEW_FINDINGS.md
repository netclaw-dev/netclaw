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

5. **`config-*` smoke tape re-validation under raw input** (from finding 1) + the updated
   `config-surfaces` tape — run `./scripts/smoke/run-smoke.sh light` and fix any breakage before push.

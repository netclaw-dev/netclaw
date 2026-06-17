## Why

A deep C# implementation review of the `netclaw config` / `netclaw init` TUI
(`docs/reviews/2026-06-config-tui-deep-review.md` — 85 findings; the 32 high/medium
are all confirmed against code) found that the TUI's high-severity bugs cluster into
a few systemic root causes rather than isolated defects: the single-threaded Termina
loop does disk I/O and network probes with no consistent concurrency model
(fire-and-forget tasks that race a save, non-atomic `File.WriteAllText` that can
corrupt `netclaw.json`/`devices.json`, sync-over-async that freezes the input loop),
config parse/read errors throw straight into the event loop (crash or permanent
freeze), and several security-relevant fallbacks silently assume a *permissive*
default — a direct violation of the repo's default-deny posture and the constitution's
"No silent fallbacks" rule. This change hardens those root causes; it is reliability
and security hardening of shipped behavior, not a new feature.

## What Changes

- **Atomic, serialized config persistence.** All config / secrets / device-registry
  writes go through one atomic write seam (temp file + rename) and are serialized so a
  background task and a user save can never write the same file concurrently. Fixes the
  corruption window on `devices.json` and `netclaw.json`.
- **Background-task lifecycle discipline.** Config viewmodels track their background
  probe/label-refresh tasks (no fire-and-forget), and cancel-and-await them before a
  save and on dispose, so a stale probe result can no longer clobber freshly-loaded
  state or persist a stale snapshot.
- **Responsive event loop.** Remove sync-over-async on the UI thread; probes run off
  the loop so the TUI stays responsive.
- **Fail-loud on config parse/read.** Parse/load on render and autosave paths surface a
  status message and stay usable instead of throwing into the event loop (no more
  dashboard-render crashes or a wizard wedged at `IsRunning=true`).
- **Deny-by-default security fallbacks.** An unparseable / unrecognized security-relevant
  value denies (most-restrictive / disabled) and warns — never silently assumes a
  permissive default (posture, server-enabled, plaintext-credential).
- **Targeted correctness/secret fixes.** Audience (ACL trust-tier) changes autosave;
  credentials persist only after a successful probe; unresolved channel names never
  become inert ACL keys; assorted crash/throw edges removed.
- **NOT in scope (deferred):** decomposing the two ~2,300-line god-object viewmodels
  (`ChannelsConfigViewModel`, `SkillSourcesConfigViewModel`) — the design findings flag
  these as the structural enabler of the concurrency bugs, but the refactor is large and
  belongs in its own follow-on change after this hardening lands. The 53 low-severity
  findings (catalogued in the review doc) are likewise deferred for an opportunistic sweep.

## Capabilities

### New Capabilities

- `config-tui-resilience`: invariants for how the config/init TUI persists data and
  handles malformed or security-relevant config — atomic+serialized writes, tracked and
  cancellable background tasks, a responsive loop, fail-loud parsing, deny-by-default
  fallbacks, persist-after-validate for secrets, and no silent loss of an ACL change.

### Modified Capabilities

None — the affected behaviors were never specified as requirements; they are introduced
as new invariants under `config-tui-resilience`.

## Impact

- **Code:** `ConfigEditorSession` / `WizardConfigBuilder` / `ConfigFileHelper` (atomic
  write seam); the device-registry writer in `ExposureModeStepViewModel`; the config
  viewmodels (`ChannelsConfigViewModel`, `SkillSourcesConfigViewModel`,
  `SecurityAccessViewModel`, `BrowserAutomationConfigViewModel`,
  `TelemetryAlertingConfigViewModel`, `ConfigDashboardViewModel`), the manager/step
  viewmodels (`ProviderManagerViewModel`, `ProviderStepViewModel`,
  `HealthCheckStepViewModel`, `SlackStepViewModel`, `DiscordStepViewModel`),
  `McpToolPermissionsViewModel`, and the probe interfaces made truly async.
- **Tests:** new concurrency tests (race/cancellation), fake-failure tests proving the
  bad path is blocked before persistence, and config round-trip tests, per the repo
  Automation Floor; native smoke tapes for any touched TUI surface.
- **Security & operational:** net-positive — removes corruption windows, removes silent
  permissive fallbacks on the default-deny surface, and stops malformed config from
  crashing or wedging the TUI. No intended user-facing behavior change on the happy path.
- **Evidence:** every task cites the file:line in `docs/reviews/2026-06-config-tui-deep-review.md`.

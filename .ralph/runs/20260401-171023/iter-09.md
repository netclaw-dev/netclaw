# Iteration 09 — Flight Recorder

**Run:** 20260401-171023
**Date:** 2026-04-01

---

## Task Selected

**M7.A6: Hot-reload exclusion and spec updates**
(Milestone 7 Phase A — exposure-modes change, OpenSpec tasks 6.1, 8.1, 8.2)

---

## Surface Area Classification

- `Netclaw.Configuration` — `DaemonConfig.ParseExposureMode` visibility change (internal → public)
- `Netclaw.Daemon` — `ConfigWatcherService` logic change (new constructor param + Daemon section detection)
- `Netclaw.Daemon.Tests` — tests added for new behavior
- `docs/spec/SPEC-006` — implementation status added
- `docs/spec/SPEC-011` — hardcoded URL updated to reference `DaemonConfig`

---

## Verification Level

**L1** — build + unit tests only. No I/O coordination or UI changes; pure
business-logic path in `ConfigWatcherService.ApplyReloadAsync`.

---

## Skills Consulted

- `.claude/skills/ralph-loop.md` — process discipline

---

## Design Decisions

`DaemonConfig.ParseExposureMode` was `internal`. Made it `public` to allow
`ConfigWatcherService` (in `Netclaw.Daemon`) to call it when reading the new
config from disk. Alternatives considered and rejected:
- Duplicating the parse switch in `ConfigWatcherService` — violates DRY
- `InternalsVisibleTo("Netclaw.Daemon")` in `Netclaw.Configuration.csproj` —
  inappropriate (InternalsVisibleTo is for test projects, not production deps)
- Building a full `IConfiguration` from JSON — heavyweight for this use case

`ParseExposureMode` is a utility on a public type; no reason to keep internal.

The Daemon section comparison uses `DaemonConfig` record equality (value semantics).
`ReadDaemonConfigFromFile` returns defaults (`new DaemonConfig()`) for missing files
and missing Daemon sections, so a missing Daemon section equals the running defaults —
non-daemon changes still trigger a restart as expected.

---

## Commands Run + Outcomes

```
dotnet build src/Netclaw.Daemon/Netclaw.Daemon.csproj -c Release --no-restore
  → Build succeeded. 0 warnings, 0 errors.

dotnet test src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj -c Release --no-restore --filter "ConfigWatcherService"
  → Passed! Failed: 0, Passed: 13, Skipped: 0, Total: 13

dotnet test src/Netclaw.Configuration.Tests/Netclaw.Configuration.Tests.csproj -c Release --no-restore
  → Passed! Failed: 0, Passed: 129, Skipped: 0, Total: 129

dotnet slopwatch analyze
  → Scan complete: 2 issue(s) found, Warnings: 2
  → Both warnings are pre-existing SW005 in Netclaw.Daemon.csproj and
    Netclaw.Providers.csproj (OPENAI001 NoWarn entries). Not new violations.
    Exit code 0.
```

---

## Deviations / Skips

None. All Done-when criteria addressed.

---

## Follow-ups Noticed But Deferred

- The 2 slopwatch SW005 warnings (OPENAI001 NoWarn in csproj files) are pre-existing
  and not related to this change. No action needed.
- `ConfigWatcherService` currently does not detect `Daemon` section changes when
  the config file is missing (returns defaults, which equals running defaults if
  no custom Daemon config). This is correct behavior — file deletion should still
  trigger a restart for non-daemon config cleanup.

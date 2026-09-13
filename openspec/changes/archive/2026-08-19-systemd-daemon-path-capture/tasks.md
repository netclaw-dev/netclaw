## 1. Paths & environment-file model

- [x] 1.1 Add `DaemonEnvironmentFilePath` to `NetclawPaths` under `ConfigDirectory` (e.g. `config/daemon.env`); confirm no collision with existing config assets.
- [x] 1.2 Add a small helper (in `DaemonManager` or alongside `NetclawPaths`) that renders the env-file content: a single `PATH=<installDir>:<captured operator PATH>` line, prepending the install dir.

## 2. Install: capture instead of guess

- [x] 2.1 In `DaemonManager.InstallAsync`, capture the operator `PATH` via `Environment.GetEnvironmentVariable("PATH")` (no shell spawn) and write the env file to `DaemonEnvironmentFilePath` (creating `ConfigDirectory`).
- [x] 2.2 Change the generated unit to reference the env file via `EnvironmentFile=<abs path>` and REMOVE the inline `Environment=PATH=` directive.
- [x] 2.3 Repurpose/rename `ComposeSystemdUnitPath` to build the captured-PATH value (install dir + captured), or fold it into the render helper from 1.2; keep the install-dir-first ordering.
- [x] 2.4 Update install/upgrade messaging to mention the env file and the required `systemctl --user restart netclaw` after changes.

## 3. Uninstall cleanup

- [x] 3.1 In `DaemonManager.UninstallAsync`, delete `DaemonEnvironmentFilePath` alongside the unit file (idempotent if absent).

## 4. Doctor check rewrite (consumer side of the contract)

- [x] 4.1 Rewrite `SystemdUnitPathDoctorCheck` to: read the unit, find `EnvironmentFile=`, confirm the referenced file exists, and confirm its `PATH` includes the install dir (still derived from `ExecStart`).
- [x] 4.2 Warnings point remediation at `netclaw doctor --fix` (or reinstall) + `systemctl --user restart netclaw`; keep the not-installed / non-Linux pass-through behavior.

## 5. Doctor --fix rehydration

- [x] 5.1 In `DoctorFixService`, add env-file rehydration that runs OUTSIDE the `netclaw.json`-existence early-return (independent of app config).
- [x] 5.2 Emit a `DoctorFileFix` for the env file only when it is missing, unwired, or missing the install dir; capture the current shell `PATH`. Reuse `ApplyAsync` (file write only — no daemon restart).
- [x] 5.3 Ensure the fix plan/description surfaces the `systemctl --user restart netclaw` instruction to the operator.

## 6. Tests

- [x] 6.1 Install-content test: `BuildDaemonUnitContent` has `EnvironmentFile=-` and NO `Environment=PATH=`; `DaemonPathEnvironmentFile.Render` puts install dir first. (Full `InstallAsync` not driven — it runs real `systemctl`/`loginctl` against the live service; the pure builders are exactly what it writes.)
- [x] 6.2 Uninstall env-file removal extracted to `DaemonManager.RemoveDaemonEnvironmentFile()` and unit-tested (deletes the file + idempotent). The seam exists because full `UninstallAsync` runs real `systemctl stop/disable netclaw.service` and would mutate the developer's own service.
- [x] 6.3 Rewrite `SystemdUnitPathDoctorCheckTests`: pass (wired + present + install dir), warn (no `EnvironmentFile=` → reinstall), warn (file absent → doctor --fix), warn (install dir missing from PATH), warn (malformed ExecStart), skip (no unit), skip (non-Linux).
- [x] 6.4 `DoctorFixService` tests: rehydrates when env file missing/stale, INCLUDING when `netclaw.json` is absent; no-op when healthy; no-op when unit legacy/unwired; surfaces the restart instruction; applies to disk.
- [x] 6.5 Producer→consumer contract test: the exact env file + unit the installer builders produce are accepted by `SystemdUnitPathDoctorCheck` (Pass) and reported healthy by the doctor-fix path (no fix).

## 7. Docs & system skill

- [x] 7.1 Update `docs/spec/SPEC-011-daemon-architecture.md` (env-file model; remove hardcoded-PATH description).
- [x] 7.2 Update `docs/prd/PRD-004-cli-onboarding-and-config.md` install/doctor operator flow.
- [x] 7.3 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`: how the shell tool gets its `PATH`, the "re-run install or `doctor --fix` + restart after installing new tools" loop, and the mise/asdf/direnv limitation. Bump `metadata.version`.

## 8. Quality gates

- [x] 8.1 `dotnet slopwatch analyze` — 0 issues found (verified).
- [x] 8.2 `./scripts/Add-FileHeaders.ps1 -Verify` — all files have headers (verified).
- [x] 8.3 Eval suite N/A: change is a diagnostics-table addition + skill version bump, not skill-matching/tool/identity/memory/system-prompt logic (the categories the suite guards); the live-model suite needs a configured model target. No eval-case triggers apply.

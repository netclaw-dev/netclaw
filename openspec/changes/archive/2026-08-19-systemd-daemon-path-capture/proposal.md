## Why

When Netclaw is installed as a systemd `--user` service, the daemon starts with systemd's
sanitized, non-interactive environment — it does **not** inherit the operator's login-shell
`PATH`. To compensate, `netclaw daemon install` today bakes a **hardcoded** `Environment=PATH=`
list into the generated unit file (`installDir:~/.local/bin:/usr/local/bin:/usr/bin:/bin:...`).
That list can never anticipate a real operator's environment: the failure that surfaced this
(GitHub #1544) was `~/.dotnet/dotnet` being invisible to the agent's shell tool during a CI
restore, because `~/.dotnet` is not in the hardcoded list. Guessing at the operator's `PATH` is
structurally wrong — the operator's shell already knows the answer. This change captures the
operator's real `PATH` from the CLI process (which is a child of their shell) instead of
guessing, and keeps it fresh via `netclaw daemon install` and `netclaw doctor --fix`.

Source PRD: **PRD-004** (CLI onboarding and config) — daemon install/doctor operator flows.

## What Changes

- **BREAKING (unit file shape):** `netclaw daemon install` no longer writes a hardcoded
  `Environment=PATH=` directive into `~/.config/systemd/user/netclaw.service`. Existing installs
  keep working until re-run, but the generated unit file shape changes on the next install.
- `netclaw daemon install` captures the operator's **real** `PATH` from the CLI's own inherited
  environment (`Environment.GetEnvironmentVariable("PATH")`) — zero shell execution, no dotfile
  sourcing — and writes it to a netclaw-owned environment file that the unit references via
  `EnvironmentFile=`. The daemon only ever *reads* that file.
- The captured value prepends the daemon's own install directory (so the bundled `netclaw` CLI
  always resolves) ahead of the operator's captured `PATH`.
- `netclaw doctor --fix` **rehydrates** the environment file from the current shell's `PATH` when
  it is missing, unwired, or stale, then instructs the operator to
  `systemctl --user restart netclaw`. The fix writes files only; it does **not** silently restart
  the daemon.
- `SystemdUnitPathDoctorCheck` is rewritten: instead of parsing the unit's `Environment=PATH=`
  line, it validates that the unit references the environment file (`EnvironmentFile=`) and that
  the file exists and contains a `PATH` including the daemon's install directory.
- `netclaw daemon uninstall` removes the netclaw-owned environment file alongside the unit.
- Explicitly **rejected** (documented as a considered alternative in design): having the daemon
  spawn a login shell (`bash -lc`) at startup to derive `PATH`. Running operator dotfiles in an
  unsupervised background service can hang boot and is unpredictable — it violates the repo's
  no-silent-fallback / fail-loud posture.

### In scope (MVP)

- Linux systemd `--user` service install/uninstall path.
- Capture-at-install and rehydrate-on-`doctor --fix`.
- Doctor check + fix, docs, and system-skill guidance updates.

### Out of scope

- Dynamic per-directory toolchain managers (`mise`, `asdf`, `direnv`) that inject `PATH` on `cd`.
  A one-shot capture cannot represent a per-directory `PATH`; documented as a known limitation.
- Deriving `PATH` live at daemon startup (rejected — see above).
- macOS / Windows service install (already unsupported; unchanged).
- Manual `netclaw daemon start` (non-systemd): the daemon is a child of the operator's terminal
  and already inherits the real `PATH`, so no change is needed there.

## Capabilities

### New Capabilities

- `daemon-shell-path`: How the installed systemd `--user` daemon provisions the operator's `PATH`
  for the agent's shell tool — captured (not guessed) from the CLI environment at install and
  rehydrated by `doctor --fix`, delivered via a netclaw-owned `EnvironmentFile=`, and validated by
  the systemd PATH doctor check.

### Modified Capabilities

<!-- No existing spec owns systemd install / shell-tool PATH provisioning. The adjacent daemon
     specs (daemon-bootstrap-pairing, daemon-container, daemon-exposure) cover pairing, container,
     and exposure — none change requirements here. Leaving empty intentionally. -->

## Impact

- **Code**
  - `src/Netclaw.Cli/Daemon/DaemonManager.cs` — `InstallAsync` (write `EnvironmentFile=`, capture
    real `PATH`), `ComposeSystemdUnitPath` (repurpose to build the captured value with install-dir
    prepend), `UninstallAsync` (delete the env file), install/upgrade messaging.
  - `src/Netclaw.Cli/Doctor/SystemdUnitPathDoctorCheck.cs` — validate `EnvironmentFile=` wiring +
    env-file contents instead of the unit's inline PATH.
  - `src/Netclaw.Cli/Doctor/DoctorFixService.cs` — add an env-file rehydration fix. Must run
    **outside** the current `netclaw.json`-existence early-return, since the systemd env file is
    independent of the app config file.
- **Cross-boundary contract** (producer → consumer): `DaemonManager` (producer) writes the env
  file; `SystemdUnitPathDoctorCheck` (consumer) validates it. Both must agree on the file path,
  the `EnvironmentFile=` directive, and that the install directory appears on `PATH`. Tests must
  prove the produced file is exactly what the check accepts.
- **Tests** — `SystemdUnitPathDoctorCheckTests`, `SystemdUserServiceTests`, and `DaemonManager`
  install/uninstall tests (unit content, env-file creation/removal, doctor-fix rehydration incl.
  the no-config-file case).
- **Docs** — `docs/spec/SPEC-011-daemon-architecture.md`, `docs/prd/PRD-004-cli-onboarding-and-config.md`.
- **System skill** (System Skills Sync Rule) — `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  (daemon install / doctor guidance): how the shell tool gets its `PATH`, and the "re-run install
  or `doctor --fix` after installing new tools" refresh loop.
- **Security / operational**
  - Security: **no privilege change.** The shell command policy is `PATH`-independent —
    `ShellCommandPolicy` is a deny-list matching the literal typed verb token, and
    `ApprovalPatternMatching` keys on verb + directory scoping; neither resolves commands against
    `$PATH`. Widening the daemon's `PATH` only affects bare-name *resolution*, which is downstream
    of both gates. It cannot bypass a deny (deny fires on the token regardless of resolution) or
    widen an allow (there is no resolved-path allow-list). This premise is why capturing the
    operator's `PATH` is safe.
  - Operational: env file changes require a service restart to take effect (systemd only re-reads
    `EnvironmentFile=` on unit (re)start). Both install and `doctor --fix` surface the restart
    instruction explicitly rather than restarting the daemon implicitly.

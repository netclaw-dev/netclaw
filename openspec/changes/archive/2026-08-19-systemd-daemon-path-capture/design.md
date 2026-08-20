## Context

A systemd `--user` service starts with a sanitized, non-interactive environment. It does **not**
read `~/.bashrc`/`~/.profile` and does **not** inherit the operator's login-shell `PATH` — by
design, so services are reproducible. Netclaw's agent shell tool (and
`BackgroundJobExecutionActor`) spawn `bash -c` from the daemon, so under the installed service they
see only whatever `PATH` the unit provides.

Today `netclaw daemon install` compensates by baking a **hardcoded** `Environment=PATH=` list into
the unit (`DaemonManager.ComposeSystemdUnitPath`): install dir, `~/.local/bin`, and the standard
system dirs. GitHub #1544 is the failure mode: `~/.dotnet` is not in that list, so the daemon's
shell tool cannot find `dotnet`. Any hardcoded list is a guess that will be wrong for some
operator.

The operator's shell already knows the correct `PATH`. Crucially, so does any `netclaw` CLI process
the operator launches from that shell — it inherits `PATH` as a normal environment variable. This
design captures that inherited value instead of guessing, and never runs operator shell code from
the daemon.

Confirmed premise (from code review of `Netclaw.Security`): the shell command policy is
`PATH`-independent. `ShellCommandPolicy` is a deny-list matching the literal typed verb token
(tokenized, punctuation-trimmed); `ApprovalPatternMatching` keys on verb + directory scoping. No
enforcement path resolves a command against `$PATH`, and there is no resolved-path allow-list.
Therefore widening the daemon's `PATH` changes only bare-name *resolution* (ergonomics), never the
security decision — so capturing the operator's `PATH` is safe.

## Goals / Non-Goals

**Goals:**

- The installed daemon's shell tool resolves the same tools the operator can resolve, without
  hand-maintaining a directory list.
- Never execute operator shell/dotfiles from the daemon (no boot hang, no surprise side effects).
- Keep the value refreshable through commands the operator already runs (`daemon install`,
  `doctor --fix`).
- Keep the producer (`DaemonManager`) and consumer (`SystemdUnitPathDoctorCheck`) in exact
  agreement on the file, wiring, and contents.

**Non-Goals:**

- Live/continuous `PATH` sync. The value is a snapshot as of the last install / `doctor --fix`.
- Capturing dynamic per-directory `PATH` managers (`mise`, `asdf`, `direnv`).
- Changing manual `netclaw daemon start` (non-systemd), which already inherits the operator `PATH`.
- macOS/Windows service install (still unsupported).

## Decisions

### D1: Capture the CLI's inherited PATH — don't guess, don't source a shell

Read `Environment.GetEnvironmentVariable("PATH")` in the CLI process at install / `doctor --fix`
time. The CLI is a child of the operator's interactive shell, so this is the operator's real `PATH`
with **zero** shell execution.

- **vs. hardcoded list (status quo):** guaranteed to miss someone's tools (the #1544 bug). Rejected.
- **vs. daemon spawns `bash -lc` at startup:** would be fresher, but runs operator dotfiles in an
  unsupervised background service — can hang boot, has unpredictable side effects, and to "fail
  loud" on probe failure we'd need a fallback anyway. Rejected; violates the repo's
  no-silent-fallback posture.
- **vs. global `~/.config/environment.d/`:** the systemd-blessed mechanism, but it (a) changes
  `PATH` for **every** user service, not just netclaw, and (b) its `${PATH}` resolves to the
  manager's sanitized default — it would **not** capture `~/.dotnet` without the operator hand-
  listing dirs, so it does not actually solve #1544. Rejected as the primary mechanism.

### D2: Deliver via `EnvironmentFile=` (unit-scoped), not inline `Environment=` or environment.d

The unit references a netclaw-owned file: `EnvironmentFile=<path>` with a single `PATH=...` line.

- Unit-scoped → zero blast radius on other user services (unlike environment.d).
- Separately rewritable → `doctor --fix` can rehydrate `PATH` without rewriting the unit.
- `daemon-reload` + service restart deterministically re-reads it (vs. environment.d's fuzzy
  re-read semantics).
- Clean removal on uninstall.

### D3: Env-file location reuses `NetclawPaths`

`DaemonManager` already receives a `NetclawPaths`. Add a `DaemonEnvironmentFilePath` property under
the existing `ConfigDirectory` (e.g. `<BasePath>/config/daemon.env`) rather than computing a new
ad-hoc path in `DaemonManager`. This follows the repo's "reuse before you add" rule and keeps the
path a single source of truth shared by the producer, the doctor check, and uninstall. The unit
references it by resolved absolute path.

### D4: Install-dir is prepended to the captured PATH

Provisioned value = `installDir : <captured operator PATH> : <system floor>`, de-duplicated with
empty elements dropped. installDir leads (bundled CLI wins), the operator's real dirs follow (the
point of #1544), and a guaranteed floor (`/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin`) is always
appended.

**The floor is not a guess at the operator's tools — it is a functional baseline** (a POSIX shell,
coreutils, admin `sbin` tools) that the old unit-baked PATH provided unconditionally. Code review
caught that dropping it regressed two cases the old code could never hit: an empty/unset capture
would yield `PATH=installDir` alone (silently breaking *every* shell command, and the doctor check
would still pass it), and a normal desktop login PATH omits `/usr/sbin`,`/sbin` (so `ip`/`iptables`
would stop resolving). Appending the floor restores the old guarantee while keeping the captured
operator dirs. **Empty `PATH` elements are dropped** because POSIX resolves an empty element to the
current directory — with the daemon running `bash -c` in an agent-controlled workspace, a captured
`::` (common from `PATH="$PATH:"` in a dotfile) would let a planted binary shadow a system command.

### D5: `doctor --fix` rehydration lives outside the config-file gate

`DoctorFixService.BuildPlanAsync` currently early-returns when `netclaw.json` is absent and only
emits `DoctorFileFix`es against the config JSON. The daemon env file is independent of app config,
so its rehydration fix is evaluated **before/around** that early-return. It reuses the existing
`DoctorFileFix` (path + original + updated text) + `ApplyAsync` file-write model. The fix is emitted
only when the file is missing, unwired, or missing the install dir — avoiding needless churn. The
fix description/plan surfaces the required `systemctl --user restart netclaw`; `ApplyAsync` writes
files only and never restarts the daemon.

### D6: Doctor check validates wiring + file, not an inline PATH line

`SystemdUnitPathDoctorCheck` moves from parsing the unit's `Environment=PATH=` to: (1) unit has
`EnvironmentFile=` → env file; (2) env file exists; (3) env file `PATH` includes the install dir
(install dir still derived from the unit's `ExecStart`). This keeps the check as the enforcement of
the producer/consumer contract, now against the new shape.

### D8: `EnvironmentFile=-` is tolerant of a missing file (surfaced during implementation)

The unit uses the `-` prefix (`EnvironmentFile=-<path>`), so a deleted/missing env file
degrades the daemon's shell-tool PATH to the sanitized systemd default rather than preventing
the entire daemon (Slack, connectors, everything) from starting. The strict alternative
(`EnvironmentFile=<path>`, service fails to start on a missing file) is "louder", but taking the
whole daemon down over a missing PATH helper file is disproportionate — and PATH is *not* a
security boundary here (see the confirmed premise), so a degraded PATH is a functionality gap, not
a privilege issue. The `SystemdUnitPathDoctorCheck` warning + `doctor --fix` make the degraded
state discoverable and repairable, which is the right altitude for a non-security degradation.

### D7: Freshness & restart are explicit, not implicit

`PATH` is current as of the last `install` / `doctor --fix`. Installing a new tool afterward
requires re-running either, then restarting the service. Both commands print the restart
instruction. The doctor **check** nudges when the file is missing/stale, closing the loop.

## Risks / Trade-offs

- **[Snapshot goes stale after installing new tools]** → `doctor --fix` rehydrates in one command;
  the doctor check warns when the install dir is absent from `PATH`. Documented in the
  `netclaw-operations` skill.
- **[`EnvironmentFile=` only re-read on unit (re)start]** → install and `doctor --fix` both surface
  the explicit `systemctl --user restart netclaw` step; no silent restart.
- **[Captured PATH is only as good as the shell that ran the command]** → if the operator installs
  from a minimal shell missing a tool dir, the daemon inherits that gap. Re-run from a normal shell
  or after fixing the shell; `doctor --fix` re-captures. Documented.
- **[Dynamic per-directory PATH managers not captured]** → out of scope; documented limitation.
- **[Backward compat: existing installs still carry inline `Environment=PATH=`]** → they keep
  working; the rewritten doctor check flags them (no `EnvironmentFile=`), and the next
  `daemon install` (or `doctor --fix` + restart) migrates them to the env-file shape and drops the
  inline directive.

## Migration Plan

1. Ship the code. No config-schema change — the env file is not part of `netclaw.json`, so no
   `netclaw-config.v1.schema.json` update and no `SchemaFixResolver` interaction.
2. Existing installed services are unaffected until acted on. On upgrade, the doctor check surfaces
   a warning for the old inline-PATH shape.
3. Operator remediation: `netclaw daemon install` (re-run, idempotent — rewrites unit + writes env
   file) **or** `netclaw doctor --fix`, then `systemctl --user restart netclaw`.
4. Rollback: revert the code. Old and new unit shapes both keep the service runnable; a stray
   `config/daemon.env` left by a newer build is inert to older builds (they ignore it).

## Open Questions

- Env-file basename under `ConfigDirectory`: `daemon.env` proposed — confirm no collision with
  existing config assets in that directory.
- Should the doctor check treat "install dir present but a *previously captured* dir now missing"
  as stale? MVP: only warns when the install dir is absent or the file is missing/unwired, to avoid
  false positives from legitimately changed PATHs.
- Should `daemon install` offer to restart the service for the operator? MVP: instruct only, to
  keep install non-disruptive and consistent with `doctor --fix`.

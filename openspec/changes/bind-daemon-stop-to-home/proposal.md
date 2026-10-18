## Why

A CLI process for one Netclaw home can act on the daemon of another home on the same machine (issue #2527).
The shutdown notice goes to the daemon port, and the daemon logs "stopping" and sends an alert for any
caller. `netclaw daemon stop` sends SIGTERM to the PID in the PID file and never checks which home that
process serves. `Start` does not tell the child daemon which home to serve. Part 1 (#2532) kept the tests
away from the real daemon. This change closes the product defects.

## What Changes

- The shutdown notice carries the PID and the resolved home path of the daemon that the caller means to
  stop. The daemon compares both with its own PID and home. A mismatch gets 409, a Warning log that names
  both homes, no "stopping" log, and no `daemon.stopping` alert. A missing or malformed value gets 400.
- `netclaw daemon stop`, `netclaw update`, and the factory reset send no signal until they have proof that
  the PID serves this home. Without proof they refuse, remove nothing, print a clear error on stderr, and
  exit with code 1.
- The CLI sends the notice to the local control endpoint of this home, not to `NETCLAW_DAEMON_ENDPOINT`.
- `netclaw daemon start` gives the child daemon the home of the CLI in `NETCLAW_HOME`.
- The 200 answer of the daemon carries its resolved `home`. The CLI accepts a 200 only when `pid` and `home`
  match. A 200 without `home` (an older daemon) leaves the decision to the environment of the process (Linux)
  or ends in a refusal.
- The environment proof reads the target process as that process would: `NETCLAW_HOME`, or `<HOME>/.netclaw`
  from the `HOME` of the same environment. A relative value, or a value with `~` or `$`, gives no proof.
- The home comparison resolves links one component at a time, like `realpath(3)`, with a bound of 40 links.
- A refusal prints once on stderr and has a second line with the cause and the recovery. `start` and `status` do
  not report a PID that is not shown to serve this home as the daemon of this home. The factory reset gives the
  same recovery and no longer advises `systemctl --user stop`. The `pkill netclawd` advice goes away.
- The endpoint answers 400 for a `home` with a NUL character or that is not fully qualified, and removes
  control characters from the `home` in the Warning log.
- Two focused mutation gates protect the identity comparison of the endpoint and the proof before the signal.

## Capabilities

### New Capabilities

- `daemon-lifecycle`: how the CLI starts and stops a daemon, and how the daemon accepts a shutdown notice,
  so that a command for one home never stops, alerts, or logs a stop for a daemon of another home.

### Modified Capabilities

(none; no existing spec covers the shutdown notice or the PID file)

## Impact

- `src/Netclaw.Daemon/Lifecycle/LifecycleEndpointRouteBuilderExtensions.cs`: request fields, checks, 400 and 409.
- `src/Netclaw.Cli/Daemon/DaemonManager.cs`: proof before signal, notice target, `NETCLAW_HOME` for the child.
- `src/Netclaw.Cli/Daemon/SystemdUserService.cs`: the home comparison moves to a shared helper.
- `src/Netclaw.Configuration/HomeIdentity.cs`: the link walk and the environment rule.
- `src/Netclaw.Cli/Program.cs`, `src/Netclaw.Cli/Update/UpdateCommand.cs`, `src/Netclaw.Cli/Tui/InitExistingInstallViewModel.cs`:
  one writer for the refusal, the status exit code, the reset advice, and the help text.
- `docs/runbooks/daemon-upgrade.md`, `docs/spec/SPEC-011-daemon-architecture.md`, `docs/spec/GLOSSARY.md`: the recovery and the terms.
- `scripts/run-shutdown-notice-identity-mutations.sh`, `scripts/run-stop-proof-mutations.sh`, `TOOLING.md`, `.github/workflows/pr_validation.yml`.
- The `netclaw-operations` system skill does not change (decided), so no eval run is needed. The skill needs
  the recovery text later. The PR body lists it as a follow-up.
- No config type changes, so the config schema is unchanged. No change to `docs/architecture/tool-authorization.md`:
  the change adds no authorization decision owner, grant scope, candidate kind, or consent surface.
- Compatibility: an old CLI with a new daemon gets 400 for its notice. The old CLI treats the notice as
  best effort, so its SIGTERM stop still works. A new CLI with an old daemon gets 200 with the PID of that
  daemon and no `home`. On Linux the environment of the process decides. On macOS and Windows the CLI refuses,
  and the operator stops the old daemon by hand once (`kill <pid>`).

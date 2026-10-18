## Why

A CLI process for one Netclaw home can act on the daemon of another home on the same machine (issue #2527).
The shutdown notice goes to the daemon port, and the daemon logs "stopping" and sends an alert for any
caller. `netclaw daemon stop` sends SIGTERM to the PID in the PID file and never checks which home that
process serves. `Start` does not tell the child daemon which home to serve. Part 1 (#2532) kept the tests
away from the real daemon. This change closes the product defects.

## What Changes

- The shutdown notice carries the PID and the canonical home path of the daemon that the caller means to
  stop. The daemon compares both with its own PID and home. A mismatch gets 409, a Warning log that names
  both homes, no "stopping" log, and no `daemon.stopping` alert. A missing or malformed value gets 400.
- `netclaw daemon stop`, `netclaw update`, and the factory reset send no signal until they have proof that
  the PID serves this home. Without proof they refuse, remove nothing, print a clear error on stderr, and
  exit with code 1.
- The CLI sends the notice to the local control endpoint of this home, not to `NETCLAW_DAEMON_ENDPOINT`.
- `netclaw daemon start` gives the child daemon the home of the CLI in `NETCLAW_HOME`.
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
- `scripts/run-shutdown-notice-identity-mutations.sh`, `scripts/run-stop-proof-mutations.sh`, `TOOLING.md`, `.github/workflows/pr_validation.yml`.
- The `netclaw-operations` system skill does not change (decided), so no eval run is needed.
- No config type changes, so the config schema is unchanged. No change to `docs/architecture/tool-authorization.md`:
  the change adds no authorization decision owner, grant scope, candidate kind, or consent surface.
- Compatibility: an old CLI with a new daemon gets 400 for its notice. The old CLI treats the notice as
  best effort, so its SIGTERM stop still works. A new CLI with an old daemon gets 200 with the PID of that
  daemon and accepts it only when that PID is the PID in the PID file.

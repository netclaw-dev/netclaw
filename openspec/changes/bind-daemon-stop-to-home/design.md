## Context

See proposal.md for the motivation and `specs/daemon-lifecycle/spec.md` for the rules. Today the endpoint checks
only `reason`. `DaemonManager.StopAsync` reads the PID file, checks that the process name is `netclawd`,
posts the notice, and signals. `SystemdUserService.ProcessServesHome` already reads
`/proc/<pid>/environ`, but it is private and `DaemonManager` does not use it. `DaemonApi.ResolveLocalControlEndpoint`
already returns the endpoint from the `Daemon` config of a home and ignores `NETCLAW_DAEMON_ENDPOINT`.

## Goals / Non-Goals

**Goals:**

- The daemon and the CLI each refuse on their own. Either side alone stops the false alert and the cross-home signal.
- One definition of "same home", shared by both sides.

**Non-Goals:**

- `daemon status` and the PID file service do not change. See Open Questions.
- The endpoint does not stop the daemon. SIGTERM stays the real stop.
- No new token or secret. The loopback Operator identity stays the authorization gate.

## Decisions

### Decision owners and data

| Decision | Owner | Data |
|----------|-------|------|
| The notice is for this daemon | the shutdown endpoint of the daemon | call-local query values; process-local PID and `NetclawPaths.BasePath` |
| The PID serves this home | `DaemonManager.StopAsync` | call-local; reads the durable PID file, stores nothing |
| Which home the child serves | `DaemonManager.Start` | call-local; `NetclawPaths.BasePath` of the manager |

### D1. One shared home comparison

Move `ResolveLinks` and the comparison out of `SystemdUserService` into one helper in `Netclaw.Configuration`
that both `DaemonManager`, `SystemdUserService`, and the endpoint call. It returns a canonical path: full path,
every component with its links resolved, no trailing separator. It compares with case rules of the platform.
Alternative: keep one copy in the CLI and one in the daemon. Rejected: the two would drift, and the CLI sends
a value that the daemon must read the same way. The rule "reuse before you add" applies.

The old helper resolved only the last path component. The shared helper resolves every component, because
macOS keeps `/var` and `/tmp` behind links. A change in behavior for `SystemdUserService` is acceptable: it
only makes two spellings of one folder compare equal.

### D2. The notice is the proof, the environment is the fallback

The daemon is the one party that knows its own PID and home, so its answer is the best proof, and it works on
Linux, macOS, and Windows. Reading `/proc/<pid>/environ` works on Linux only. It stays as a fallback for a
daemon that does not answer (wedged, or not yet bound), so that `daemon stop` can still stop it. It never
overrules a 409.

Alternative: use the environment alone. Rejected: macOS and Windows give no proof. Alternative: use a status
endpoint. Rejected: `/api/health/status` needs a new `home` field and an extra request, and the notice already
carries both values. Alternative: refuse when the daemon does not answer. Rejected: a wedged daemon could not
be stopped by `netclaw daemon stop`, and `ExecStop` of the systemd unit would fail.

### D3. A 200 is proof only with the right PID

A daemon from before this change ignores `pid` and `home` and answers 200 with its own PID. The CLI compares
the PID in the body with the PID from the PID file. A different PID is no proof.

### D4. Response codes

The match keeps the 200 and the `daemon.stopping` alert that exist today. The investigation note proposed 202
and moving the alert to the SIGTERM drain. This change does not do that: it would change the alert flow and the
operator-visible reason. Do it in a separate change if wanted.

### D5. Refusal text on stderr

The refusal goes to stderr with exit code 1. Other daemon failures print on stdout today. The change does not
move those. The implementer can write the refusal with `Console.Error` in `DaemonManager` as the existing
"could not notify" note does, or move all failures of `WriteDaemonResult` to stderr. Tests check stderr for the refusal only.

### D6. Mutation scope

The gate covers the identity block of the endpoint, between the markers `// shutdown-identity:begin` and
`// shutdown-identity:end`. It holds the PID parse and range check, the home check, the PID comparison, and the
home comparison. The gate needs every tested mutant killed and at least 8 mutants in the span. The implementer
sets the exact count in the script when the code exists, as other gates do. Stryker also mutates string literals in the span. Do not write a literal logger category there: inject `ILogger<T>` so that no mutant survives that a test cannot see. The reference run on a prototype killed 18 of 19 mutants. The survivor was such a literal. A second target for the
proof-before-signal decision in `StopAsync` is a follow-up that the owner of the repository can add under the
rule in `TOOLING.md`.

## Risks / Trade-offs

- A daemon that is wedged on a non-Linux host gives no proof, so `daemon stop` refuses → the error text names the PID, and the operator can stop the process by hand.
- A stand-in PID reused by another process between the read and the signal → the check narrows this window but cannot close it; the PID file model has it today.
- The helper change in `SystemdUserService` could alter the unit ownership result for a home path with a link in a parent folder → the existing wiring tests for symlinked homes cover the final component; add none, run them.

## Open Questions

- `daemon status` and `GetStatus` still report "running (PID N)" when the PID file of home B holds the PID of the daemon of home A. A later change can apply the same proof there.

## Context

See proposal.md for the motivation and `specs/daemon-lifecycle/spec.md` for the rules. Today the endpoint checks
only `reason`. `DaemonManager.StopAsync` reads the PID file, checks that the process name is `netclawd`,
posts the notice, and signals. `SystemdUserService.ProcessServesHome` already reads
`/proc/<pid>/environ`, but it is private and `DaemonManager` does not use it. `DaemonApi.ResolveLocalControlEndpoint`
already returns the endpoint from the `Daemon` config of a home and ignores `NETCLAW_DAEMON_ENDPOINT`.

Review round 2 (PR #2544, head `cc55a4693`) found one blocker and five defects in the first version. D2, D3,
and D6 changed, and D7 to D12 are new. The summary:

- The environment proof read `NETCLAW_HOME` with the `HOME` and the working folder of the CLI, not those of the
  target process (D8).
- The home comparison could join two different folders when a link target held `..` (D9).
- A 200 from an older daemon proved a PID but not a home (D3).
- The refusal printed twice and gave the operator no way to recover (D11).
- `start` and `status` said "running" for a PID that is not shown to serve the home (D12).

## Goals / Non-Goals

**Goals:**

- The daemon and the CLI each refuse on their own. Either side alone stops the false alert and the cross-home signal.
- One definition of "same home", shared by both sides.
- An operator who gets a refusal can see why, and can recover with the text on the screen.

**Non-Goals:**

- The PID file service does not change.
- The endpoint does not stop the daemon. SIGTERM stays the real stop.
- No new token or secret. The loopback Operator identity stays the authorization gate.
- No defense against a hostile caller that holds the token (see D7).

## Decisions

### Decision owners and data

| Decision | Owner | Data |
|----------|-------|------|
| The notice is for this daemon | the shutdown endpoint of the daemon | call-local query values; process-local PID and `NetclawPaths.BasePath` |
| The PID serves this home | `DaemonManager.StopAsync` | call-local; reads the durable PID file, stores nothing |
| Which home the child serves | `DaemonManager.Start` | call-local; `NetclawPaths.BasePath` of the manager |
| Which home a target process serves | `HomeIdentity.EnvironmentServesHome` | call-local; the text of `/proc/<pid>/environ` |
| Whether two paths are one home | `HomeIdentity.Canonical` and `Same` | call-local; reads links, stores nothing |
| Whether `start` and `status` may claim a daemon | `DaemonManager.GetStatus` and `Start` | call-local; PID file, environment, lock file |

### D1. One shared home comparison

Move `ResolveLinks` and the comparison out of `SystemdUserService` into one helper in `Netclaw.Configuration`
that both `DaemonManager`, `SystemdUserService`, and the endpoint call. It returns a canonical path: full path,
every component with its links resolved one at a time (D9), no trailing separator. It compares with case rules of the platform.
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

### D3. A 200 is proof only with the right PID and the right home

A daemon from before this change ignores `pid` and `home` and answers 200 with its own PID. The first version
of this change accepted a 200 whose PID matched the PID file. That proves the PID but not the home: a new CLI
with an old daemon on a shared port, and a stale PID file that holds the PID of that daemon, would send
SIGTERM to a daemon of another home. The mixed-version window is the `update` flow, so it happens.

The new daemon echoes its resolved home path as `home` in the 200 body. The CLI accepts a 200 only when `pid` and
`home` both match. A 200 with no `home` (an older daemon) is Unconfirmed, as a missing answer is: on Linux the
environment of the process decides (D8). On macOS and Windows an Unconfirmed answer ends in a refusal, so an
`update` from an old daemon to a new one needs the operator to stop the old daemon by hand (the `kill <pid>`
advice of D11). This costs one manual step during the mixed-version window on those platforms. The cost is
accepted because the alternative is a signal to the wrong daemon.

A 200 whose body is not a JSON object, whose `pid` is not a whole JSON number, or whose `home` is not a string
is Refused. The CLI reads the body with `JsonElement.ValueKind` checks before it reads a number, because
`TryGetInt32` throws for a string.

### D4. Response codes

The match keeps the 200 and the `daemon.stopping` alert that exist today. The investigation note proposed 202
and moving the alert to the SIGTERM drain. The owner decided to keep both in this change and to file the move
as a follow-up.

### D5. Refusal text on stderr, once

The refusal goes to stderr with exit code 1. Other daemon failures print on stdout today, and this change does
not move them (decided). Round 1 wrote the refusal twice: `StopAsync` wrote it on stderr, and
`WriteDaemonResult` wrote the same message again on stdout. The refusal now has one writer. The result carries a
flag that marks a refusal. `DaemonManager.StopAsync` writes nothing for it. The `daemon stop` and `daemon start`
commands write the message on stderr, and `WriteDaemonResult` skips stdout for a refusal. The update flow and
the factory reset show `DaemonResult.Message` in their own output, so they show it once. The flag must pass
through `UpdateDaemonStopResult.ToDaemonResult`. Tests check the process output: stderr holds the first line
once, and stdout does not hold it.

### D6. Mutation scope

Two gates protect the two decisions that gate an effect.

**`shutdown-notice-identity`** covers the identity block of the endpoint, between the markers
`// shutdown-identity:begin` and `// shutdown-identity:end`. It holds the PID parse and range check, the home
check, the PID comparison, and the home comparison. Every tested mutant must die, and the span must hold at
least the number of mutants in `scripts/run-shutdown-notice-identity-mutations.sh` (18 before round 2; the
script and `TOOLING.md` hold the current number, and the three must match). Stryker also mutates string literals in the span. Do not write a literal logger category there:
inject `ILogger<T>`. A reference run on a prototype killed 18 of 19 mutants. The survivor was such a literal.

**`stop-proof`** covers the proof in `DaemonManager.StopAsync`, because the proof gates SIGTERM to a process.
It runs Stryker twice: on `DaemonManager.cs` (spans `stale-pid`, `stop-notice`, `stop-proof`) and on
`HomeIdentity.cs` (span `home-match`). The tests are `StopProofMutationTests`. They kill mutants in the
PID equality, the 200 versus other status, the environment home match, the rule that a 409 is never overruled,
and the stale-PID handling. A span must not hold an `OperatingSystem` check: the mutant that removes it is
equal to the original on the Linux runner, so no test can kill it. `ProcessServesHome` returns `false` for an
unreadable environment instead.

Round 2 adds code to the `home-match` span: the walk of D9, the environment rule of D8, and the parse of the
`HOME` entry. It adds code to the `stop-notice` span: the checks of the 200 body (D3). The tests that kill the
new mutants are `HomeIdentityMutationTests` and the added tests of `StopProofMutationTests`. The implementer
runs both gates, sets the exact minimum counts in the scripts and in `TOOLING.md`, and adds no test: when a
mutant survives, the test author adds the test. The status and start proof of D12 sits outside every span,
because it sends no signal.

The comparison of the 40-link limit needs a test at the edge (40 links resolve, 41 fail). A mutant that moves the
limit by one must die.

### D7. The check guards against accidents, not against a hostile caller

The 409 body names the PID and the home of the daemon, so any caller that holds the token can learn both and
send them again. This is acceptable. The check exists for the accidents of #2527: a stale PID file, a port that
two homes share, a test that reaches a real daemon. The token is the security boundary. A caller with the
token can already stop the daemon through other endpoints. We keep the PID and home in the 409 body because
the operator needs them to see which daemon answered. Alternative: keep them in the Warning log only. Rejected:
the CLI shows the body when the answer is a refusal, and the operator may not read the daemon log.

### D8. The environment proof reads the target as the target would

`/proc/<pid>/environ` is the environment of the target process. The first version read `NETCLAW_HOME` from it
but resolved the rest in the context of the CLI: an absent value became the default home of the CLI (from the
`HOME` of the CLI), and a relative value became a path under the working folder of the CLI. The installed
systemd unit sets no `NETCLAW_HOME`, so for the production daemon the "proof" was "the home of the CLI is the
default home of the CLI". It proved nothing.

The rule now follows `NetclawPaths` in the context of the target:

- Absent, empty, or white-space `NETCLAW_HOME`: the home is `<HOME>/.netclaw`, with `HOME` from the same
  environment. No usable `HOME` (missing, empty, or not fully qualified) gives no proof.
- A value that is not fully qualified, or that holds `~` or `$`, gives no proof. The daemon froze a relative value
  against a working folder that it later left, and the CLI cannot read that folder. The CLI does not expand
  `~` or `$HOME`, because a second expander would drift from `PathExpansion`. A home path that holds a literal
  `~` or `$` gets a false refusal. This is safe, and D11 gives the operator a recovery.
- The result is a refusal for any doubt. A false refusal costs the operator one `kill`. A false proof stops the
  wrong daemon.

Alternative: guess the working folder of the target from `/proc/<pid>/cwd`. Rejected: the daemon changes its
folder after startup, so the link shows the wrong folder. Follow-up (not in this change): write
`Environment=NETCLAW_HOME=<home>` into the generated systemd unit, so that the production daemon declares its
home as a daemon from `daemon start` does.

`SystemdUserService.ProcessServesHome` uses the same function, so the fix covers the unit ownership check. A
unit process with a relative `NETCLAW_HOME` is then not shown to serve any home, and the stop goes to the
process path, which refuses without proof.

### D9. The home walk resolves one link at a time

`FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` joins a relative link target to the link path as the
text spells it. A `..` in the target then collapses against that text. When a parent of the text is a link, the
collapse leaves the wrong folder. The review showed a layout where `Canonical(a/link)` named `y` and the real
folder was `q/y`. The first version claimed that it never made two folders compare as one. That claim was false.

The walk of the first requirement fixes it as `realpath(3)` does. It reads one link at a time
(`LinkTarget`, not the final target), joins a relative target to the resolved folder, and goes on with the rest of
the path. A hop limit of 40 (the limit of the Linux kernel) bounds a cycle. A cycle makes `Canonical` throw
`IOException` and makes `Same` return `false`. The endpoint then answers 409, and the CLI treats the path as
no proof. A `..` in the text of the input stays lexical. `NetclawPaths` collapses it with `GetFullPath` before
any code reads the folder, so the daemon and the CLI agree.

Alternative: call `realpath` through P/Invoke on Unix. Rejected: it adds a platform branch that the Linux
mutation gate cannot cover, and Windows needs its own code.

### D10. The notice rejects a malformed home as a client error

`Path.IsPathRooted` accepts `\x` and `C:x` on Windows. Those paths are relative to a drive or a folder. The
endpoint uses `Path.IsPathFullyQualified`. A NUL character in `home` made `GetFullPath` throw and the endpoint
answer 500. The endpoint now rejects a NUL with 400 before any file system call. The Linux tests cover the NUL
case. The Windows forms are in the test data and pass on Linux too (a backslash is a file name character there),
so only a Windows run proves the `IsPathFullyQualified` choice.

### D11. A refusal tells the operator what to do

A refusal that says only "not shown to serve" leaves the operator with one way out, and no document names it.
Every refusal has a second line with the cause and the recovery:

- Cause: the daemon answered for another daemon or home (Refused), or it did not confirm the PID (all other
  cases). The answer decides which text applies.
- Recovery 1: if no Netclaw daemon of this home runs, delete `<home>/netclaw.pid`. The PID file is stale for this
  home, and the next command works.
- Recovery 2: if the PID is a daemon of this home (a wedged daemon on macOS or Windows, where the stop has no
  proof), the operator checks the process and stops it with `kill <pid>` or Task Manager. The CLI does not send
  that signal.

The same text goes into the CLI help of `netclaw daemon` and the daemon runbook. The factory reset shows both
lines and does not advise `systemctl --user stop netclaw.service` for a refusal, because that advice stops the
default unit, and the PID may belong to another home. The `netclaw-operations` skill needs the same text. The
skill does not change in this PR (decided). The PR body lists the skill text as a follow-up.

The pre-existing advice `pkill netclawd` for a held lock with no PID file stops the daemon of every home. The
message now names the lock file of this home and tells the operator to find the process that holds it and to
use `kill <pid>`.

### D12. Start and status need a proof that sends nothing

`netclaw daemon status` and `start` said "running" and "already running" for any `netclawd` PID in the PID file.
They cannot send the notice, because a notice has an effect on the daemon (the log line and the alert). They use
a proof with no effect:

1. Linux: the environment rule of D8. It works for a daemon that does not answer.
2. Where the environment cannot be read (macOS, Windows, no permission): the lock file of this home is held. A
   daemon of this home holds that lock for its whole life, and a PID file of home B that names the daemon of
   home A does not make home B hold a lock.

The lock gives weaker proof than the environment: it shows that some daemon runs for this home, not that the
PID is that daemon. The case that it misses needs a live daemon for this home with a PID file that names
another PID. A daemon rewrites the PID file at startup, so this does not happen without hand edits. The stop
uses the strong proof (the notice) and is not weakened.

`status` exits with code 1 and keeps `DaemonStatus.IsRunning` true. The update flow and the factory reset test
`IsRunning` to decide whether to stop. They must go on to the stop, which refuses, so that a PID file that
names a live `netclawd` blocks a reset instead of letting it delete a home under a daemon that may serve it.
`DaemonStatus` gets a new member that says the PID is not shown to serve this home; `Program.cs` reads it for the
exit code.

Alternative: report `IsRunning` false for such a PID. Rejected: the reset and the update would then go on,
and a daemon that is not shown to serve the home would not block them. That is the old behavior that this change
removes for stop.

## Risks / Trade-offs

- A daemon that is wedged on a non-Linux host gives no proof, so `daemon stop` refuses → the error text names the PID and gives `kill <pid>` (D11).
- A daemon from before this change on macOS or Windows gives an Unconfirmed 200, so `update` from that daemon needs a manual stop once (D3).
- A home path with a literal `~` or `$`, or a relative `NETCLAW_HOME` in the daemon, gives a false refusal for a stop that the operator wants (D8) → the recovery text applies.
- The walk of D9 reads one link per step, so a path with many links costs more file system calls than before → the home path is short, and the call runs once per command.
- A stand-in PID reused by another process between the read and the signal → the check narrows this window but cannot close it; the PID file model has it today.
- The helper change in `SystemdUserService` could alter the unit ownership result for a home path with a link in a parent folder → the existing wiring tests for symlinked homes cover the final component; add none, run them.

## Open Questions

- Should the generated systemd unit write `Environment=NETCLAW_HOME=<home>`? It would make the proof of D8 direct for the production daemon. This is a follow-up issue.
- Should the lock file replace the PID file as the cross-platform signal for `status` and `stop`? D12 uses it only for `status` and `start`, and only where the environment is unreadable.
- The `netclaw-operations` skill needs the recovery text of D11 (a follow-up issue; the PR body names it).

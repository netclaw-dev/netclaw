## Purpose

Defines how the CLI starts, stops, and reports on the daemon of one Netclaw home, and how a daemon accepts a
shutdown notice. A command for one home never stops, alerts, or logs a stop for the daemon of another home. A
command for one home never says that a daemon of another home runs for it.

Cross-cutting terms: see the [engineering glossary](../../../../docs/spec/GLOSSARY.md). This spec uses
"resolved home path" as the glossary defines it.

## ADDED Requirements

### Requirement: One definition of the same home

The daemon and the CLI SHALL compare two home paths through one rule. The rule first makes each path fully
qualified and removes a trailing separator and the dot segments of the text, as `NetclawPaths` does. It then
resolves the links of the path one component at a time, like `realpath(3)`. The result is the resolved home path.

The rules for the walk:

- Each component is checked in the folder that the walk has already resolved. That folder holds no link.
- A relative link target is joined to that resolved folder. A `..` in the target then leaves that resolved
  folder, not a folder that the text of the path named.
- An absolute link target restarts the walk from its root.
- A `.` segment in a link target is skipped. A `..` at the root stays at the root, as `realpath(3)` does.
- The walk follows at most 40 links. The 41st link is an error: the path has no resolved home path. The
  resolution throws `IOException` with a message that holds `40 links`. The comparison then says the two paths
  differ.
- A component that does not exist keeps its spelling.
- Two paths name the same home when their resolved home paths are equal. The comparison is case-sensitive,
  except on Windows. A prefix is not a match.

Two different folders SHALL NOT compare as the same home.

Ordered flow (schematic; link reads only):

```text
resolve(path):
  rest = parts(GetFullPath(path)); done = root
  while rest is not empty:
    part = rest.pop_front()
    if done/part is a link:
      hops += 1; if hops > 40 -> error "too many links"
      target = read_link(done/part)
      if target is absolute: done = root(target)
      rest = parts(target) + rest        # ".." in rest then leaves "done", the resolved folder
    else if part == "..": done = parent(done)
    else: done = done/part
  return done
```

#### Scenario: A link in a parent folder resolves to its target

- **GIVEN** `/t/private/var/home` is a folder and `/t/var` is a link to `private/var`
- **WHEN** the CLI compares `/t/var/home` with `/t/private/var/home`
- **THEN** the paths name the same home

#### Scenario: A dot segment in a link target leaves the resolved folder

- **GIVEN** `b` is a link to `q/r`, `a` is a link to `b/c`, and `q/r/c/link` is a link to `../../y`
- **AND** `q/y` and `y` are two different folders
- **WHEN** the CLI compares `a/link` with `q/y`
- **THEN** the paths name the same home
- **AND** `a/link` and `y` name different homes

#### Scenario: A link cycle gives no match

- **GIVEN** `a` is a link to `b` and `b` is a link to `a`
- **WHEN** the CLI compares `a` with `a`
- **THEN** the paths name different homes, and the resolution reports an error that names the limit of 40 links

### Requirement: The shutdown notice names the daemon that it is for

`POST /api/lifecycle/shutdown` SHALL carry three query values: `reason`, `pid`, and `home`. `pid` SHALL be
the PID that the caller read from the PID file of its home. `home` SHALL be the resolved home path of that home.

The daemon SHALL compare `pid` with its own PID and `home` with its own home through the rule of the first
requirement.

When both match, the daemon SHALL answer 200. The body SHALL hold `reason`, `pid` (its own PID), and `home`
(its own resolved home path). The daemon SHALL write the "Netclaw daemon stopping" log line and send the
`daemon.stopping` alert, as it did before this change.

When `pid` or `home` does not match, or the daemon cannot resolve `home`, the daemon SHALL answer 409. The body
SHALL name the PID and home of the notice and the PID and home of the daemon. The daemon SHALL write one
Warning log line that names both homes. It SHALL NOT write a "stopping" log line and SHALL NOT send a
`daemon.stopping` alert. The daemon SHALL remove every control character and every line separator from the
`home` of the notice before it writes the Warning, and SHALL cut the value to 200 characters.

When `pid` or `home` is missing, empty, or malformed, the daemon SHALL answer 400. A `pid` is malformed when
it is not a whole number above zero. A `home` is malformed when it holds a NUL character or is not a fully
qualified path. On Windows, `\x` and `C:x` are not fully qualified. The body SHALL name `pid` and `home`. The
daemon SHALL NOT treat a missing value as a match. It SHALL NOT write a "stopping" log line and SHALL NOT send an
alert.

When `reason` is missing, the daemon SHALL answer 400 and name `reason`. A caller that is not authorized
SHALL get 401, as before.

The daemon owns this decision. The data is call-local (the query values) and process-local (the PID and
the home of the daemon). Nothing is persisted.

This check guards against an accident, such as a stale PID file or a shared port. It does not guard against a
hostile caller that holds the token. Such a caller can read the PID and home of the daemon in the 409 body and
send them again. The token is the security boundary. See the design, decision D7.

Ordered flow (schematic; it omits the authorization gate):

```text
daemon receives POST ?reason=R&pid=P&home=H
  R missing                                  -> 400
  P not a whole number > 0, H has NUL, H not fully qualified -> 400
  P == own PID and same_home(H, own home)
      yes -> log "stopping", send alert, 200 {reason, pid=own PID, home=own resolved home}
      no  -> log Warning (both homes, H cleaned), 409 {error}
```

#### Scenario: A notice for this daemon is announced

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `?reason=cli-stop&pid=4100&home=/home/op/.netclaw`
- **THEN** the daemon answers 200 with PID 4100 and `home` `/home/op/.netclaw`
- **AND** the log has one "Netclaw daemon stopping" line
- **AND** one `daemon.stopping` alert is sent

#### Scenario: The answer names the resolved home path

- **GIVEN** a daemon that serves the home `/srv/link-to-home`, a link to `/srv/real-home`
- **WHEN** a caller sends a notice for that daemon
- **THEN** the 200 body has `home` `/srv/real-home`

#### Scenario: Another spelling of the same home matches

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends the home as `/home/op/.netclaw/` or as a link that resolves to that folder
- **THEN** the daemon answers 200

#### Scenario: A notice for another PID is refused

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `?reason=cli-stop&pid=4101&home=/home/op/.netclaw`
- **THEN** the daemon answers 409
- **AND** the log has one Warning line and no "stopping" line
- **AND** no alert is sent

#### Scenario: A notice for another home is refused

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `?reason=cli-stop&pid=4100&home=/tmp/x`
- **THEN** the daemon answers 409
- **AND** the Warning line names `/tmp/x` and `/home/op/.netclaw`
- **AND** no "stopping" line is written and no alert is sent

#### Scenario: A home that does not exist does not match

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `home=/no/such/home`
- **THEN** the daemon answers 409, not a server error

#### Scenario: A home with a link cycle is refused

- **GIVEN** a daemon with PID 4100 and a link `loop` that points to itself
- **WHEN** a caller sends `home=<folder>/loop`
- **THEN** the daemon answers 409, not a server error

#### Scenario: A folder inside the home is not the home

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `home=/home/op/.netclaw/workspaces`
- **THEN** the daemon answers 409

#### Scenario: A line break in the home does not reach the log

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `home=/x` followed by a line feed and `fake log line`
- **THEN** the daemon answers 409
- **AND** the one Warning line holds no line feed, no carriage return, and no U+2028 or U+2029
- **AND** no other control character remains in that line

#### Scenario: A notice without identity is refused

- **WHEN** a caller sends `?reason=cli-stop` with no `pid` and no `home`
- **THEN** the daemon answers 400
- **AND** no "stopping" line is written and no alert is sent

#### Scenario: A malformed value is refused

- **WHEN** a caller sends `pid=abc`, `pid=0`, `pid=-1`, an empty `home`, `home=relative/path`, or a `home` with a
  NUL character
- **THEN** the daemon answers 400, not a server error
- **AND** no "stopping" line is written and no alert is sent

### Requirement: A stop needs proof that the PID serves this home

`netclaw daemon stop`, `netclaw update`, and the factory reset SHALL send no signal to the PID in the PID
file until they have proof that the PID serves the home of the command. The CLI SHALL use the home from
`NETCLAW_HOME` or its default, resolved as the daemon resolves it.

The CLI SHALL send the shutdown notice of the second requirement to the local control endpoint of this home.
That endpoint comes from the `Daemon` config of this home. The CLI SHALL NOT use `NETCLAW_DAEMON_ENDPOINT`
or the client config for the notice, because they can name a remote daemon, and the signal goes to a local
PID. The notice carries the PID from the PID file and the resolved home path of the CLI.

The CLI SHALL read the answer to the notice as follows:

| Answer | Result |
|--------|--------|
| 409 | Refused |
| 200, and the body is not a JSON object | Refused |
| 200, and `pid` is missing, is not a JSON number, or is not a whole number | Refused |
| 200, and `pid` differs from the PID of the notice | Refused |
| 200, `pid` matches, and `home` is present but is not a string | Refused |
| 200, `pid` matches, and `home` is a string that is empty, holds a NUL character, or is not fully qualified | Refused |
| 200, `pid` matches, and `home` is a string for another home | Refused |
| 200, `pid` matches, and `home` names this home | Accepted: this is proof |
| 200, `pid` matches, and `home` is missing (a daemon from before this change) | Unconfirmed: the operating system decides |
| no connection, a timeout, or a status other than 200 or 409 | Unconfirmed: the operating system decides |

A Refused answer SHALL give no proof and SHALL NOT be overruled by the environment of the process. An
Unconfirmed answer SHALL give proof only when the operating system shows that the process serves this home.
On Linux, the operating system shows it through the environment of the process, by the next rule. A platform
that cannot show it gives no proof, so an Unconfirmed answer ends in a refusal there.

The environment rule reads the home that the target process would resolve, from the environment of that
process (`/proc/<pid>/environ`). It never uses the `HOME` or the working folder of the CLI.

- When `NETCLAW_HOME` is absent, empty, or only white space, the process serves `<HOME>/.netclaw`. `HOME` is the
  `HOME=` entry of the same environment. When that entry is missing, empty, not fully qualified, or holds a `%`,
  the result is no proof.
- When `NETCLAW_HOME` has a value, the CLI removes surrounding white space. The value SHALL be fully qualified.
  A relative value gives no proof, because the daemon froze it against a working folder that the CLI cannot
  know. A value that holds a `~`, a `$`, or a `%` gives no proof, because `NetclawPaths` expands those tokens
  (`~`, `$HOME`, `${HOME}`, and `%USERPROFILE%`) and the CLI does not.
- When the environment holds a name twice, the first entry counts. The .NET runtime of the daemon reads the first
  entry too.
- The CLI then compares the home it found with the home of the command through the rule of the first
  requirement.

When the CLI has no proof, it SHALL send no signal and SHALL NOT remove the PID file or the lock file. It SHALL
write the refusal on stderr once, return a failed result, and exit with code 1. The refusal has two lines.
The first line is `Nothing was stopped. PID <pid> is not shown to serve <home>.` The second line names the
cause and the recovery, as the next requirement says. No caller of the stop writes the refusal a second time. The
stop writes nothing else on stdout for the refusal.

When the daemon does not answer because of a connection error or a timeout, the CLI SHALL write
`Note: could not notify daemon of shutdown reason: <detail>` on stderr, as it did before this change.

The CLI owns this decision. The data is call-local: the PID file is durable but the CLI only reads it, and
the proof is not stored.

Ordered flow (schematic; it omits the supervisor and systemd unit steps):

```text
StopAsync(home)
  pid = read <home>/netclaw.pid
  no process with pid, or process is not netclawd -> unchanged (see the stale PID requirement)
  answer = POST <home local endpoint>/api/lifecycle/shutdown?reason&pid&home=resolve(home)
  409, 200 with a bad body, 200 with another pid or home   -> refuse
  200 with this pid and this home                          -> proven
  200 with this pid and no home, or no answer, or other status:
      Linux and environ(pid) names this home               -> proven
      otherwise                                            -> refuse
  proven -> SIGTERM pid (Windows: Kill), wait, remove PID file
```

#### Scenario: A stale PID file of another home does not stop its daemon

- **GIVEN** home A has a running daemon with PID 4100
- **AND** the PID file of home B holds 4100
- **WHEN** an operator runs `NETCLAW_HOME=B netclaw daemon stop`
- **THEN** the CLI sends no signal to PID 4100
- **AND** the daemon of home A keeps running and gets no accepted notice
- **AND** the PID file of home B remains
- **AND** stderr has `Nothing was stopped. PID 4100 is not shown to serve B.` once, and stdout does not have it
- **AND** the exit code is 1

#### Scenario: The port of this home belongs to a daemon of another home

- **GIVEN** the `Daemon.Port` of home B is the port of the daemon of home A, and the PID file of B holds the PID of that daemon
- **WHEN** an operator runs `netclaw daemon stop` for home B
- **THEN** the daemon of home A answers 409 and keeps running
- **AND** the CLI sends no signal and exits with code 1

#### Scenario: A daemon from before this change answers for another PID

- **GIVEN** the daemon at the port of home B answers 200 with its own PID 900
- **AND** the PID file of home B holds 4100
- **WHEN** the CLI stops home B
- **THEN** the CLI sends no signal to PID 4100

#### Scenario: A 200 that names another home is no proof

- **GIVEN** the daemon at the port of home B answers 200 with the PID 4100 and `home` A
- **AND** the environment of PID 4100 names home B
- **WHEN** the CLI stops home B
- **THEN** the CLI sends no signal to PID 4100

#### Scenario: A 200 with a PID that is not a number is no proof

- **GIVEN** the daemon at the port of home B answers 200 with the body `{"pid":"4100"}`
- **AND** the environment of PID 4100 names home B
- **WHEN** the CLI stops home B
- **THEN** the CLI sends no signal to PID 4100 and does not fail with an unhandled error

#### Scenario: A daemon of this home proves itself

- **GIVEN** home A has a running daemon with PID 4100
- **WHEN** an operator runs `netclaw daemon stop` for home A
- **THEN** the notice carries `pid=4100` and the resolved home path of home A
- **AND** the daemon answers 200 with PID 4100 and the home
- **AND** the CLI sends SIGTERM to PID 4100 and the daemon stops

#### Scenario: A daemon from before this change answers 200 and the environment decides

- **GIVEN** the daemon at the port of home A answers 200 with PID 4100 and no `home`
- **AND** the environment of PID 4100 names home A
- **WHEN** an operator runs `netclaw daemon stop` for home A on Linux
- **THEN** the CLI sends SIGTERM to PID 4100
- **GIVEN** instead that the environment of PID 4100 names home B
- **THEN** the CLI sends no signal

#### Scenario: A daemon that does not answer still stops when its environment names this home

- **GIVEN** the daemon of home A holds PID 4100, its environment has `NETCLAW_HOME` for home A, and its port does not answer
- **WHEN** an operator runs `netclaw daemon stop` for home A on Linux
- **THEN** the CLI sends SIGTERM to PID 4100

#### Scenario: A daemon that does not answer and serves another home is not stopped

- **GIVEN** the PID file of home B holds 4100, the environment of PID 4100 names home A, and the port of B does not answer
- **WHEN** the CLI stops home B
- **THEN** the CLI sends no signal

#### Scenario: A process with no NETCLAW_HOME serves the HOME of its own environment

- **GIVEN** the daemon of the default home runs with `HOME=/home/op`, no `NETCLAW_HOME`, and no answer from its port
- **AND** the PID file of the home `/home/op/.netclaw` holds its PID
- **WHEN** a CLI with `HOME=/home/op` stops `/home/op/.netclaw`
- **THEN** the CLI sends SIGTERM
- **GIVEN** instead a CLI with `HOME=/srv/h` and `NETCLAW_HOME` absent, whose default home `/srv/h/.netclaw` holds the same PID file
- **THEN** the CLI sends no signal, because the process serves `/home/op/.netclaw`

#### Scenario: A process with no NETCLAW_HOME and no HOME gives no proof

- **GIVEN** the environment of PID 4100 has neither `NETCLAW_HOME` nor `HOME`, and its port does not answer
- **WHEN** the CLI stops the default home of the CLI
- **THEN** the CLI sends no signal

#### Scenario: A relative NETCLAW_HOME gives no proof

- **GIVEN** the daemon was started as `NETCLAW_HOME=.netclaw-dev netclawd` in the folder `/proj/A`
- **AND** the PID file of `/proj/B/.netclaw-dev` holds its PID, and the port of that home does not answer
- **WHEN** an operator in `/proj/B` runs `NETCLAW_HOME=.netclaw-dev netclaw daemon stop`
- **THEN** the CLI sends no signal
- **AND** the same holds when the folders of the CLI and the daemon are the same, because the CLI cannot know the working folder of the daemon

#### Scenario: A NETCLAW_HOME with a home token gives no proof

- **GIVEN** the environment of PID 4100 has `NETCLAW_HOME=~/.netclaw` and `HOME=/home/op`, and its port does not answer
- **WHEN** the CLI stops `/home/op/.netclaw`
- **THEN** the CLI sends no signal

#### Scenario: A NETCLAW_HOME or HOME with a percent sign gives no proof

- **GIVEN** the environment of PID 4100 has `NETCLAW_HOME=/srv/%USERPROFILE%/nc`, and its port does not answer
- **WHEN** the CLI stops the folder `/srv/%USERPROFILE%/nc`, spelled with the percent signs
- **THEN** the CLI sends no signal
- **GIVEN** instead that the environment has no `NETCLAW_HOME` and has `HOME=/srv/%USERPROFILE%`
- **WHEN** the CLI stops `/srv/%USERPROFILE%/.netclaw`
- **THEN** the CLI sends no signal

#### Scenario: The first of two entries counts

- **GIVEN** the environment of PID 4100 has `NETCLAW_HOME=/h1` and later `NETCLAW_HOME=/h2`, and its port does not answer
- **WHEN** the CLI stops `/h1`
- **THEN** the CLI sends SIGTERM
- **WHEN** the CLI stops `/h2`
- **THEN** the CLI sends no signal
- **AND** the same holds for two `HOME=` entries when there is no `NETCLAW_HOME`

#### Scenario: A 200 whose home is not a full path is no proof

- **GIVEN** the daemon at the port of home B answers 200 with the PID 4100 and `home` set to an empty string,
  to a string with a NUL character, or to a relative path that the working folder of the CLI turns into home B
- **AND** the environment of PID 4100 names home B
- **WHEN** the CLI stops home B
- **THEN** the CLI sends no signal to PID 4100 and does not fail with an unhandled error

#### Scenario: The endpoint variable does not receive the notice

- **GIVEN** `NETCLAW_DAEMON_ENDPOINT` names another daemon
- **WHEN** the CLI stops home A
- **THEN** the notice goes to the port in the config of home A
- **AND** the other daemon receives no request

#### Scenario: The factory reset needs the same proof

- **GIVEN** the PID file of home B holds the PID of the daemon of home A
- **WHEN** an operator starts a factory reset of home B
- **THEN** the CLI sends no signal to the daemon of home A
- **AND** the reset deletes nothing and reports that it failed

### Requirement: A refusal tells the operator how to recover

Every refusal of `stop`, `start`, and the factory reset SHALL carry a second line. The text of the second line is
exact, with one space between the three parts:

```text
<cause> If no Netclaw daemon of this home runs, delete <pidfile>. If PID <pid> is a daemon of this home, check it, then stop it with 'kill <pid>' (Task Manager on Windows).
```

- `<pidfile>` is the PID file path of this home (`<home>/netclaw.pid`).
- `<cause>` is `The daemon at <endpoint> answered for another daemon or home.` when the answer was Refused. It is
  `The daemon at <endpoint> did not confirm PID <pid> for this home.` for every other case of a stop.
  `<endpoint>` is the local control endpoint of this home, for example `http://127.0.0.1:5300`.
  `start` and `status` send no notice, so their `<cause>` is always `No daemon of this home was proven to own PID <pid>.`
  and has no `<endpoint>`.
- The two lines join with a line feed. The result has no third line.
- The text is the same for `stop` and the factory reset. `start` and `status` use the `<cause>` of the previous
  point and the same recovery sentences.

The `kill` advice covers a wedged daemon of this home on macOS and Windows, where the stop has no proof. The
operator must check the process first. The CLI never sends the signal in that case.

The factory reset SHALL show both lines when its stop is a refusal, and SHALL say that nothing was deleted. It
SHALL NOT advise `systemctl --user stop netclaw.service` for a refusal, because the PID may belong to the daemon
of another home. It keeps that advice for a stop failure that is not a refusal.

The CLI help of `netclaw daemon` SHALL name the refusal and the `netclaw.pid` recovery. The daemon runbook
SHALL describe both recoveries.

#### Scenario: The refusal has a cause and a recovery

- **GIVEN** home B has a PID file with PID 4100, and the daemon at its port answers 409
- **WHEN** an operator runs `netclaw daemon stop` for home B
- **THEN** stderr has `Nothing was stopped. PID 4100 is not shown to serve B.`
- **AND** the next line has `answered for another daemon or home`, `delete B/netclaw.pid`, and `kill 4100`

#### Scenario: A refusal without an answer names the missing proof

- **GIVEN** home B has a PID file with PID 4100, and its port does not answer
- **WHEN** an operator runs `netclaw daemon stop` for home B
- **THEN** the second line has `did not confirm PID 4100 for this home`

#### Scenario: Start and status name no notice

- **GIVEN** home B has a PID file with PID 4100, and the process is not shown to serve home B
- **WHEN** an operator runs `netclaw daemon status` or `netclaw daemon start` for home B
- **THEN** the second line is `No daemon of this home was proven to own PID 4100. If no Netclaw daemon of this home runs, delete B/netclaw.pid. If PID 4100 is a daemon of this home, check it, then stop it with 'kill 4100' (Task Manager on Windows).`
- **AND** the output has no `did not confirm`, because these commands send no notice

#### Scenario: The factory reset gives the same recovery

- **GIVEN** the PID file of home B holds the PID of the daemon of home A
- **WHEN** the factory reset of home B stops
- **THEN** the message has `Nothing was deleted`, the refusal lines, and `delete B/netclaw.pid`
- **AND** the message does not have `systemctl --user stop netclaw.service`

### Requirement: Start and status do not claim a daemon without proof

`netclaw daemon start` and `netclaw daemon status` send no notice. They read the PID file of this home and need a
different proof, with no effect on the daemon. A PID from the PID file is shown to serve this home when:

- the environment of the process can be read, and it names this home by the environment rule of the stop
  requirement; or
- the environment cannot be read (macOS, Windows, or no permission), and the lock file of this home is held.

A PID that is not shown to serve this home SHALL NOT be reported as the daemon of this home.

`netclaw daemon status` SHALL print `PID <pid> is not shown to serve <home>.` and the second line of the
recovery requirement, and SHALL exit with code 1. It SHALL print the lines on stdout, as it prints its other
status lines. It SHALL NOT run the readiness probe. It SHALL NOT remove the PID file. `DaemonStatus.IsRunning`
stays `true` for this case, so that `update` and the factory reset go on to the stop, which refuses with the same
proof rule as a stop.

`netclaw daemon start` SHALL NOT print `Daemon already running` for such a PID. It SHALL NOT start a process. It
SHALL print `Daemon was not started. PID <pid> is not shown to serve <home>.` and the second line of the
recovery requirement on stderr once, and SHALL exit with code 1.

A PID that is shown to serve this home keeps the old results: `Daemon running (PID <pid>, uptime ...)` for
status, and `Daemon already running (PID <pid>).` for start. A held lock file with no PID file keeps its old
results too.

When the lock file is held and the PID file is missing, the stop message SHALL NOT advise `pkill netclawd`,
because that command stops the daemon of every home. It SHALL name the lock file of this home and tell the
operator to find the process that holds it and to stop that process by its PID with `kill <pid>`.

#### Scenario: Status does not report the daemon of another home

- **GIVEN** the PID file of home B holds the PID of the daemon of home A
- **WHEN** an operator runs `netclaw daemon status` for home B
- **THEN** the output has `PID 4100 is not shown to serve B.`
- **AND** the output has no `running`
- **AND** the exit code is 1
- **AND** the PID file of home B remains

#### Scenario: Status reports the daemon of this home

- **GIVEN** home A has a running daemon with PID 4100, and its environment names home A
- **WHEN** an operator runs `netclaw daemon status` for home A
- **THEN** the output has `Daemon running (PID 4100`
- **AND** the exit code is 0 when the daemon is ready

#### Scenario: Start does not say that the daemon of another home runs

- **GIVEN** the PID file of home B holds the PID of the daemon of home A
- **WHEN** an operator runs `netclaw daemon start` for home B
- **THEN** stderr has `Daemon was not started. PID 4100 is not shown to serve B.` once
- **AND** the output has no `already running`
- **AND** the CLI starts no process, and the exit code is 1

#### Scenario: Start reports the daemon of this home

- **GIVEN** home A has a running daemon with PID 4100, and its environment names home A
- **WHEN** an operator runs `netclaw daemon start` for home A
- **THEN** the output has `Daemon already running (PID 4100).`

#### Scenario: The stop advice for a held lock names this home

- **GIVEN** the lock file of home A is held and its PID file is missing
- **WHEN** an operator runs `netclaw daemon stop` for home A
- **THEN** the message has the lock file path of home A and `kill`
- **AND** the message does not have `pkill`

### Requirement: A stale PID file keeps its handling

When the PID file names a PID with no process, or a process that is not a `netclawd` process, the CLI
SHALL remove the PID file, send no notice, send no signal, return the failed result `Daemon is not running.`,
and exit with code 1. This behavior is unchanged by this change. When the lock file is held and the PID
file is missing, the CLI SHALL keep the existing result: it reports the missing PID file and sends no signal.

#### Scenario: No process has the PID

- **GIVEN** the PID file holds the PID of a process that has exited
- **WHEN** the CLI stops the daemon
- **THEN** the result is `Daemon is not running.` and the PID file is gone

#### Scenario: A process that is not a daemon has the PID

- **GIVEN** the PID file holds the PID of a process that is not named `netclawd`
- **WHEN** the CLI stops the daemon
- **THEN** that process gets no signal
- **AND** the PID file is removed and the result is `Daemon is not running.`

### Requirement: Start gives the child daemon its home

`netclaw daemon start` SHALL set `NETCLAW_HOME` in the environment of the `netclawd` process that it starts
to the home path of the CLI. The value SHALL replace any `NETCLAW_HOME` that the CLI process inherited.

#### Scenario: The child serves the home of the CLI

- **GIVEN** the CLI acts on home `/tmp/x`
- **AND** the CLI process has `NETCLAW_HOME=/home/op/other`
- **WHEN** the CLI starts the daemon
- **THEN** the environment of the child has `NETCLAW_HOME=/tmp/x`

#### Scenario: The child gets the home when the CLI used the default home

- **GIVEN** the CLI process has no `NETCLAW_HOME`
- **WHEN** the CLI starts the daemon
- **THEN** the environment of the child has `NETCLAW_HOME` set to the default home path

## Purpose

Defines how the CLI starts and stops the daemon of one Netclaw home, and how a daemon accepts a shutdown
notice. A command for one home never stops, alerts, or logs a stop for the daemon of another home.

Cross-cutting terms: see the [engineering glossary](../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: The shutdown notice names the daemon that it is for

`POST /api/lifecycle/shutdown` SHALL carry three query values: `reason`, `pid`, and `home`. `pid` SHALL be
the PID that the caller read from the PID file of its home. `home` SHALL be the canonical absolute path of
that home. The canonical path has every link resolved and no trailing separator.

The daemon SHALL compare `pid` with its own PID and `home` with its own home. Two paths name the same home
when their canonical forms are equal. A path part that does not exist is not a link; it keeps its spelling.
A prefix is not a match.

When both match, the daemon SHALL answer 200 with the reason and its own PID. It SHALL write the
"Netclaw daemon stopping" log line and send the `daemon.stopping` alert, as it did before this change.

When `pid` or `home` does not match, the daemon SHALL answer 409. The body SHALL name the PID and home of
the notice and the PID and home of the daemon. The daemon SHALL write one Warning log line that names both
homes. It SHALL NOT write a "stopping" log line and SHALL NOT send a `daemon.stopping` alert.

When `pid` or `home` is missing, empty, or malformed, the daemon SHALL answer 400. A `pid` is malformed when
it is not a whole number above zero. A `home` is malformed when it is not an absolute path. The body SHALL name `pid` and `home`. The daemon SHALL
NOT treat a missing value as a match. It SHALL NOT write a "stopping" log line and SHALL NOT send an alert.

When `reason` is missing, the daemon SHALL answer 400 and name `reason`. A caller that is not authorized
SHALL get 401, as before.

The daemon owns this decision. The data is call-local (the query values) and process-local (the PID and
the home of the daemon). Nothing is persisted.

Ordered flow (schematic; it omits the authorization gate):

```text
daemon receives POST ?reason=R&pid=P&home=H
  R missing                              -> 400
  P not a whole number > 0, H not absolute -> 400
  P == own PID and canon(H) == canon(own home)
      yes -> log "stopping", send alert, 200 {reason, pid=own PID}
      no  -> log Warning (both homes), 409 {error}
```

#### Scenario: A notice for this daemon is announced

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `?reason=cli-stop&pid=4100&home=/home/op/.netclaw`
- **THEN** the daemon answers 200 with PID 4100
- **AND** the log has one "Netclaw daemon stopping" line
- **AND** one `daemon.stopping` alert is sent

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

#### Scenario: A folder inside the home is not the home

- **GIVEN** a daemon with PID 4100 and home `/home/op/.netclaw`
- **WHEN** a caller sends `home=/home/op/.netclaw/workspaces`
- **THEN** the daemon answers 409

#### Scenario: A notice without identity is refused

- **WHEN** a caller sends `?reason=cli-stop` with no `pid` and no `home`
- **THEN** the daemon answers 400
- **AND** no "stopping" line is written and no alert is sent

#### Scenario: A malformed value is refused

- **WHEN** a caller sends `pid=abc`, `pid=0`, `pid=-1`, an empty `home`, or `home=relative/path`
- **THEN** the daemon answers 400
- **AND** no "stopping" line is written and no alert is sent

### Requirement: A stop needs proof that the PID serves this home

`netclaw daemon stop`, `netclaw update`, and the factory reset SHALL send no signal to the PID in the PID
file until they have proof that the PID serves the home of the command. The CLI SHALL use the home from
`NETCLAW_HOME` or its default, resolved as the daemon resolves it.

The CLI SHALL send the shutdown notice of the first requirement to the local control endpoint of this home.
That endpoint comes from the `Daemon` config of this home. The CLI SHALL NOT use `NETCLAW_DAEMON_ENDPOINT`
or the client config for the notice, because they can name a remote daemon, and the signal goes to a local
PID. The notice carries the PID from the PID file and the canonical home of the CLI.

The proof SHALL be one of these:

- The daemon at that endpoint answers 200, and the PID in the body equals the PID from the PID file.
- The daemon does not answer (no connection, a timeout, or a status other than 200 or 409), and the
  operating system shows that the process serves this home. On Linux, the proof is `NETCLAW_HOME` in the
  environment of the process; an absent value means the default home. A platform that cannot show it gives
  no proof.

A 409 SHALL give no proof and SHALL NOT be overruled by the environment of the process. A 200 whose body has
another PID SHALL give no proof. A daemon from before this change answers 200 for any notice and reports
its own PID, so the PID check keeps a daemon of another home from passing as the daemon of this home.

When the daemon does not answer because of a connection error or a timeout, the CLI SHALL write
`Note: could not notify daemon of shutdown reason: <detail>` on stderr, as it did before this change.

When the CLI has no proof, it SHALL send no signal and SHALL NOT remove the PID file or the lock file. It
SHALL write this text on stderr, return a failed result, and exit with code 1:
`Nothing was stopped. PID <pid> is not shown to serve <home>.`

The CLI owns this decision. The data is call-local: the PID file is durable but the CLI only reads it, and
the proof is not stored.

Ordered flow (schematic; it omits the supervisor and systemd unit steps):

```text
StopAsync(home)
  pid = read <home>/netclaw.pid
  no process with pid, or process is not netclawd -> unchanged (see the stale PID requirement)
  answer = POST <home local endpoint>/api/lifecycle/shutdown?reason&pid&home=canon(home)
  answer is 200 and body.pid == pid                -> proven
  answer is 409, or 200 with another PID            -> refuse
  no answer, Linux, environ(pid).NETCLAW_HOME == home -> proven
  otherwise                                          -> refuse
  proven -> SIGTERM pid (Windows: Kill), wait, remove PID file
```

#### Scenario: A stale PID file of another home does not stop its daemon

- **GIVEN** home A has a running daemon with PID 4100
- **AND** the PID file of home B holds 4100
- **WHEN** an operator runs `NETCLAW_HOME=B netclaw daemon stop`
- **THEN** the CLI sends no signal to PID 4100
- **AND** the daemon of home A keeps running and gets no accepted notice
- **AND** the PID file of home B remains
- **AND** stderr has `Nothing was stopped. PID 4100 is not shown to serve B.`
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

#### Scenario: A daemon of this home proves itself

- **GIVEN** home A has a running daemon with PID 4100
- **WHEN** an operator runs `netclaw daemon stop` for home A
- **THEN** the notice carries `pid=4100` and the canonical path of home A
- **AND** the daemon answers 200 with PID 4100
- **AND** the CLI sends SIGTERM to PID 4100 and the daemon stops

#### Scenario: A daemon that does not answer still stops when its environment names this home

- **GIVEN** the daemon of home A holds PID 4100, its environment has `NETCLAW_HOME` for home A, and its port does not answer
- **WHEN** an operator runs `netclaw daemon stop` for home A on Linux
- **THEN** the CLI sends SIGTERM to PID 4100

#### Scenario: A daemon that does not answer and serves another home is not stopped

- **GIVEN** the PID file of home B holds 4100, the environment of PID 4100 names home A, and the port of B does not answer
- **WHEN** the CLI stops home B
- **THEN** the CLI sends no signal

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

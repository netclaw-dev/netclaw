## ADDED Requirements

### Requirement: Bash launcher sets HOME and PWD and removes CDPATH

When the system starts a Bash shell process, it SHALL set these variables in
the child process environment only:

- `TMPDIR`, `TMP`, and `TEMP`: the run's managed temporary directory, as the
  requirement "Every run receives the standard temporary environment" states.
- `HOME`: the daemon user profile directory. The daemon SHALL read this value
  one time, when it creates the shell environment at startup. The value SHALL
  stay the same for the life of the daemon process.
- `PWD`: the exact working directory of the shell process.

The system SHALL remove `CDPATH` from the Bash child environment. If the user
profile directory is not an absolute path, the system SHALL NOT set `HOME`. The
child process then keeps the `HOME` value of the daemon process. A PowerShell
process SHALL NOT receive the `HOME` or `PWD` value from this requirement.

The launcher and the shell approval parser SHALL read the temporary and `HOME`
values from one shared variable list. The parser SHALL use the same working
directory value that the launcher writes to `PWD`. The two values SHALL NOT be
computed separately.

#### Scenario: Example - Bash process receives the launch variables

- **GIVEN** a POSIX run with a managed temporary directory and a working
  directory
- **AND** the daemon user profile is an absolute path
- **WHEN** Netclaw starts a Bash shell process for the run
- **THEN** `TMPDIR`, `TMP`, and `TEMP` equal the managed temporary directory
- **AND** `HOME` equals the user profile that the daemon read at startup
- **AND** `PWD` equals the working directory
- **AND** `CDPATH` is not set

#### Scenario: Example - relative cd follows the working directory through a link

- **GIVEN** the working directory path contains a symbolic link
- **WHEN** the shell process runs `cd sub` and then `cd ../..`
- **THEN** each `cd` goes to the directory that the approval parser resolved
  lexically from the working directory

#### Scenario: Counterexample - home that is not absolute gives no HOME value

- **GIVEN** the daemon user profile is not an absolute path
- **WHEN** Netclaw starts a Bash shell process
- **THEN** Netclaw does not set `HOME` on the child process
- **AND** the approval parser has no `HOME` launch value

### Requirement: Approval parser trusts a launch variable only while it is unchanged

The shell approval parser SHALL use a launch value only when the selected shell
host has a proven no-startup initial state. For Bash, this state requires a
probed GNU Bash 5.2 or 5.3 at `/bin/bash`, started with only `-c`. The parser
SHALL resolve `$TMPDIR`, `$TMP`, `$TEMP`, and `$HOME` to the launch values in
that state. It SHALL resolve a relative `cd` from the working directory, because
the launcher removes `CDPATH`.

A command that resolves to a launch value SHALL receive the same approval
decision as the same command with the literal path. A stored grant for the
literal directory SHALL apply.

The parser SHALL treat a launch value as unknown after any statement that can
change it. Examples are an assignment, `export`, a prefix assignment, `unset`,
`read`, and `source`. The parser SHALL also treat `CDPATH` as unknown after the
source sets it. The parser SHALL NOT use launch values for source that another
process reads, for example the child of a `bash -lc` wrapper. An unknown value
SHALL stay unresolved. The parser SHALL NOT replace it with the launch value or
with another default.

#### Scenario: Example - $HOME gets the decision of the literal path

- **GIVEN** a Bash host with a proven no-startup initial state
- **AND** the launch `HOME` value is `/home/alice`
- **WHEN** the agent submits `cat "$HOME/x"`
- **THEN** the approval decision equals the decision for `cat /home/alice/x`
- **AND** a stored `cat` grant that covers `/home/alice` allows the command

#### Scenario: Example - $TMPDIR gets the decision of the literal path

- **GIVEN** a Bash host with a proven no-startup initial state
- **WHEN** the agent submits `cat "$TMPDIR/notes.txt"`
- **THEN** the approval decision equals the decision for the literal path below
  the managed temporary directory

#### Scenario: Counterexample - changed HOME is not trusted

- **GIVEN** a Bash host with a proven no-startup initial state
- **WHEN** the agent submits `export HOME=/etc; cat "$HOME/x"`
- **THEN** the parser does not resolve `$HOME` to the launch value
- **AND** the command does not receive an allow decision from the launch value

#### Scenario: Counterexample - changed CDPATH keeps a relative cd unresolved

- **GIVEN** a Bash host with a proven no-startup initial state
- **AND** stored grants allow `cd` and `cat` in the project directory
- **WHEN** the agent submits `CDPATH=/etc; cd sub && cat notes.txt`
- **THEN** the relative `cd` stays unresolved
- **AND** the command is not allowed

#### Scenario: Counterexample - wrapped shell does not use launch values

- **GIVEN** a Bash host with a proven no-startup initial state
- **WHEN** the agent submits `bash -lc 'cat "$TMPDIR/notes.txt"'`
- **THEN** the parser does not resolve the inner `$TMPDIR` to the launch value
- **AND** the command is not allowed

#### Scenario: Counterexample - unproved Bash host does not use launch values

- **GIVEN** a Bash host without a proven no-startup initial state
- **WHEN** the agent submits `cat "$TMPDIR/notes.txt"`
- **THEN** the parser does not resolve `$TMPDIR` to the launch value
- **AND** the command is not allowed

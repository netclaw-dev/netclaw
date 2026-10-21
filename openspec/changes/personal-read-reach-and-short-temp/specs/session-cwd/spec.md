## MODIFIED Requirements

### Requirement: Managed temporary directory is the private temporary location

The system SHALL provide a separate managed temporary directory for each
parent and child run.

On a POSIX host (issue #2554), the directory SHALL be short enough for a Unix
socket path below it:

- base: `/tmp/netclaw-<uid>`, where `<uid>` is the effective user ID of the
  daemon. On macOS the base SHALL use the resolved form
  `/private/tmp/netclaw-<uid>`.
- key: the first 16 lowercase hex characters of the SHA-256 hash of the UTF-8
  text of the session storage root (the version-2 envelope root, or the
  legacy session directory).
- parent run: `<base>/<key>/p`.
- child run: `<base>/<key>/c<h>`, where `<h>` is the first 8 lowercase hex
  characters of the SHA-256 hash of the UTF-8 run ID.
- The storage root of the location SHALL be the key folder `<base>/<key>`.
- For a base of 17 characters, the path
  `<temp_dir>/com.google.Chrome.XXXXXX/SingletonSocket` SHALL have 107
  characters or fewer for a parent and a child run.

Before each shell launch, `ManagedTemporaryEnvironment` SHALL create the base
and the key folder with mode 0700 and SHALL check each of them without
following a link:

- it is not a symbolic link,
- the daemon user owns it,
- it has no group or other permission bits.

Each `file_write` or `file_edit` below the base SHALL first use the same
creation and checks on the managed folders, also when the target folder
exists. A Team session has no shell, so a file tool
can be the first writer. A failed check SHALL refuse the write.

If a check fails, the system SHALL refuse the launch with an error that names
the folder and the failed check. It SHALL NOT set `TMPDIR`, and it SHALL NOT
use another directory. These checks are call-local. Netclaw SHALL NOT change
the mode or owner of a folder that already exists.

`TMPDIR`, `TMP`, and `TEMP` SHALL name the directory, and the shell parser
facts SHALL use the same value. A persisted background-job record SHALL keep
its stored directory and storage root. On Windows the layout SHALL stay
`<session-envelope>/tmp/parent` for a version-2 parent and
`<session-envelope>/subagents/<run-id>/tmp` for a child, with the current
link checks.

The system SHALL identify this
directory as the preferred location for disposable files. It SHALL use the
session directory, not the complete envelope, as the shell working-directory
fallback when no project or explicit working directory exists.

Personal and Team working context and correction text SHALL provide the
absolute managed temporary path when the agent needs an alternative to the
platform temporary root. Public context SHALL retain existing private-path
redaction. The system SHALL NOT silently substitute a path or imply that
temporary-directory cleanup occurs as part of this behavior.

#### Scenario: Example - no-project shell separates cwd and temp

- **GIVEN** a session has no declared project directory
- **WHEN** the agent invokes `shell_execute` without an explicit working
  directory
- **THEN** the shell working directory is the bound session directory at
  `<session-envelope>/workspace`
- **AND** temporary APIs in the process resolve to the managed temporary
  directory

#### Scenario: Counterexample - complete envelope is not the fallback cwd

- **GIVEN** a version-2 session has raw logs and child runs in its envelope
- **WHEN** the agent invokes `shell_execute` without a project or explicit cwd
- **THEN** the shell does not start at `<session-envelope>`
- **AND** a recursive search of `.` does not include those sibling areas

#### Scenario: Parent and child temporary directories do not collide

- **GIVEN** a parent and two child runs belong to one session
- **WHEN** Netclaw resolves their managed temporary directories
- **THEN** each run receives a different directory
- **AND** every directory remains below the same key folder `<base>/<key>`

#### Scenario: Example - a Chrome socket fits below the managed directory

- **GIVEN** a POSIX parent run with the managed directory `<base>/<key>/p`
- **WHEN** a process binds the Unix socket
  `$TMPDIR/com.google.Chrome.XXXXXX/SingletonSocket`
- **THEN** the bind succeeds
- **AND** the same bind below `<session-envelope>/tmp/parent` on the live host
  fails, because that path has 133 characters

#### Scenario: Counterexample - an unsafe base refuses the launch

- **GIVEN** `/tmp/netclaw-<uid>` is a symbolic link, is owned by another user,
  or has mode 0755
- **WHEN** the agent invokes `shell_execute`
- **THEN** the launch fails with an error that names the folder and the cause
- **AND** the process does not start, and Netclaw does not use the deep path

#### Scenario: Example - a first file write keeps the folders private

- **GIVEN** `/tmp/netclaw-<uid>` does not exist and the umask is 0022
- **WHEN** a Team session calls `file_write` on `<temp_dir>/notes.txt`
- **THEN** the base, the key folder, and the run folder have mode 0700
- **AND** a later shell launch of this session or another session passes the
  checks

#### Scenario: Counterexample - own temporary folder gets no platform-temp advice

- **GIVEN** an interactive Personal run with the managed directory
  `/tmp/netclaw-<uid>/<key>/p`
- **WHEN** the agent invokes `file_write` on `/tmp/netclaw-<uid>/<key>/p/a.md`
- **THEN** the system does not suggest the managed temporary directory
- **AND** a `file_write` on `/tmp/a.md` still gets the suggestion

#### Scenario: Example - correction names the managed temporary directory

- **GIVEN** a correction recommends private temporary storage
- **WHEN** the correction is rendered for the agent
- **THEN** it names the exact managed temporary directory for that run
- **AND** it does not name the complete session envelope as disposable storage

#### Scenario: Counterexample - Public context cannot receive managed paths

- **GIVEN** a Public parent or child agent
- **WHEN** it evaluates a platform-temp operation
- **THEN** it does not receive a private managed path
- **AND** existing Public path-redaction behavior remains

#### Scenario: Counterexample - session end does not imply cleanup

- **GIVEN** an agent writes a disposable file under its managed temporary
  directory
- **WHEN** the current session ends
- **THEN** this capability does not delete or schedule deletion of that file
- **AND** retention remains unchanged until a separate cleanup capability is
  specified

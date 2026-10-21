## Why

Issue #2554: an agent writes files to `/tmp` and then `file_read` refuses to
read them. Two faults cause this loop. #2090 (7 Sep 2026) added both.

1. #2090 removed the #1770 rule "interactive Personal file reads reach as far
   as the shell". An explicit Personal `ReadFiles.Mode = Roots` now confines
   `file_read`, but the shell uses the `WriteFiles` profile. With
   `WriteFiles.Mode = All`, the shell reaches `/tmp` and `file_read` does not.
2. #2090 moved `TMPDIR` to `<session-envelope>/tmp/parent`. On the live host
   that path has 92 characters. A Unix socket path has a limit of 107
   characters. Chrome needs 41 more characters, so Chrome fails, and the agent
   sets `TMPDIR=/tmp/v`.

The owner (Aaron, 18 Oct 2026) approved three changes: Fix A, Fix B, and a
doctor warning.

## What Changes

- **Fix A (read reach).** For the Personal audience only, `file_read`,
  `file_list`, `file_search`, and `attach_file` reach at least as far as the
  write profile. Write `All` gives read and attach `All`. Write `Roots` adds
  the write roots to the read and attach roots. Protected paths stay denied.
  `set_working_directory` stays confined. Team and Public do not change.
- **Fix B (short temp folder).** On POSIX, the managed temp folder of a run is
  `/tmp/netclaw-<uid>/<key>/p` (parent) or `/tmp/netclaw-<uid>/<key>/c<8 hex>`
  (child). Netclaw creates the base and the key folder with mode 0700 and
  checks them before each shell launch. A failed check refuses the launch.
  There is no fallback to the deep path. Only the key folder becomes a session
  root. Windows keeps the current layout.
- **Doctor.** `netclaw doctor` warns when an audience profile can write wider
  than it can read. It names the profile and the two modes. It has no
  auto-fix.
- **BREAKING (internal API):** `SessionStoragePaths.CreateVersion2` and
  `SessionStoragePaths.CreateLegacy` take a `ManagedTemporaryLayout`. Persisted
  records do not change format. An old background-job record keeps its stored
  deep path.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-tools`: the `File read tool` requirement. Personal read reach covers
  the write reach. The counterexample scenario changes.
- `tool-authorization`: TA-6 (root sources and the Personal read rule), TA-8
  (the bounded-read redirect scenario), and TA-16 (doctor warning).
- `session-cwd`: the `Managed temporary directory is the private temporary
  location` requirement. The path is short, private, and checked.

## Impact

This change supports PRD-002 (gateway security envelope: default deny, no
silent fallback) and PRD-006 (tool authority comes from the path access
decision).

Out of scope:

- Team and Public read reach
- `$XDG_RUNTIME_DIR` as a base (some hosts do not set it, and it is a small
  tmpfs)
- a change to the Windows layout
- migration of old background-job records

### Security and operational impact

- Personal file tools can read each path that the shell can write. This is the
  #1770 guarantee. Protected paths stay denied for read.
- A local user can create `/tmp/netclaw-<uid>` first, or put a link there. The
  launch then fails with a clear error. Only root can remove a folder that
  another user owns.
- A file tool write that makes the first folder below the base uses the same
  creation and checks as the shell launch.
- A Team or Public root that covers the base does not reach the key folder of
  another session.
- The OS can delete `/tmp` content. The folder is disposable. Netclaw creates
  it again before each launch.
- Release notes: a host with an explicit Personal `ReadFiles.Mode = Roots`
  gets wider Personal read reach after the upgrade. Unattended Personal runs
  (reminders, webhooks) get the same reach with no prompt.

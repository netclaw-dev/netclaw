## MODIFIED Requirements

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands
using tokenization. The verb chain SHALL consist of non-flag,
non-path tokens from the start of the command until the first flag
(`-`), path-like argument, or URL argument. Path-like arguments are
tokens beginning with `/`, `~/`, `./`, `../` or equal to `~`, `.`, or
`..`. The verb-chain output SHALL NOT include any path-like tokens —
the path is captured separately as the candidate's **effective
directory** (see "Path argument as effective directory" below).

For shell approval units, `&&`, `||`, and `;` SHALL split into
separate units, while `|` SHALL remain inside the current unit. For
`bash -c` or `sh -c` wrappers, the inner command SHALL be extracted
and scanned recursively.

When `ShellTokenizer.SplitCompoundCommand` detects bash control-flow
tokens or unbalanced quotes/brackets, it SHALL return an empty
verb-chain list. The approval gate SHALL then offer only `Once` and
`Deny`. See the "Pattern extraction refuses bash control-flow"
requirement for details.

The matcher SHALL operate on `ApprovalEntry` records keyed by
`(verb, directory)`. The "is this string a verb chain or a directory
root?" inspection logic of v1 SHALL NOT be present in the v2 matcher.

Approval persistence SHALL store one `ApprovalEntry` per extracted verb
chain, EXCEPT for clauses whose verb is in the side-effect skip list
(see "Pure side-effect verbs not persisted"). Compound commands SHALL
produce up to N entries from one user click on `Always here` or `Always
anywhere`, where N is the count of clauses with extractable verbs that
are not in the skip list.

#### Scenario: Verb chain extracted from simple command

- **GIVEN** the command `git push origin main`
- **WHEN** the pattern is extracted
- **THEN** the verb chain is `git push`
- **AND** `origin` and `main` are not part of the verb chain

#### Scenario: Verb chain stops at flag

- **GIVEN** the command `ls -la /tmp`
- **WHEN** the pattern is extracted
- **THEN** the verb chain is `ls`
- **AND** the effective directory is `/tmp`

#### Scenario: Verb chain stops at first path argument

- **GIVEN** the command `find /home/petabridge -name "netclaw" -type f`
- **WHEN** the pattern is extracted
- **THEN** the verb chain is `find`
- **AND** the effective directory is `/home/petabridge`
- **AND** the verb does NOT include `/home/petabridge`

#### Scenario: Multi-level verb chain

- **GIVEN** the command `docker compose up -d`
- **WHEN** the pattern is extracted
- **THEN** the verb chain is `docker compose up`

#### Scenario: Control operators create separate approval units

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** `git add`, `git commit`, and `git push` are checked as
  separate approval units against the v2 matcher
- **AND** each unit's effective directory comes from its own clause
  (cwd in this case, since none have explicit path arguments other
  than `.`)

#### Scenario: Compound segments batched in one prompt

- **GIVEN** none of `git add`, `git commit`, `git push` are approved
- **WHEN** the command `git add . && git commit -m "fix" && git push`
  is checked
- **THEN** a single approval prompt lists all three verbs as bullets
- **AND** one click on `Always here` persists three `(verb, cwd)`
  entries (each clause's effective directory resolves to cwd)

#### Scenario: bash -c inner command scanned recursively

- **GIVEN** the command `bash -c "git push --force"`
- **WHEN** approval and hard deny are checked
- **THEN** the inner command `git push --force` is extracted and
  scanned
- **AND** verb chain `git push` is checked through the v2 matcher

### Requirement: Persistent approval storage

The system SHALL store persistent approvals in
`~/.netclaw/config/tool-approvals.json` using a `version: 2` typed
schema. Each entry SHALL be an `ApprovalEntry` with a required `verb`
field (the command head plus subcommand chain, e.g. `git remote` —
NOT `git remote add origin https://...`) and an optional `directory`
field (an absolute path, or `null` for the global wildcard). The file
SHALL contain per-audience sections with per-tool `ApprovalEntry`
lists. The file SHALL NOT be monitored by `ConfigWatcherService`.

When the daemon reads a `tool-approvals.json` file that does not have
`version: 2`, the file SHALL be quarantined to
`tool-approvals.json.v1.bak` and an empty v2 store SHALL be returned.
The daemon SHALL write the empty v2 store on the next persist call. No
automatic translation of v1 entries SHALL be performed.

The matcher SHALL approve a candidate invocation when there exists an
`ApprovalEntry` whose `verb` equals the candidate's extracted verb
chain AND (`directory` is `null` OR the candidate's **effective
directory** is under `directory`). The effective directory is the
candidate's extracted path argument when present; otherwise it is the
candidate's cwd at evaluation time. Relative extracted paths resolve
against cwd before the under-check. Symlink-segment guard SHALL apply
to the resolved effective directory.

The file SHALL also be operator-editable via the `netclaw approvals`
CLI (see the `netclaw-cli` capability). The daemon SHALL pick up
out-of-band edits — whether made by direct file editing or by the
CLI — on the next approval check, without requiring a restart.

#### Scenario: Always here persists typed (verb, directory) entries from extracted paths

- **GIVEN** the user clicks `Always here` for verbs `git remote` and
  `git rev-parse` from a command running in cwd `~/repos/foo/` with
  no explicit path arguments
- **WHEN** the approval is processed
- **THEN** `tool-approvals.json` contains
  `[{"verb":"git remote","directory":"~/repos/foo/"},
    {"verb":"git rev-parse","directory":"~/repos/foo/"}]`
- **AND** the daemon does NOT restart

#### Scenario: Always here uses extracted path as directory when present

- **GIVEN** the agent invokes `find /home/petabridge -name X` (cwd is
  the session_dir)
- **WHEN** the user clicks `Always here`
- **THEN** `tool-approvals.json` contains
  `{"verb":"find","directory":"/home/petabridge"}`
- **AND** the entry's directory is `/home/petabridge` (the extracted
  path), NOT the session_dir cwd

#### Scenario: Folder-scoped trust compounds across deeper paths

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"find","directory":"/home/petabridge"}`
- **WHEN** the agent invokes `find /home/petabridge/.netclaw -name X`
- **THEN** the candidate's effective directory is
  `/home/petabridge/.netclaw`
- **AND** that directory is under the entry's `/home/petabridge`
- **AND** the matcher returns approved
- **AND** no prompt is rendered

#### Scenario: Always anywhere persists null-directory entry

- **GIVEN** the user clicks `Always anywhere` for verb `freshdesk`
- **WHEN** the approval is processed
- **THEN** `tool-approvals.json` contains
  `{"verb":"freshdesk","directory":null}`

#### Scenario: v1 file quarantined on first read

- **GIVEN** `tool-approvals.json` exists without a `version` field
  (or with `version` other than `2`)
- **WHEN** the daemon loads the file
- **THEN** the file is moved to `tool-approvals.json.v1.bak`
- **AND** `Load()` returns an empty v2 store
- **AND** no v1 entries are translated to v2

#### Scenario: Matcher approves under directory entry using extracted path

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"git status","directory":"~/repos/foo/"}`
- **WHEN** the agent invokes `git status` (no explicit path arg) with
  cwd `~/repos/foo/`
- **THEN** the candidate's effective directory falls back to cwd
- **AND** the matcher returns approved
- **AND** no prompt is rendered

#### Scenario: Matcher approves under null-directory entry

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"freshdesk","directory":null}`
- **WHEN** the agent invokes `freshdesk --since=24h` with cwd
  `~/.netclaw/sessions/<id>/`
- **THEN** the matcher returns approved regardless of effective
  directory

#### Scenario: Matcher rejects when effective directory is outside entry directory

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"git remote","directory":"~/repos/foo/"}`
- **WHEN** the agent invokes `git remote -v` with cwd `~/repos/bar/`
- **THEN** the candidate's effective directory falls back to cwd
  `~/repos/bar/`
- **AND** the matcher returns not-approved
- **AND** the approval gate prompts the user

#### Scenario: Approve once is retry-scoped only

- **GIVEN** the user clicks `Once` for command `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts
  again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Operator-applied revocation visible without restart

- **GIVEN** the daemon is running with a persisted entry
  `{"verb":"git push","directory":null}`
- **WHEN** an operator removes that entry via `netclaw approvals revoke`
- **AND** a new approval check evaluates `git push`
- **THEN** the daemon re-loads the file and observes the entry is gone
- **AND** the user is prompted for approval again
- **AND** the daemon was not restarted

### Requirement: Directory-root approvals for shell_execute

For `shell_execute`, persistent approvals SHALL be stored as typed
`(verb, directory)` `ApprovalEntry` records, NOT as separate verb
patterns and directory-root entries. The matcher SHALL approve a
candidate invocation when an `ApprovalEntry` exists whose `verb`
matches the candidate's verb chain AND (`directory` is `null` OR the
candidate's **effective directory** is under `directory`).

The candidate's effective directory SHALL be the first path-like
argument extracted from the command if present (with the file-parent
rule below applied), otherwise the cwd resolved by `ShellTool`.

When the extracted path resolves to a file (rather than a directory),
the persisted entry's directory SHALL be the parent directory of that
file. This is a string operation — `Path.GetDirectoryName(...)` — with
no filesystem syscall, so it produces deterministic results regardless
of file existence at extract time.

For multi-path commands (e.g. `cp /src/a /dst/b`), the **first** path
argument SHALL be used as the effective directory. Subsequent path
arguments SHALL NOT influence the persisted entry.

For commands where the path is hidden behind a flag (e.g. `git -C
/repo log`, `make -C /build target`), the effective directory SHALL
fall back to cwd. Operators with that workflow SHALL use
`set_working_directory` to declare scope explicitly.

`Once` SHALL retry only the blocked call; it SHALL NOT create any
session or persistent approval.

`This chat` SHALL store `(verb, effective directory)` entries in
session-scoped memory only.

`Always here` SHALL persist `(verb, effective directory)` entries to
`tool-approvals.json`.

`Always anywhere` SHALL persist `(verb, null)` entries to
`tool-approvals.json` — the global wildcard.

The system SHALL enforce path normalization, boundary-safe
containment, path traversal checks, and `ToolPathPolicy` as the safety
backstop. `ToolPathPolicy` SHALL resolve symlinks along every component
of a candidate path so that a planted symlink under an approved
directory cannot be used to reach a protected path that lies outside
that directory.

The minimum-depth check SHALL apply to the effective directory of
`Always here` and `This chat` persistence, not just the cwd:
`Always here` SHALL NOT persist a directory shallower than two path
segments. When the effective directory is too shallow (e.g. `find /` or
`rm ~`), the prompt SHALL omit the `Always here` button (only `Once`,
`This chat`, `Always anywhere`, `Deny` remain) so the user cannot
accidentally write a too-shallow root.

#### Scenario: Once retries only the blocked call

- **GIVEN** a shell command `cat ~/repos/foo/notes.md` requires approval
- **WHEN** the user clicks `Once`
- **THEN** only the current blocked call is retried
- **AND** no `ApprovalEntry` is recorded
- **AND** a later `cat ~/repos/foo/other.md` prompts again

#### Scenario: Always here uses extracted path as effective directory

- **GIVEN** a shell command `find /home/petabridge -name X` with cwd
  `~/.netclaw/sessions/<id>/`
- **WHEN** the user clicks `Always here`
- **THEN** `{"verb":"find","directory":"/home/petabridge"}` is written
  to `tool-approvals.json` (NOT the session_dir cwd)
- **AND** a future `find /home/petabridge/.netclaw` is auto-approved

#### Scenario: Always here on file-targeting command stores parent directory

- **GIVEN** a shell command `cat ~/.bashrc`
- **WHEN** the user clicks `Always here`
- **THEN** the persisted entry is `{"verb":"cat","directory":"~/"}`
  (the parent of `~/.bashrc`, not the file path itself)
- **AND** a future `cat ~/.profile` is auto-approved (same verb,
  same parent directory)

#### Scenario: Multi-path command uses first path

- **GIVEN** a shell command `cp /src/a.txt /dst/b.txt`
- **WHEN** the user clicks `Always here`
- **THEN** the persisted entry is `{"verb":"cp","directory":"/src/"}`
  (parent of `/src/a.txt`, the first path argument)
- **AND** a future `cp /src/c.txt /elsewhere/d.txt` is auto-approved

#### Scenario: Flag-hidden path falls back to cwd

- **GIVEN** a shell command `git -C /repo log` with cwd `~/work/`
- **WHEN** the pattern is extracted
- **THEN** the effective directory is `~/work/` (cwd; the path behind
  `-C` is not extracted)
- **AND** clicking `Always here` persists `(git, ~/work/)`, not
  `(git, /repo)`

#### Scenario: Always anywhere stores (verb, null) entry

- **GIVEN** a shell command `freshdesk --since=24h` requires approval
- **WHEN** the user clicks `Always anywhere`
- **THEN** `{"verb":"freshdesk","directory":null}` is written to
  `tool-approvals.json`
- **AND** a scheduled task firing `freshdesk` in any cwd is
  auto-approved on next invocation

#### Scenario: Boundary-safe matching prevents prefix collisions

- **GIVEN** `{"verb":"cat","directory":"/home/user/"}` is approved
- **WHEN** the agent runs `cat data.txt` with cwd `/home/usersecret/`
- **THEN** the candidate does NOT match the entry
- **AND** the approval gate prompts the user

#### Scenario: Symlink in effective directory breaks the approval match

- **GIVEN** `{"verb":"cat","directory":"/home/user/safe/"}` is approved
- **AND** `/home/user/safe/leak` is a directory symlink resolving
  to `/etc`
- **WHEN** the agent runs `cat /home/user/safe/leak/passwd`
- **THEN** the symlink-segment check breaks the auto-approval
- **AND** `ToolPathPolicy.CommandReferencesDeniedPath` blocks
  execution if the canonical path is protected

#### Scenario: Shallow extracted path prevents Always here

- **GIVEN** an approval prompt for `find / -name X` (the extracted
  path is `/`, depth 0)
- **WHEN** the prompt is rendered
- **THEN** the `Always here` button is omitted
- **AND** only `Once`, `This chat`, `Always anywhere`, `Deny` are
  shown — same behavior as today's shallow-cwd case

## ADDED Requirements

### Requirement: Path argument as effective directory

The shell verb extractor SHALL classify each token in a clause as
either a verb-chain token or a path-like token. A token is path-like
when it starts with `/`, `~/`, `./`, `../`, or is exactly equal to
`~`, `.`, or `..`. Other tokens — even those containing `/` somewhere
internally, like a URL or a regex — SHALL NOT be classified as paths.
This is intentionally conservative; a false positive would silently
expand or contract trust scope.

For each clause, the extractor SHALL emit a `(verb, candidateDirectory)`
pair where `verb` is the chain of leading non-flag, non-path tokens and
`candidateDirectory` is the **first** path-like token encountered in
that clause, or `null` if the clause contains no path token. The
matcher and persistence layer SHALL treat `candidateDirectory` as the
candidate's effective directory when present, falling back to the
spawned process's cwd otherwise.

When persisting an entry whose `candidateDirectory` resolves to a
file rather than a directory (determined heuristically by checking
whether the path has a final extension via `Path.HasExtension`), the
persisted directory SHALL be `Path.GetDirectoryName(...)` of the
extracted path. This is a string operation; no filesystem syscall is
performed at extract time.

#### Scenario: Absolute path token classified as path

- **GIVEN** the command `find /home/petabridge -name X`
- **WHEN** tokens are classified
- **THEN** `find` is a verb-chain token
- **AND** `/home/petabridge` is the candidate directory
- **AND** `-name` and `X` are not part of either output

#### Scenario: Tilde-prefixed token classified as path

- **GIVEN** the command `cat ~/.profile`
- **WHEN** tokens are classified
- **THEN** the candidate directory is `~/` (parent of `~/.profile`,
  applying the file-parent rule)

#### Scenario: Relative dot-path classified as path

- **GIVEN** the command `grep -r foo ./build`
- **WHEN** tokens are classified
- **THEN** the candidate directory is `./build`
- **AND** the matcher resolves `./build` against cwd at evaluation
  time

#### Scenario: URL not classified as path

- **GIVEN** the command `curl https://example.com/foo`
- **WHEN** tokens are classified
- **THEN** the candidate directory is `null`
- **AND** the matcher falls back to cwd

#### Scenario: Internal slash not classified as path

- **GIVEN** the command `grep -r 'a/b' .`
- **WHEN** tokens are classified
- **THEN** `'a/b'` is NOT classified as a path (does not start with
  `/`, `~`, `./`, or `../`)
- **AND** `.` is the candidate directory (resolves to cwd)

### Requirement: Pure side-effect verbs not persisted

The system SHALL skip persistence for clauses whose verb is in the
side-effect-only skip list AND that have no path argument AND no
shell redirect operator (`>`, `>>`, `|`), even when the user clicks
`Always here` or `Always anywhere` on a compound command containing
those clauses. Skipped clauses SHALL still be authorized for the
current call (the click still grants runtime permission), but no
`ApprovalEntry` SHALL be written for them.

The skip list SHALL contain at least: `echo`, `printf`, `:` (bash
null command), `true`, `false`. The list SHALL be kept conservative —
only commands that produce stdout-only side effects without filesystem
or process impact when used without redirects.

The resolution line emitted to the channel after persistence SHALL
list which verbs were persisted and which were authorized-once
because they were skip-listed, so the operator can see exactly what
ended up in the store.

#### Scenario: Echo with no path argument is authorized but not persisted

- **GIVEN** the user clicks `Always here` on the compound command
  `cat A.txt; echo "==="; cat B.txt`
- **WHEN** persistence runs
- **THEN** `tool-approvals.json` contains entries for `cat` only
  (one or two entries depending on the path-extraction collapse rule)
- **AND** no entry for `echo` is written
- **AND** the resolution line indicates the echo clause was authorized
  for this call

#### Scenario: Echo with redirect is persisted normally

- **GIVEN** the user clicks `Always here` on the command
  `echo hello > /tmp/log.txt`
- **WHEN** persistence runs
- **THEN** `tool-approvals.json` contains
  `{"verb":"echo","directory":"/tmp/"}` (parent of the redirect target)
- **AND** the redirect operator triggers normal persistence

#### Scenario: True and false treated as side-effect-only

- **GIVEN** the user clicks `Always anywhere` on the command
  `make build || true`
- **WHEN** persistence runs
- **THEN** `tool-approvals.json` contains
  `{"verb":"make build","directory":null}` only
- **AND** no entry for `true` is written

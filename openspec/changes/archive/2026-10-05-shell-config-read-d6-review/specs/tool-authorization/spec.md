## MODIFIED Requirements

### Requirement: TA-6 Path access decisions own file-tool authority

A file tool SHALL get its filesystem authority only from a path access
decision for its exact file operation (`Read`, `Write`, `Attach`, or
`DeclareProjectScope`). A shell path SHALL use the `Write` operation, except
that a Bash program that only reads its operands SHALL get `Read` protection
for a write-protected path (owner decision D6). The
decision SHALL apply, in order: the canonical path, the audience root catalog,
the link check, then protection.

- Roots SHALL come from the audience profile (`ReadFiles`, `WriteFiles`,
  `AttachFiles` with mode `None`, `Roots`, or `All`), the session storage
  envelope of the session, the declared project directory, and the global read
  roots (`{skills_dir}`, `{identity_dir}`, `{workspaces_dir}`) for `Read` only
  and never for `Public`.
- Only `Personal` SHALL get the shared Netclaw sessions root and the legacy
  logs root. `Team` and `Public` SHALL get only their own session envelope
  and session directory. A child run SHALL inherit the audience and workspace
  limits of its parent.
- A legacy run MAY read its own exact raw log. That exact-file authority SHALL
  NOT cover the parent directory, an adjacent file, or a project declaration.
  A storage ancestor that Netclaw reads for a link check SHALL NOT grant
  directory authority.
- `Personal` with mode `All` SHALL skip root checks, attended or unattended
  (decision D2). Only a `DeclareProjectScope` decision SHALL stay inside the
  trusted roots. Consent SHALL NOT widen an explicit `Roots` or `None` profile,
  and a bounded profile SHALL confine attended and unattended runs alike.
- A relative path SHALL resolve against the project directory, else the
  session directory. File tools SHALL NOT expand `~`; `~/x` is a relative path.
- A path through a link that leaves the root SHALL be denied. A path whose
  base has a link ancestor SHALL be denied, and Netclaw SHALL NOT try another
  base.
- Protection SHALL depend on the operation. Each file under the config
  directory is
  [ordinary configuration](../../../docs/spec/GLOSSARY.md#ordinary-configuration)
  and SHALL be readable by a file tool (owner decision D6), except
  `secrets.json` and the webhook route files, which hold the verification
  secret. This includes `netclaw.json`, the grant store `tool-approvals.json`,
  `daemon.env`, device state, bootstrap state, and the hard-deny override
  file. Secrets (`secrets.json`), webhook route files, keys, the database,
  process-control files, and the tooling shadow SHALL be read-denied. The config directory, secrets, keys, the database, process
  control files, system skills, and server feeds SHALL be write-denied.
- Shell text that names secrets, webhook route files, keys, the database, or
  process-control files SHALL be denied. Shell text that names the config
  directory SHALL be denied. Only an exact path argument of a read-only shell
  program that names one file below the config directory SHALL leave this
  text check. A `..` segment, a glob, the directory itself, and program text
  (a `jq` module search path, `python3 -c`, `node -e`) SHALL keep the denial.
- A read-only shell program SHALL be one of the policy-data programs `cat`,
  `head`, `tail`, `wc`, `grep`, `jq`, and `diff`, with bounded argument
  values and no assignment prefix. A redirect that writes SHALL keep `Write`
  protection for its target only, and a null-device redirect SHALL be
  ignored. A plain
  argument word that names an entry of the command's directory SHALL make the
  program not read-only. Such a program SHALL get `Read` protection only for a
  path that the write list protects. A directory operand that holds a
  read-denied path SHALL stay denied. Each other shell program SHALL keep
  `Write` protection for each path, so each write to a config file stays
  denied.
- Owner decision D5 (option A): a shell glob word that can match a protected
  shell path, the default credential store (`~/.netclaw/keys`,
  `~/.netclaw/config/secrets.json`), or a directory that contains one, SHALL
  get the literal-path denial (`shell_references_protected_path`). The match
  SHALL be lexical and segment by segment. It SHALL cover dot entries,
  bracket expressions, and case. Netclaw SHALL NOT list directories or follow
  links for this check.
- Accepted D5 gap: a link below the covering directory of a glob that leads
  to a protected path is not seen by the lexical check. Only a person can make
  such a link, because an agent call that links to the credential store
  (`ln -s ~/.netclaw/keys x` or `ln -s ~/.netclaw/k* x`) is denied.
- A shell word that reads a proved variable value SHALL get the protected-path
  decision of the literal value.
- Allow checks SHALL compare paths with ordinal case except on Windows. Deny
  checks SHALL ignore case.
- A path access denial SHALL be terminal and SHALL NOT reveal root paths to a
  Public session. No grant SHALL replace a path access denial, attended or
  unattended.
- Tool capability and shell command policy SHALL run before file protection.
  File authority SHALL NOT enable shell. Netclaw SHALL derive the known real
  paths of a shell call from the command analysis, independent of approval
  candidates, and SHALL check known causal-intent and fallback paths before
  stored or reviewed-safe coverage.
- A readable config file SHALL NOT imply write, edit, attach, or other shell
  authority. Secret values SHALL live only in protected stores.

Owner: `PathAccessPolicy` owns the path access decision, and its result is
call-local. `ToolPathPolicy` owns protection and the D5 glob match
(`GlobMayReachDeniedPath`, with the segment match in `ShellGlobScope`). Its
read, write, and shell lists come from `DaemonToolPathPolicyFactory` and are
process-local. No state of these checks is durable.

#### Scenario: Each config file but the secrets is readable

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `netclaw.json`, `tool-approvals.json`, or `hard-deny-overrides.json`
- **THEN** the path access decision allows the read
- **AND** a `shell_execute` call with `cat <config dir>/hard-deny-overrides.json` is not denied

#### Scenario: A shell write to a config file stays denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `cp other.json <config dir>/hard-deny-overrides.json`, `echo x > <config dir>/netclaw.json`, or `sort -o <config dir>/netclaw.json <config dir>/netclaw.json`
- **THEN** authorization returns `Denied`

#### Scenario: Ordinary config is readable but not by shell text

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `netclaw.json` or on `tool-approvals.json`
- **THEN** the path access decision allows the read
- **AND** a `shell_execute` call whose text names the whole config directory (`grep -r token <config dir>` or `cat <config dir>/*.json`) is denied

#### Scenario: Program text that names the config directory stays denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `jq -n 'import "secrets" as $s {search: "<config dir>"}; $s'`
- **THEN** authorization returns `Denied`

#### Scenario: A harmless redirect does not deny a config read

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `grep -n port <config dir>/netclaw.json 2>/dev/null`
- **THEN** the call is not denied

#### Scenario: Webhook route files stay read-denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on a file in the webhooks directory, or `shell_execute` with `cat <config dir>/webhooks/<route>.json`
- **THEN** the read is denied

#### Scenario: Secrets stay read-denied

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `secrets.json` or on a file in the `keys` directory
- **THEN** the path access decision denies the read

#### Scenario: A readable grant store is not writable

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_write` on `tool-approvals.json`
- **THEN** the path access decision denies the write

#### Scenario: A glob that can match the credential store is denied as the literal path

- **GIVEN** an interactive Personal session with a grant for anywhere for `cat` (catalog case `glob-credential-keys-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `cat ~/.netclaw/k*/*.xml`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`
- **AND** `cat ~/.netclaw/*/secrets.json` is denied with the same reason

#### Scenario: A glob that cannot match a protected path is not denied

- **GIVEN** an interactive Personal session with a grant for anywhere for `ls` (catalog case `glob-in-directory-segment-uses-global-grant`)
- **WHEN** the model calls `shell_execute` with `ls -d ~/repositories/*/akka*`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: An agent cannot link to the credential store

- **GIVEN** an interactive Personal session with a grant for anywhere for `ln` (catalog case `glob-link-to-credential-keys-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `ln -s ~/.netclaw/k* keys-link`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`

#### Scenario: A variable that holds a protected path is denied

- **GIVEN** an interactive Personal session with a grant for anywhere for `cat` (catalog case `assigned-credential-path-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `x=~/.netclaw/config/secrets.json; cat "$x"`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`

#### Scenario: A stored grant decides outside the trusted roots in an unattended run

- **GIVEN** an unattended Personal run in Approval mode and the default Personal profile
- **AND** a folder grant for `make` in an external directory
- **WHEN** the model calls `shell_execute` with `make` in that directory
- **THEN** the call is allowed by the stored grant, as in a chat

#### Scenario: An unattended run without a grant stays denied

- **GIVEN** an unattended Personal run in Approval mode and no grant
- **WHEN** the model calls `shell_execute` with `make` in an external directory
- **THEN** the call is denied with `approval_required_unattended`
- **AND** a chat of the same audience would prompt for the same call

#### Scenario: An unattended run has the file reach of a chat

- **GIVEN** an unattended Personal run and the default Personal profile
- **WHEN** the model calls `file_read` on a file outside the session and project
- **THEN** the path access decision is the same as for an interactive Personal session

#### Scenario: A bounded profile confines an unattended run

- **GIVEN** a Personal profile with `WriteFiles` mode `Roots` and an unattended run
- **WHEN** the model calls `shell_execute` with a working directory outside those roots
- **THEN** the call is denied with `shell_working_directory_outside_trust_zone`
- **AND** a covering stored grant does not change the denial

#### Scenario: A grant never opens a protected path

- **GIVEN** an unattended Personal run in Approval mode and a grant for `cat`
- **WHEN** the model calls `shell_execute` with `cat <config dir>/netclaw.json`
- **THEN** the call is denied

#### Scenario: Team does not get the shared sessions root

- **GIVEN** a Team session
- **WHEN** the model calls `file_read` on a file in another session's directory
- **THEN** the path access decision denies the read

#### Scenario: Link escape from a project base

- **GIVEN** a project directory whose ancestor is a link to `/outside`
- **WHEN** the model calls `file_read` with a relative path
- **THEN** the path access decision denies the read
- **AND** Netclaw does not retry against the session directory

#### Scenario: Restricted session cannot read a sibling session

- **GIVEN** a Team session
- **WHEN** the model calls `file_read` on the raw log of another session
- **THEN** the path access decision denies the read

#### Scenario: Personal session keeps cross-session read access

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on a file in another session directory
- **THEN** the path access decision allows the read

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
  envelope of the session, the managed temporary key folder of the run
  (`<base>/<key>`, see `session-cwd`), the declared project directory, and the
  global read roots (`{skills_dir}`, `{identity_dir}`, `{workspaces_dir}`) for
  `Read` only and never for `Public`.
- The managed temporary key folder SHALL be a session root for every
  audience. The shared base (`/tmp/netclaw-<uid>`) SHALL NOT be a root, so a
  session cannot reach the key folder of another session.
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
  trusted roots. Consent SHALL NOT widen a profile, and a bounded profile SHALL
  confine attended and unattended runs alike.
- Personal read rule (issue #2554): for `Personal` only, a `Read` or `Attach`
  decision SHALL reach at least as far as the `WriteFiles` profile. Write mode
  `All` SHALL make the read and attach mode `All`. Write mode `Roots` SHALL add
  the write roots to the read and attach roots. The rule SHALL NOT apply to a
  `DeclareProjectScope` decision or to `Team` or `Public`. The trusted-root
  list for a `Read` or `Attach` operation SHALL use the same rule.
  `PathAccessPolicy` owns the rule. Its data is call-local.
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
  control files, and the tooling shadow SHALL be write-denied. The system
  skill folder and the server feed folder SHALL NOT be protected (owner
  decision, 2026-10-05): skills are agent guidance, as the identity files
  are, and not control plane. The sync state files of each configured
  server feed (skills and sub-agents) are integrity records and SHALL stay
  write-denied; a file tool MAY read them. The shell and the file tools SHALL
  use the same protected-path list.
- Shell text that names secrets, webhook route files, keys, the database, or
  process-control files SHALL be denied. Shell text that names the config
  directory SHALL be denied. Only an exact path argument of a read-only shell
  program that names one file below the config directory SHALL leave this
  text check. A `..` segment, a glob, a brace word, the directory itself, and
  program text (a `jq` module search path, `python3 -c`, `node -e`) SHALL keep
  the denial. The check SHALL also see the directory in each spelling: with
  `//`, `/./`, or `name/../` in the text, and in the unquoted value of a word
  whose quotes split the name.
- A read-only shell program SHALL be one of the policy-data programs `cat`,
  `head`, `tail`, `wc`, `grep`, `jq`, and `diff`, with bounded argument
  values and no assignment prefix. A redirect that writes SHALL keep `Write`
  protection for its target only, and a null-device redirect SHALL be
  ignored. A plain
  argument word that names an entry of the command's directory, or a word
  with a brace, SHALL make the program not read-only. Such a program SHALL get
  `Read` protection only for a path that the write list protects. A
  write-protected directory operand that holds a read-denied path SHALL stay
  denied. Known gap (issue #2341): a recursive or brace read that names only a
  parent of the config directory is not denied by this rule. Each other shell program SHALL keep
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
- A shell path that file protection denies SHALL get the reason
  `shell_path_protected` when the path, or its link target, is a
  write-protected path. Otherwise it SHALL get the reason
  `shell_path_outside_trusted_roots`: a bounded (`Roots`) profile does not hold
  the path.
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
- **WHEN** the model calls `shell_execute` with `cat <config dir>/secrets.json`
- **THEN** the call is denied

#### Scenario: A brace word keeps the denial

- **GIVEN** a Personal run, attended or unattended
- **WHEN** the model calls `shell_execute` with `cat <config dir>/{netclaw,secrets}.json`
- **THEN** the call is denied, because Bash expands the word to `secrets.json`
- **AND** `cat <config dir>/netclaw.json` is not denied

#### Scenario: Program text keeps the denial in each spelling

- **GIVEN** a Personal run, attended or unattended
- **WHEN** the model calls `shell_execute` with `jq -n 'import "secrets" as $s {search: "<home>//config"}; $s'`, or with `<home>/./config`, `<home>/x/../config`, or `<home>/con'fig'` in the program text
- **THEN** the call is denied
- **AND** program text that names `<home>/x/../other` is not denied by this check

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

#### Scenario: Personal read reaches the write profile

- **GIVEN** a Personal profile with `ReadFiles` mode `Roots` and `WriteFiles`
  mode `All`, attended or unattended
- **WHEN** the model calls `file_read` on `/tmp/x`
- **THEN** the path access decision allows the read

#### Scenario: Personal project declaration stays confined

- **GIVEN** a Personal profile with `ReadFiles` mode `Roots` and `WriteFiles`
  mode `All`
- **WHEN** the model calls `set_working_directory` with `/tmp/x`
- **THEN** the path access decision denies the declaration

#### Scenario: Team read does not follow the write profile

- **GIVEN** a Team profile with `ReadFiles` mode `Roots` `[{session_dir}]` and
  `WriteFiles` mode `Roots` `[{session_dir}, D]`
- **WHEN** the model calls `file_read` on `D/x`
- **THEN** the path access decision denies the read

#### Scenario: A session reads its own temporary key folder

- **GIVEN** a Team session with the managed temporary folder `<base>/<key>/p`
- **WHEN** the model calls `file_read` on `<base>/<key>/p/x`
- **THEN** the path access decision allows the read

#### Scenario: A session cannot read another temporary key folder

- **GIVEN** a Team session with the key folder `<base>/<key>`
- **WHEN** the model calls `file_read` on `<base>/<other key>/p/x` or `<base>/x`
- **THEN** the path access decision denies the read

#### Scenario: Personal session keeps cross-session read access

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on a file in another session directory
- **THEN** the path access decision allows the read

#### Scenario: A skill script runs from its skill folder

- **GIVEN** an interactive Personal session with a grant for anywhere for `bash`
- **AND** a script `scripts/audit.sh` in a server feed skill folder
- **WHEN** the model calls `shell_execute` with `bash <feed skill folder>/scripts/audit.sh`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`
- **AND** `ls ~/.netclaw/skills/.system/` is not denied

#### Scenario: A skill folder write gets the decision of an ordinary path

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_write` or `file_edit` on a file in the system skill folder
- **THEN** the decision is the same as for a file in the user skill root
- **AND** `touch <feed skill folder>/added` asks for approval and is not denied

#### Scenario: A control-plane write stays denied in each form

- **GIVEN** an interactive Personal session with a grant for anywhere for `echo`, `touch`, `cp`, `mv`, `tee`, `rm`, `sed`, and `bash`
- **WHEN** the model writes to `netclaw.json`, `tool-approvals.json`, `secrets.json`, a webhook route file, or a key file with a redirect, `touch`, `cp`, `mv`, `tee`, `rm`, `sed -i`, `bash -c`, `file_write`, or `file_edit`
- **THEN** authorization returns `Denied`

#### Scenario: A link to the config directory names the protected cause

- **GIVEN** an interactive Personal session and a project link `cfg` to the config directory
- **WHEN** the model calls `shell_execute` with `touch cfg/netclaw.json`
- **THEN** authorization returns `Denied` with reason `shell_path_protected`

#### Scenario: A bounded profile names the trusted-root cause

- **GIVEN** a Personal profile with `WriteFiles` mode `Roots`
- **WHEN** the model calls `shell_execute` with a path argument outside those roots
- **THEN** authorization returns `Denied` with reason `shell_path_outside_trusted_roots`

#### Scenario: The feed sync state stays write-protected

- **GIVEN** an interactive Personal session with a configured server feed and a grant for anywhere for `echo` and `rm`
- **WHEN** the model calls `shell_execute` with `echo x > <feed sync state>` or `rm <feed sync state>`, or calls `file_write` on that file
- **THEN** authorization returns `Denied`, and the shell redirect gets reason `shell_path_protected`
- **AND** `file_read` on that file is not denied

### Requirement: TA-16 Operators manage grants through the CLI and doctor

The `netclaw approvals` command SHALL list, add, and revoke grants per audience
and tool. `list` SHALL show token-prefix, legacy-exact, and repository grants
with distinct labels. `trust-verb` SHALL accept one complete static shell
phrase and SHALL reject a flag, a redirect, an assignment, a dynamic command
name, and a compound command. `revoke` SHALL reject an ambiguous
cross-audience form. The command SHALL fail closed on an invalid store.

`netclaw doctor` SHALL warn when `shell_execute` is in `Approval` mode but the
shell mode is `Off`, when shell grants exist but shell is disabled, and when
Personal sets `shell_execute` to `Auto` while the host shell is enabled. It
SHALL report an error for an unrestricted Public or Team profile.

`netclaw doctor` SHALL warn when an audience profile can write wider than it
can read. The warning SHALL name the audience and the `WriteFiles` and
`ReadFiles` modes. It SHALL NOT offer an auto-fix, because a narrow read
profile can be intentional. `ToolAudienceProfilesDoctorCheck` owns the
check. It reads the bound `Tools` section only. "Wider" means one of these:

- write mode `All` and read mode `Roots` or `None`
- write mode `Roots` and read mode `None`
- write mode `Roots` and read mode `Roots`, and a write root that no read root
  equals or contains, by a lexical comparison of the configured text

For `Personal`, the warning also says that Personal file reads already reach
the write profile at runtime.

#### Scenario: trust-verb rejects a compound command

- **WHEN** the operator runs `netclaw approvals trust-verb "git status && rm x" --shell bash`
- **THEN** the command fails and stores nothing

#### Scenario: Doctor warns about an explicit Personal shell Auto

- **GIVEN** `Tools.ShellMode` `HostAllowed` and a Personal `shell_execute` override of `Auto`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** the audience profile check reports a warning

#### Scenario: Doctor warns when a profile writes wider than it reads

- **GIVEN** a Personal profile with `ReadFiles` mode `Roots` and `WriteFiles`
  mode `All`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** the audience profile check reports a warning that names `Personal`,
  `WriteFiles.Mode=All`, and `ReadFiles.Mode=Roots`
- **AND** `netclaw doctor --fix` does not change the profile

#### Scenario: Doctor does not warn when reads cover writes

- **GIVEN** each profile has a read reach that covers its write reach (for
  example, the posture defaults, or read `Roots` `[A]` and write `Roots`
  `[A/sub]`)
- **WHEN** the operator runs `netclaw doctor`
- **THEN** the audience profile check reports no write-wider-than-read warning

### Requirement: TA-8 Every candidate needs coverage

A call SHALL run without a prompt only when every candidate has
[coverage](../../../docs/spec/GLOSSARY.md#coverage). Coverage sources
SHALL be:

- a one-time consent for the exact blocked call (the retry passes every check
  again);
- a chat grant of the same session, or of a parent session for a subagent;
- a persistent grant: global (any directory), folder (the candidate's real
  scope is inside the folder, with no link below the grant root), or
  repository;
- [reviewed-safe policy](../../../docs/spec/GLOSSARY.md#reviewed-safe-policy),
  only for a catalog phrase, and only when the path rule below permits every
  known path. Under decision D1 it also covers an unknown operand value;
- under decision D1, a grant for anywhere for an exact candidate whose only
  unknown part is an operand;
- an approval-exempt data command with no directory scope and no assignment
  digest, while the store is available: an output command (`echo`,
  `printf`, `:`, `true`, `false`), or in Bash a test builtin (`test`, `[`);
- a Bash command that runs no program, while the store is available (owner
  decision, 2026-10-07): a command with only redirects (`> file`), or a data
  command, when each redirect target is one proved plain file. A data command
  that a shell-state assignment reaches SHALL qualify only when each operand
  is proved data. Each redirect SHALL get the decision of the file tool for
  the audience and the path: `file_write` for a write target, `file_read` for
  an input redirect (`<`). The shell trust zone SHALL judge each target, and
  the `file_read` path rules SHALL also judge an input target. A refused
  target, a target that is not proved, and a `Deny` mode of the file tool
  SHALL deny the call (`shell_path_protected`,
  `shell_path_outside_trusted_roots`, `shell_redirect_read_denied`,
  `shell_redirect_unproved`, `shell_redirect_file_tool_denied`). Each redirect
  of the command SHALL get these checks before any prompt. With an `Approval`
  mode of the file tool, a stored grant of that tool SHALL cover the redirect.
  With no such grant, one exact candidate SHALL name each write and read that
  needs consent, with `Once` and `Deny` only. Otherwise the candidate
  SHALL NOT ask for a stored grant, and an answer SHALL NOT save one. Such a
  command SHALL get no managed temporary directory advice. A Bash source that
  parses with no command (an assignment, a comment, an empty `case`, an
  empty subshell) SHALL be allowed after the screens. A target below `/dev/` other than `/dev/null`, a glob, or a
  set of values SHALL make a data command one exact candidate.

The path rule for reviewed-safe policy SHALL NOT depend on the run (D2):

- A known path SHALL qualify when the audience profile lets a file tool read
  it (`ReadFiles`). The path SHALL be a host path of the shell's own style.
  Netclaw SHALL apply protection to the lexical path and to the link-resolved
  path, and a protected path SHALL never qualify. Such a path SHALL NOT need
  a project declaration for a reviewed phrase.
- Otherwise a known path SHALL qualify only inside the session and project
  roots.

A grant SHALL apply only to the audience and the tool that it names. A grant
for `shell_execute` SHALL NOT authorize another tool. A chat grant of one
session SHALL NOT cover another session. A global grant SHALL cover a phrase
in any directory. A folder grant SHALL NOT cover a candidate outside its
folder, and a new global grant SHALL NOT remove a folder grant.

A shell grant SHALL cover a candidate's command words by one reach rule
(owner decision, 2026-10-05). The approval matcher and the store hygiene SHALL
use the same rule:

- A grant of two or more words names a verb. It SHALL cover each candidate
  whose command words start with the grant words. The later words are the
  arguments of the verb.
- A grant of one word names only the program. It SHALL cover a candidate
  only when the candidate has that one command word. Policy data that gives a
  program a one-token chain (`echo`, `which`, `jq`) keeps its exception.
- A word of the grant SHALL never be free. A candidate with fewer words, or
  with another word in a grant position, SHALL need separate coverage.
- A grant with no words SHALL cover nothing.
- A token-prefix grant (`TokenPrefix`) and a legacy exact-phrase grant
  (`LegacyExact`) SHALL use the same rule. The words of a legacy grant are the
  space-separated words of its phrase. The display text of the prompt SHALL
  NOT count.
- The rule SHALL use no command-specific knowledge: no option tables, no
  per-command lists, and no executable grammar.
- A new grant SHALL save the command words of the approved candidate. The
  reach rule SHALL NOT change what a grant saves.

A word after the verb slot that names a link in the occurrence directory SHALL
stay a command word. The protected-path screen SHALL check each plain word
after the program word, command word or argument. When the word names a link
in the occurrence directory and the resolved link target is protected, the
call SHALL be denied before grant lookup, also under a grant. The link check
SHALL NOT change folder, repository, or global grant coverage, so a link to an
ordinary file SHALL keep the decision of its grant.

The store SHALL NOT save a grant that a stored grant already covers, and
`netclaw doctor --fix` SHALL remove such a grant. A grant covers another grant
when the tool, the shell, and the assignment digest are equal, its words cover
the other words by the reach rule, and it applies anywhere or has the same
scope. A removal SHALL NOT change an allowed decision.

A repository grant SHALL cover a candidate only when the candidate resolves to
an ordinary checkout or a Git-registered linked worktree of the same Git
common directory. Netclaw SHALL resolve that identity from disk at each check.
A repository grant for repository A SHALL NOT cover repository B. Netclaw SHALL
reject copied `.git` pointers, moved worktrees, external links, and a main
checkout that uses `--separate-git-dir`.

A non-shell tool SHALL have one candidate: its tool name, or a path-scoped
name for a control-plane write.

Owner: `ReviewedSafeShellPolicy` and `PathAccessPolicy.IsReadableByAudience`
own the reviewed-safe path rule; their result is call-local.
`ApprovalPatternMatching` owns the grant match, including the legacy rule; its
result is call-local. `ToolApprovalActor` holds chat grants (actor-local).
`ToolApprovalStore` holds persistent grants (durable).

#### Scenario: A command that runs no program needs no prompt

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants
- **WHEN** the model calls `shell_execute` with `printf 'a\tb\n' > drafts-harvest.tsv && : > drafts-harvest.json` (catalog case `no-program-harvest-drafts-allows`), `> drafts.json`, or `x=1`
- **THEN** authorization returns `Allowed` with no grant lookup
- **AND** an unattended call returns the same result

#### Scenario: A command that runs no program cannot reach a refused path

- **GIVEN** an interactive Personal session on the Bash 5.2 host
- **WHEN** the model calls `shell_execute` with `: > <config dir>/secrets.json` or `echo x > <config dir>/tool-approvals.json`
- **THEN** authorization returns `Denied`
- **AND** with bounded read and write roots, `: < <outside>/notes.txt` returns `Denied` (issue #2554: a Personal read reaches the write profile, so the shell trust zone refuses the path first with `shell_path_outside_trusted_roots`; the screen keeps the `shell_redirect_read_denied` check)
- **AND** with bounded read roots and the write profile `All`, `: < <outside>/notes.txt` returns `Allowed`
- **AND** `date > out.txt` and `echo $(rm -rf build) > out.txt` still prompt for `date` and `rm`

#### Scenario: A redirect gets the consent mode of its file tool

- **GIVEN** an interactive Personal session on the Bash 5.2 host
- **WHEN** `file_write` has mode `Deny` and the model calls `shell_execute` with `echo x > out.txt`
- **THEN** authorization returns `Denied` with reason `shell_redirect_file_tool_denied`
- **AND** with mode `Approval`, the prompt shows the one candidate `write <project>/out.txt` and offers `Once` and `Deny` only
- **AND** with mode `Approval`, an unattended call returns `Denied` with reason `approval_required_unattended`
- **AND** with mode `Approval` and a chat grant for `file_write`, the call returns `Allowed`
- **AND** with `file_read` = `Approval` and `file_write` = `Deny`, `: < notes.txt > out.txt` returns `Denied`

#### Scenario: Folder grant stays inside its folder

- **GIVEN** a persistent folder grant for `git status` in `/work/a`
- **WHEN** the model calls `shell_execute` with `git status` in `/work/b`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: A safe phrase reads a path outside the project in an interactive run

- **GIVEN** an interactive Personal session with the default profile and no grants (catalog case `safe-verb-external-allows`)
- **WHEN** the model calls `shell_execute` with `git status` in a directory outside the project
- **THEN** authorization returns `Allowed` with allow reason `ReviewedSafePolicy`

#### Scenario: A safe phrase does not leave the trusted roots in an unattended run

- **GIVEN** an unattended Personal run in Approval mode and no grant (catalog case `unattended-external-reviewed-safe-allows`)
- **WHEN** the model calls `shell_execute` with `git ls-tree feature` in an external directory that the profile may read
- **THEN** authorization returns `Allowed` with allow reason `ReviewedSafePolicy`, as in a chat (D2)
- **AND** under a bounded profile that cannot read the directory, the call is denied

#### Scenario: A legacy grant covers its own command words

- **GIVEN** a Personal `LegacyExact` grant for `dotnet list package`
- **WHEN** the model calls `shell_execute` with `dotnet list package --vulnerable --include-transitive`
- **THEN** the grant covers the candidate, although the prompt text is `dotnet list`

#### Scenario: A legacy grant does not cover other words

- **GIVEN** a Personal `LegacyExact` grant for `git push upstream`
- **WHEN** the model calls `shell_execute` with `git push origin main`
- **THEN** authorization returns `RequiresApproval`
- **AND** the grant covers `git push upstream feature-x`

#### Scenario: A verb grant covers its arguments

- **GIVEN** a Personal global grant for `git push`, or a `LegacyExact` grant for `git push`
- **WHEN** the model calls `shell_execute` with `git push upstream` or `git push origin main`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`
- **AND** a global grant for `dotnet package search` covers `dotnet package search Dapper.AOT`

#### Scenario: A grant word is never free

- **GIVEN** a Personal global grant for `git push upstream`, and another for `git push origin feature-x`
- **WHEN** the model calls `shell_execute` with `git push origin main`
- **THEN** authorization returns `RequiresApproval`
- **AND** the `git push upstream` grant covers `git push upstream feature-x`

#### Scenario: A program-only grant stays exact

- **GIVEN** a Personal global grant for `gh`, saved from `gh --help`
- **WHEN** the model calls `shell_execute` with `gh auth logout`
- **THEN** authorization returns `RequiresApproval`
- **AND** the grant covers `gh --version`

#### Scenario: A verb grant does not hide a link target

- **GIVEN** a Personal grant for `git add`, a link `keylink`, and a link `keys2` in the project directory, both to the protected keys directory
- **WHEN** the model calls `shell_execute` with `git add keylink` or `git add keys2` (a word with a digit is an argument) in the project directory
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`, attended or not
- **AND** the same grant covers `git add README`

#### Scenario: A folder grant covers a link to a file in its folder

- **GIVEN** a Personal folder grant for `mytool write` in the project directory, and a link `readmelink` in that directory to `README.md` in the same directory
- **WHEN** the model calls `shell_execute` with `mytool write readmelink` in the project directory
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`, in an attended and in an unattended run

#### Scenario: Store hygiene uses the reach rule

- **GIVEN** a stored global grant for `git push` and a stored global grant for `gh`
- **WHEN** Netclaw saves a folder grant for `git push upstream`, and a global grant for `gh auth logout`
- **THEN** the store skips the `git push upstream` grant, and saves the `gh auth logout` grant
- **AND** `netclaw doctor --fix` removes a stored `git push upstream feature-x` grant and keeps a stored `gh auth status` grant

#### Scenario: Repository grant covers a registered sibling worktree

- **GIVEN** a repository grant for `./scripts/bump.sh` created in worktree `w1` of repository `r`
- **WHEN** the model calls the same command in registered worktree `w2` of `r`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: Repository grant does not cover another repository

- **GIVEN** a repository grant for `./scripts/bump.sh` in repository `r`
- **WHEN** the model calls the same command in unrelated repository `q`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: Grant of another audience does not cover

- **GIVEN** a persistent Team grant for `git status`
- **WHEN** a Personal session calls `shell_execute` with `git status`
- **THEN** the Team grant does not cover the candidate

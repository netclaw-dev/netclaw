## MODIFIED Requirements

### Requirement: TA-6 Path access decisions own file-tool authority

A file tool SHALL get its filesystem authority only from a path access
decision for its exact file operation (`Read`, `Write`, `Attach`, or
`DeclareProjectScope`). A shell path SHALL use the `Write` operation. The
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
- Protection SHALL depend on the operation. The
  [ordinary configuration](../../../docs/spec/GLOSSARY.md#ordinary-configuration)
  files `netclaw.json` and the grant store `tool-approvals.json` SHALL be
  readable by a file tool (owner decision D6). Secrets (`secrets.json`), keys,
  webhook secrets, `daemon.env`, device state, bootstrap state, the hard-deny
  override file, the database, and process-control files SHALL be
  read-denied. The config directory, secrets, keys, the database, process
  control files, system skills, and server feeds SHALL be write-denied.
- Shell text that names the config directory, secrets, webhooks, keys, the
  database, or process-control files SHALL be denied. This includes
  `netclaw.json` and `tool-approvals.json`, because shell text cannot show a
  read from a write. The agent reads these files with `file_read`.
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
- A readable `netclaw.json` or `tool-approvals.json` SHALL NOT imply write,
  edit, attach, or shell authority. Secret values SHALL live only in protected
  stores.

Owner: `PathAccessPolicy` owns the path access decision, and its result is
call-local. `ToolPathPolicy` owns protection and the D5 glob match
(`GlobMayReachDeniedPath`, with the segment match in `ShellGlobScope`). Its
read, write, and shell lists come from `DaemonToolPathPolicyFactory` and are
process-local. No state of these checks is durable.

#### Scenario: Ordinary config is readable but not by shell text

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_read` on `netclaw.json` or on `tool-approvals.json`
- **THEN** the path access decision allows the read
- **AND** a `shell_execute` call with `cat <config dir>/netclaw.json` is denied with `shell_references_protected_path`

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
- **AND** `cat ~/.netclaw/*/secrets.json` and `cat ~/.netclaw/*/tool-approvals.json` are denied with the same reason

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

### Requirement: TA-7 Shell analysis uses general syntax facts

Netclaw SHALL analyze a shell call with ShellSyntaxTree for the native shell
of the daemon: Bash on Linux and macOS, and the probed PowerShell dialect
(7.x or Windows PowerShell 5.1) on Windows. All stages of one call SHALL share
one analysis. The terms [candidate](../../../docs/spec/GLOSSARY.md#candidate),
[phrase](../../../docs/spec/GLOSSARY.md#phrase), and
[unresolved syntax](../../../docs/spec/GLOSSARY.md#unresolved-syntax)
have their glossary meaning.

- Netclaw SHALL derive one candidate from each complete command occurrence.
  Pipelines, lists, loops, and same-language nested shells SHALL NOT hide an
  occurrence. A cross-language payload (for example `pwsh -Command` under
  Bash) SHALL stay an argument of the host command.
- In Bash, each command inside `if`, `case`, `while`, `until`, or a
  background list (`server &`) SHALL get its own candidate and its own
  decision.
- Candidate identity SHALL use the static verb tokens that the parser gives.
  Netclaw SHALL NOT parse the private subcommands, options, or operands of an
  executable. Safe-verb lists and deny lists are policy data.
- `Exact` and `FiniteSet` effective path values and authored filesystem
  values SHALL enter path policy. `AuthoredPathShape` alone SHALL NOT create
  filesystem authority.
- Non-path operands: an absolute word below a top-level directory that does
  not exist on the host names no file. It SHALL have no path scope, and its
  candidate SHALL use the working directory. An example is the API route in
  `gh api /repos/o/r/actions/jobs/1/logs`. Netclaw SHALL probe the top-level
  directory only when the working directory exists on the host. A top-level
  link or a probe failure SHALL keep the path scope. A URL SHALL have no path
  scope. A grant keeps its folder and repository scope (owner decision D3).
- A path word with a control character, for example multi-line `python3 -c`
  code, SHALL NOT make the call unresolved. Its scope SHALL be the deepest
  ancestor directory of its text before the first control character. The
  scope SHALL hold no control character, so the prompt can show it.
- Glob path facts (Bash 5.2 host): the scope of a glob word SHALL be its
  covering directory. Netclaw SHALL walk the directories that each segment
  can match, to a bound of 4096 directories, and every match SHALL stay inside
  the covering directory. A link that leaves the covering directory, or a walk
  that fails, SHALL keep the candidate exact with `Once` and `Deny` only. A
  glob with a wildcard in a directory segment (`*/notes.md`) can expand to an
  option word, so decision D1 SHALL apply to it. A leaf glob (`*.cs`) SHALL
  keep its path rule.
- Variable values: a word that reads a proved variable value SHALL get the
  decision of the literal value, in path policy and in hard deny (TA-5). A
  name with a run-time value (`PID=$!`, `x=$(cmd)`, `read x`) SHALL be
  unknown. A command that reads it as a word SHALL be one exact candidate with
  `Once` and `Deny` only, and SHALL get no rewrite advice.
- An unresolved command (a dynamic command name, an unknown value, an
  unresolved path or redirect, a command after an unproved directory change
  such as `cd "$x"`, `pushd`, `popd`, or a failed `cd`) SHALL produce one
  exact candidate: its source text, with no reusable grant. In a Bash session,
  attended or not, each other command of the call SHALL keep its own
  candidates and coverage.
- Bracket-word rule: a program word that is a literal bracket pattern (for
  example `["ci","build"]`), with no command words and no other word except a
  redirect, SHALL be unresolved, because Bash expands the pattern. Brace text
  and regex text in the program word (`{"b":2}`, `^\d{4}$`) SHALL NOT be
  unresolved by this rule; they keep the rewrite advice of a command with
  unknown command words. The `[` test builtin (`[ -d /work ]`) SHALL stay an
  ordinary command.
- A source that does not split into commands (incomplete control flow, a
  command-resolution mutation such as `alias` or `hash`, `&&` under Windows
  PowerShell 5.1, unresolved PowerShell syntax) SHALL allow only a one-time
  consent for the whole call.
- Data-position rule: in Bash, a dynamic operand of an output command
  (`echo`, `printf`, `:`, `true`, `false`) SHALL be data, not unresolved
  syntax. A `printf` operand SHALL be data only after a literal format, and
  `printf -v` SHALL stay unresolved. A command substitution inside the operand
  SHALL be its own command with its own candidate, and a redirect target SHALL
  keep its own check. PowerShell SHALL keep only the bare `$?` rule.
- Owner decision D1: a command whose command words are known and whose only
  unknown part is an operand value SHALL be covered by a reviewed safe phrase
  or by a grant for anywhere, attended or unattended (D2). A folder,
  repository, or chat grant SHALL NOT cover it. An unknown program word, an
  unknown redirect target, and a link SHALL keep the prompt; an unattended
  run SHALL deny it with `approval_required_unattended`.
- Accepted D1 gap: an unknown operand value can name a path that the
  protected-path text screen cannot see. A grant for anywhere already lets a
  literal operand name any readable path, so the gap adds only this case.
- A bounded assignment fact SHALL qualify a reusable grant with a SHA-256
  digest of the canonical assignment facts. A changed assignment SHALL need
  separate authority. An assignment inside an opaque fallback wrapper SHALL
  stay one-time only.
- A working directory with a `..` segment SHALL be denied with
  `shell_invalid_working_directory`.
- An internal exception, an impossible state, or an inconsistent actor result
  SHALL deny with `internal_policy_failure`.

Owner: `ShellCommandAnalysis` and `ShellApprovalMatcher` own the syntax facts,
the scopes, and the candidates. `ShellPolicyCoordinator` and
`ReviewedSafeShellPolicy` own the D1 coverage rule. All of this state is
call-local. The analysis keeps no state between calls.

#### Scenario: Compound command keeps every occurrence

- **GIVEN** an interactive Personal session with no grants
- **WHEN** the model calls `shell_execute` with `git status && npm test`
- **THEN** authorization returns `RequiresApproval`
- **AND** the candidates contain `git status` and `npm test` as separate phrases

#### Scenario: Each command in a control-flow construct gets its own decision

- **GIVEN** an interactive Personal session with no grants (catalog cases `if-statement-prompts-for-each-command` and `case-statement-uses-reviewed-phrases`)
- **WHEN** the model calls `shell_execute` with `if test -f marker; then git push; else git fetch; fi`
- **THEN** authorization returns `RequiresApproval` with the candidates `test`, `git push`, and `git fetch`
- **AND** `case x in a) cat a.txt ;; *) cat b.txt ;; esac` returns `Allowed` with allow reason `ReviewedSafePolicy`

#### Scenario: A command substitution is its own command

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `echo $(git push)` (catalog case `command-substitution-fails-closed`)
- **THEN** authorization returns `RequiresApproval` with the candidate `git push`
- **AND** the `echo` operand is data, so `echo` needs no grant

#### Scenario: A dynamic redirect target is not data

- **GIVEN** an interactive Personal session with grants for anywhere for `git status` and `printf` (catalog case `bash-substitution-redirect-target-fails-closed`)
- **WHEN** the model calls `shell_execute` with `git status > "$(printf result.log)"`
- **THEN** authorization returns `RequiresApproval` with the exact candidate `git status > "$(printf result.log)"`
- **AND** the prompt offers only `Once` and `Deny`

#### Scenario: An API route uses the folder grant of the working directory

- **GIVEN** an interactive Personal session with a folder grant for `gh api` in the project directory (catalog case `api-route-word-uses-project-folder-grant`)
- **WHEN** the model calls `shell_execute` with `gh api /repos/o/r/actions/jobs/1/logs` in the project directory
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`

#### Scenario: A word below an existing top-level directory keeps its path scope

- **GIVEN** an interactive Personal session with a folder grant for `gh api` in the project directory
- **WHEN** the model calls `shell_execute` with `gh api /usr/share/doc` in the project directory
- **THEN** the word keeps its path scope below `/usr`, because `/usr` exists on the host
- **AND** the folder grant does not cover the candidate

#### Scenario: A multi-line operand uses the scope of its clean text

- **GIVEN** an interactive Personal session with a folder grant for `python3` in the project directory (catalog case `multi-line-inline-code-uses-folder-grant`)
- **WHEN** the model calls `shell_execute` with multi-line `python3 -c` code in the project directory
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`
- **AND** without the grant, the prompt offers the reusable candidate `python3` (catalog case `multi-line-inline-code-offers-reusable-grant`)

#### Scenario: A glob that leaves through a link keeps one-time options

- **GIVEN** an interactive Personal session with a grant for anywhere for `cat` (catalog case `glob-intermediate-symlink-scope-fails-closed`)
- **AND** a directory under `artifacts` that is a link to a directory outside the covering directory
- **WHEN** the model calls `shell_execute` with `cat artifacts/*/secret.txt`
- **THEN** authorization returns `RequiresApproval` with one exact candidate and only `Once` and `Deny`

#### Scenario: D1 applies to a glob with a wildcard directory segment

- **GIVEN** an interactive Personal session (catalog cases `glob-that-may-add-option-uses-global-grant`, `glob-that-may-add-option-prompts-with-folder-grant`, and `glob-that-may-add-option-unattended-uses-global-grant`)
- **WHEN** the model calls `shell_execute` with `rm */stale.tmp`
- **THEN** a grant for anywhere for `rm` allows the call
- **AND** a folder grant for `rm` gives a prompt with the exact candidate `rm */stale.tmp`
- **AND** an unattended call with the grant for anywhere is allowed, as in a chat (D2)

#### Scenario: A run-time value gets a one-time prompt

- **GIVEN** an interactive Personal session with a grant for anywhere for `kill` (catalog case `background-process-id-kill-prompts`)
- **WHEN** the model calls `shell_execute` with `server & PID=$!; kill "$PID"`
- **THEN** authorization returns `RequiresApproval` with the candidates `server` and the exact `kill "$PID"`

#### Scenario: A bound value is not covered by a grant for another value

- **GIVEN** an interactive Personal session with a grant for anywhere for `git push origin feature-x` (catalog case `assigned-branch-is-not-covered-by-another-branch-grant`)
- **WHEN** the model calls `shell_execute` with `b=main; git push origin "$b"`
- **THEN** authorization returns `RequiresApproval`

#### Scenario: An unknown operand under a grant for anywhere

- **GIVEN** an interactive Personal session with a grant for anywhere for `kubectl get pods`
- **WHEN** the model calls `shell_execute` with `kubectl get pods -l "app=$(whoami)"` (catalog case `unknown-operand-global-grant-allows`)
- **THEN** authorization returns `Allowed`
- **AND** the same call with a folder grant prompts with one exact candidate and only `Once` and `Deny`

#### Scenario: A bracket program word stays unresolved

- **GIVEN** an unattended Personal session with no grants (catalog case `unattended-bracket-program-word-denies`)
- **WHEN** the model calls `shell_execute` with `["ci","build"]`
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** in an interactive session, `["batch one"]` gives a prompt with only `Once` and `Deny` (catalog case `bracket-program-word-with-space-stays-unresolved`)

#### Scenario: A brace program word keeps its rewrite advice

- **GIVEN** an unattended Personal session in Approval mode with no grants (catalog case `unattended-brace-program-word-gets-rewrite-advice`)
- **WHEN** the model calls `shell_execute` with `{"b":2,"nested":{"c":3}}`
- **THEN** authorization returns `RequiresAgentCorrection`
- **AND** the bracket-word rule does not deny the call

#### Scenario: Unresolved syntax in a headless run

- **GIVEN** a headless Personal session with `shell_execute` in `Approval` mode
- **WHEN** the model calls `shell_execute` with `cat "$FILE"`
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** no prompt is shown

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
- an approval-exempt output command (`echo`, `printf`, `:`, `true`, `false`)
  with no directory scope and no assignment digest, while the store is
  available.

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

A legacy exact-phrase grant (`LegacyExact`) SHALL cover a candidate when its
phrase equals all command words of the candidate. The display text of the
prompt SHALL NOT count. A candidate with more words or other words SHALL need
separate coverage.

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

- **GIVEN** a Personal `LegacyExact` grant for `git merge-base`
- **WHEN** the model calls `shell_execute` with `git merge-base dev`
- **THEN** authorization returns `RequiresApproval`

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

### Requirement: TA-9 Agent correction precedes a prompt and grants no authority

Agent correction SHALL run only after admission, hard deny, protected-path, and
shell analysis checks pass. It SHALL return `RequiresAgentCorrection` with one
or more typed corrections and SHALL NOT run the tool, show a prompt, or create
a grant. The model's replacement call SHALL start a new authorization attempt
and pass every check again.

- A shell call that runs one exact native-tool executable SHALL receive a
  native-tool correction before stored grants.
- A Personal call, attended or unattended, that authors a write under the
  platform temporary root SHALL receive a managed temporary directory
  correction when the ordinary result would ask for consent. Team and Public SHALL NOT receive
  the managed path.
- An exact leading Bash directory change for project work SHALL receive a
  one-call working-directory correction that does not rewrite the command.
- Corrections SHALL precede an `Auto` allow. Temporary and project advice
  SHALL keep stored-grant and one-time precedence.
- A repeated equivalent call after a managed temporary correction SHALL
  suppress the correction once and SHALL offer only `Once` and `Deny`.
- The parent session and a subagent SHALL use the same corrections.

#### Scenario: Temporary write gets a correction, not a prompt

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `file_write` on a path under the platform temporary root
- **THEN** authorization returns `RequiresAgentCorrection` with a managed temporary directory correction
- **AND** no prompt is shown

#### Scenario: Hard deny wins over a correction

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with a hard-denied command that also writes under the temporary root
- **THEN** authorization returns `Denied`

#### Scenario: Retry after a correction asks with one-time options only

- **GIVEN** an armed managed temporary correction for an exact call
- **WHEN** the model repeats the same call
- **THEN** authorization returns `RequiresApproval`
- **AND** the prompt offers only `Once` and `Deny`

### Requirement: TA-10 Consent prompts offer only safe options

A consent request SHALL carry the tool name, a display text with secrets
removed, the requester, the candidates, the working directory, the offered
options, and the authorization attempt identifier. It SHALL NOT carry a
`DirectoryRoots` field.

The consent request SHALL also carry adopted-context provenance.
`HasAdoptedContext` SHALL be true for any non-empty adopted window. The
adopted speakers SHALL list every adopted sender, including the requester.
`HasThirdPartyAdoptedContext` SHALL be a separate flag and SHALL NOT trim that
list. Adopted context SHALL stay quoted background and SHALL NOT originate a
consent request.

The options SHALL come from this set, in this order, with these stable keys
and labels:

| Key | Label |
|---|---|
| `approve_once` | Once |
| `approve_session` | This chat |
| `approve_always` | Always here |
| `approve_repository` | This repository |
| `approve_everywhere` | Always anywhere (Always allow this tool for an MCP tool) |
| `deny` | Deny |

- The prompt SHALL offer only `Once` and `Deny` when any uncovered
  candidate has unresolved syntax or no reusable phrase, or when the call is
  a managed temporary retry.
- `Always here` SHALL be offered only for a shell call with a directory scope
  that is not shallow and not session-owned.
- `This repository` SHALL be offered only for a clean reusable shell phrase
  whose candidates all resolve to one Git common directory.
- An assignment-qualified prompt SHALL use the versioned keys
  `approve_assignment_{session,always,repository,everywhere}_v1` for reusable
  options. `Once` and `Deny` keep their keys.
- Labels SHALL fit in 76 characters. `Always anywhere` and `Deny` SHALL have
  danger styling where the channel supports it.
- Only the requester SHALL answer, unless the principal is verified
  automation. Netclaw SHALL reject an option that the request did not offer.
- A channel type that supports interactive approval (Slack, Discord,
  Mattermost, TUI, SignalR) SHALL render the options and a text fallback. A
  channel that cannot post a prompt SHALL answer `Deny` for that call.
- Decision D2: when nobody can answer (headless chat, a reminder, a webhook,
  or a sub-agent with no approval bridge), the authorizer SHALL turn a consent
  request into the denial `approval_required_unattended` and SHALL NOT
  prompt. The tool result SHALL say that nobody can answer a prompt in an
  unattended run and SHALL tell the agent to save an "Always" grant in a chat
  with the same audience. A stored grant that covers the call SHALL still
  allow it. No reason code `channel_does_not_support_approval` exists.
- After a person approves a prompt, the tool result that the model reads
  SHALL end with one line that names the
  [consent answer](../../../docs/spec/GLOSSARY.md#consent-answer):
  `[approval: once]`, `[approval: this chat only]`,
  `[approval: always in this folder]`, `[approval: always in this repo]`, or
  `[approval: always anywhere]`. The parent session and a subagent retry
  SHALL add the same line. A denial SHALL add no line. A call that a stored
  grant or a policy allows SHALL get no line.

Owner: `ToolAccessPolicy.BuildApprovalOptions` owns the options, and the
result is call-local. `ConsentAnswerCodec.AppendResultNote` owns the approval
note line. `SessionToolExecutionPipeline` and `SubAgentActor` add the line to
the call-local tool result. The journal keeps the consent request and the
answer (durable).

#### Scenario: Unresolved syntax offers one-time options only

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with a command that has unresolved syntax
- **THEN** the prompt offers `Once` and `Deny` only

#### Scenario: Wrong requester cannot answer

- **GIVEN** a consent request from requester `U1`
- **WHEN** user `U2` selects `approve_once`
- **THEN** Netclaw rejects the answer and posts a warning
- **AND** the call does not run

#### Scenario: Headless run cannot ask

- **GIVEN** a headless Personal session with `shell_execute` in `Approval` mode
- **WHEN** the model calls `shell_execute` with an uncovered command
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** the same command with a covering stored grant is allowed

#### Scenario: MCP tool prompt has no folder option

- **GIVEN** an MCP tool in `Approval` mode
- **WHEN** the prompt is built
- **THEN** it does not offer `Always here`
- **AND** its global option has the label `Always allow this tool`

#### Scenario: Self-only adopted window keeps its provenance

- **GIVEN** a turn whose adopted window holds only earlier messages of the requester
- **WHEN** a tool call in that turn asks for consent
- **THEN** the consent request has `HasAdoptedContext` true and lists the requester as an adopted speaker
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: An approved result names the choice

- **GIVEN** an interactive Personal session and a `shell_execute` prompt
- **WHEN** the requester selects `This chat`
- **THEN** the tool result that the model reads ends with `[approval: this chat only]`

#### Scenario: A denied or pre-approved call has no approval note

- **GIVEN** an interactive Personal session
- **WHEN** the requester selects `Deny`, or a stored grant allows the call with no prompt
- **THEN** the tool result has no `[approval: ...]` line

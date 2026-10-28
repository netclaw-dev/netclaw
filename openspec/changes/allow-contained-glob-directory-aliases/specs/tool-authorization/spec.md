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
- Owner decision D5: a shell glob word that can match a protected shell
  path, the default credential store (`~/.netclaw/keys`,
  `~/.netclaw/config/secrets.json`), or a directory that contains one, SHALL
  get the literal-path denial (`shell_references_protected_path`).
  The authored pattern SHALL retain its lexical segment check, including dot
  entries, bracket expressions, and case.
- Bounded glob alias reach: Netclaw SHALL inspect directory aliases under the
  TA-7 bounds. The protected-path screen SHALL check each known target and
  the remaining pattern below that target before reusable approval.
  A later incomplete branch SHALL NOT remove a known protected target.
  A protected target SHALL retain its denial when another branch fails,
  leaves the boundary, or exhausts a bound.
  This rule SHALL NOT claim protection for arbitrary recursive program behavior.
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

#### Scenario: A contained alias target remains protected

- **GIVEN** a bounded glob with a matched directory alias inside its covering directory
- **AND** the target or its remaining pattern reaches a protected path
- **WHEN** Netclaw evaluates the call with a global grant
- **THEN** the protected-path screen denies the call before grant coverage

#### Scenario: An incomplete branch cannot erase known protected reach

- **GIVEN** a bounded glob whose first alias reaches a protected path
- **AND** another branch has a broken link or exceeds the walk bound
- **WHEN** Netclaw evaluates the call
- **THEN** the known protected target retains its terminal denial


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
- Glob path facts (Bash 5.2 host): the scope of a glob word SHALL include
  its covering directory and every known alias target scope.
  Netclaw SHALL walk only the directory branches that each segment can match.
  Reusable reach SHALL require a count of 4096 work units or less.
  Each intermediate directory visit and each link hop SHALL consume one unit.
  Ordinary directory-entry scans are outside this count.
  Every target and residual match SHALL stay inside the original covering boundary.
  Only a matched intermediate directory alias MAY use that original boundary.
  The final target of every other link SHALL retain its immediate directory containment rule.
  A pattern with a trailing slash SHALL retain its directory-only restriction
  when Netclaw computes known target reach.
  A contained directory alias SHALL be reusable only after complete target,
  protected-path, and grant-scope proof.
  The walk SHALL retain the existing link-hop bound.
  A cycle, broken link, unknown target, inspection failure, outside target, or
  exhausted bound SHALL keep reusable scope unresolved.
  In Personal Approval mode, unresolved reach alone SHALL NOT support reusable approval.
  Existing hard and protected-path rules SHALL still apply before exact consent.
  These rules include R13 denial when link resolution fails.
  If those rules permit consent, the call SHALL retain exact `Once` and `Deny` choices.
  Explicit Auto consent SHALL retain its existing behavior and SHALL NOT
  bypass hard or protected-path denials.
  Incomplete reach SHALL NOT support reusable grants or reviewed-safe coverage.
  A repeated physical directory SHALL NOT suppress a different remaining pattern.
  A glob with a wildcard in a directory segment (`*/notes.md`) can expand to an
  option word, so decision D1 SHALL apply to it. A leaf glob (`*.cs`) SHALL
  keep its path rule.
- Variable values: a word that reads a proved variable value SHALL get the
  decision of the literal value, in path policy and in hard deny (TA-5). A
  name with a run-time value (`PID=$!`, `x=$(cmd)`, `read x`) SHALL be
  unknown. A command that reads it as a word SHALL be one exact candidate with
  `Once` and `Deny` only, and SHALL get no rewrite advice, except the quote
  correction below for a command with known command words. The data-position
  rule below is the exception: an `echo` or `printf` operand that reads it
  SHALL be data.
- An unresolved command (a dynamic command name, an unknown value, an
  unresolved path or redirect, a command after an unproved directory change
  such as `cd "$x"`, `pushd`, `popd`, or a failed `cd`) SHALL produce one
  exact candidate: its source text, with no reusable grant. In a Bash session,
  attended or not, each other command of the call SHALL keep its own
  candidates and coverage.
- Scope-free data commands (0.27.1, owner finding F2): a Bash data command
  with no redirect and with proved data operands SHALL keep its normal
  candidate after an unproved directory change, when the analysis proves the
  rest of the command. Such a command has no path scope, so the directory
  cannot change what it reaches, and it keeps its approval exemption (TA-8).
  A data command with a redirect, or with an operand that is not proved data
  (an unquoted `echo $n`), SHALL stay one exact candidate.
- Bracket-word rule: a program word that is a literal bracket pattern (for
  example `["ci","build"]`), with no command words and no other word except a
  redirect, SHALL be unresolved, because Bash expands the pattern. Regex text
  in the program word (`^\d{4}$`) SHALL NOT be unresolved by this rule; it
  keeps the rewrite advice of a command with unknown command words. Since
  ShellSyntaxTree 0.4.0-beta.18, the parser rejects a brace list in the
  program word (`{"b":2,"c":3}`), because Bash expands it to another program
  and its operands, so the source is unresolved. This rule SHALL NOT apply to the `[` test builtin
  (`[ -d /work ]`); the data-position rule below applies to it.
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
- Test builtins: in Bash, `test` and `[` SHALL be data commands. Each operand
  SHALL be data only when the parser proves an exact value or a finite set
  and no value has a `[`. Bash evaluates an array subscript in a `-v` operand
  as arithmetic, and the arithmetic runs a command substitution. Thus an operand with a `[`, an
  unknown value, or a glob or file-name value SHALL make the command one exact
  candidate. Netclaw SHALL NOT parse the test operators. A path operand of a
  test builtin SHALL NOT be a scope. The protected-path screen SHALL still
  deny a literal or proved protected path.
- A Bash data command with no redirect SHALL get no assignment digest only
  when each operand is proved data. An output operand SHALL be proved data
  with an exact value, a finite set, a proved authored value with no glob
  character, or a word that Bash cannot glob (`MayPathnameExpand` is false,
  from ShellSyntaxTree 0.4.0-beta.19). A test operand SHALL need an
  exact value or a finite set with no `[`. Any other data command, and a data
  command with a redirect, SHALL keep its digest. Thus
  `n=$(cmd); echo "$n"` is data, and `d=key; echo ../netclaw/"${d}s"/*` needs
  consent, because its literal twin is denied.
- `continue`, `break`, `exit`, and `return` SHALL be Bash data commands.
  They change only which statement runs next. ShellSyntaxTree 0.4.0-beta.18
  parses `break` and `continue` with no operand or one decimal level, and
  `exit` and `return` with no operand or one bounded status. It joins the
  flow state at each one. Other forms stay unresolved. A redirect or a
  substitution keeps its own check. Thus `cd x || exit 1; ls` needs no grant
  for `exit`.
- Arithmetic (ShellSyntaxTree 0.4.0-beta.18): a bounded `$((...))` SHALL be
  data. Arithmetic that reads a command substitution or a variable without a
  proved integer value, and an arithmetic command `((...))`, SHALL be
  unresolved, because Bash evaluates those values as code.
- ANSI-C words (ShellSyntaxTree 0.4.0-beta.19): a `$'...'` word SHALL get the
  decision of its decoded text. Each proved path value SHALL also get the
  default credential store text hints, so `cat ~/.netclaw/$'\x6beys'/key-1.xml`
  is denied as its literal twin.
- Pathname expansion (ShellSyntaxTree 0.4.0-beta.19): a Bash operand that
  Bash can glob (`MayPathnameExpand`), whose value is unknown, and whose
  authored value is not proved free of glob characters SHALL make its command
  one exact candidate with `Once` and `Deny` only. Decision D1 SHALL NOT cover
  it, and an unattended run SHALL deny it. A brace word such as
  `~/.netclaw/{keys,config}/key-1.xml` is such an operand. A proved glob scope
  keeps decision D5, and `$?` is exempt. Owner decision (#2349): an operand of
  a Bash data command keeps its earlier rule. An `echo` or `printf` operand
  can only print file names, never contents, and a test operand keeps the
  proved-value rule above.
- Rewrite exception: when the pathname-expansion rule is the only cause that
  makes a command exact, and a rewrite of the command words can remove the
  word, the call SHALL get the rewrite correction, attended or unattended.
  The command SHALL stay exact, so no grant and no reviewed phrase covers it.
  The call does not run, and the rewritten call passes normal approval. A
  run-time value in the verb slot, as in `f=$(date); cat /work/$f`, gives
  unknown command words and has no literal spelling, so that command SHALL
  keep its prompt or its unattended denial.
- Quote correction (0.27.1, owner finding F4): when the command words are
  known and the pathname-expansion rule is the only cause that makes the
  command exact, the call SHALL get a quote correction that names each such
  word, attended or unattended. The call does not run. In double quotes, the
  word gets no pathname expansion and no field splitting, so the retry has one
  unknown operand, and decision D1 applies. The rule SHALL read only general
  shell facts, never the grammar of a program. An unknown program word,
  unknown command words, and any other cause SHALL keep their handling.
- The pathname-expansion rule SHALL NOT read `MayFieldSplit`. A word that can
  split but cannot glob is a quoted `"$@"` or a bounded arithmetic word.
  Splitting only cuts a value into more words, and each word keeps the check
  of a normal operand: an unknown value gets decision D1. The agent cannot set
  `$@` without consent, because `set --` needs consent and a function
  definition fails closed.
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
- **THEN** authorization returns `RequiresApproval` with the candidates `git push` and `git fetch`
- **AND** `test -f marker` is a data command, so it needs no grant
- **AND** `case x in a) cat a.txt ;; *) cat b.txt ;; esac` returns `Allowed` with allow reason `ReviewedSafePolicy`

#### Scenario: A command substitution is its own command

- **GIVEN** an interactive Personal session
- **WHEN** the model calls `shell_execute` with `echo $(git push)` (catalog case `command-substitution-fails-closed`)
- **THEN** authorization returns `RequiresApproval` with the candidate `git push`
- **AND** the `echo` operand is data, so `echo` needs no grant

#### Scenario: A test builtin with bounded operands needs no approval

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog cases `test-builtin-literal-operands-allows`, `test-builtin-bounded-variable-allows`, and `test-builtin-loop-value-allows`)
- **WHEN** the model calls `shell_execute` with `x=3; [ "$x" -gt 2 ] && echo yes`
- **THEN** authorization returns `Allowed` with allow reason `ApprovalExemptShellCandidates`
- **AND** `for d in a b; do [ "$d" = a ] && echo yes; done` returns the same result with no rewrite advice

#### Scenario: A test operand with a subscript is not data

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog case `test-builtin-subscript-operand-prompts`)
- **WHEN** the model calls `shell_execute` with `[ -v 'a[$(printf marker >&2)]' ]`
- **THEN** authorization returns `RequiresApproval` with that exact candidate
- **AND** a run-time value such as `n=$(cmd); [ -v "$n" ]` gets the same result (catalog case `test-builtin-unknown-value-prompts`)

#### Scenario: A test builtin does not hide another command

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog cases `test-builtin-guard-keeps-action-prompt`, `test-builtin-guard-keeps-hard-deny`, and `test-builtin-credential-path-denies`)
- **WHEN** the model calls `shell_execute` with `[ 3 -gt 2 ] && git push`
- **THEN** authorization returns `RequiresApproval` with the candidate `git push`
- **AND** `x=3; [ "$x" -gt 2 ] && rm -rf /` is denied with `hard_deny_system_destructive`
- **AND** `[ -f ~/.netclaw/keys/x ] && echo yes` is denied with `shell_references_protected_path`

#### Scenario: A run-time value in an output operand is data

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants (catalog case `echo-substitution-value-is-data`)
- **WHEN** the model calls `shell_execute` with `n=$(git push); echo "$n"; printf '%s\n' "$n"`
- **THEN** authorization returns `RequiresApproval` with the candidate `git push` only

#### Scenario: An unquoted glob built from a variable needs consent

- **GIVEN** a Personal session on the Bash 5.2 host with no grants (catalog cases `output-glob-from-binding-prompts` and `output-glob-from-binding-unattended-denies`)
- **WHEN** the model calls `shell_execute` with `d=key; echo ../netclaw/"${d}s"/*`
- **THEN** an interactive call returns `RequiresApproval` with the one exact candidate `echo ../netclaw/"${d}s"/*` (ShellSyntaxTree 0.4.0-beta.19 reports that the word can glob)
- **AND** an unattended call is denied with `approval_required_unattended`

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

The scenario name is historical. Since ShellSyntaxTree 0.4.0-beta.18, the
parser rejects the brace word, so the call gets no rewrite advice.

- **GIVEN** an unattended Personal session in Approval mode with no grants (catalog case `unattended-brace-program-word-denies`)
- **WHEN** the model calls `shell_execute` with `{"b":2,"nested":{"c":3}}`
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** the call does not run

#### Scenario: A bounded arithmetic expansion is data

- **GIVEN** an interactive Personal session with no grants (catalog case `arithmetic-expansion-is-data`)
- **WHEN** the model calls `shell_execute` with `echo $((1 + 2))`
- **THEN** authorization returns `Allowed`, because `echo` only prints its operands

#### Scenario: Arithmetic that can run code stays unresolved

- **GIVEN** an interactive Personal session with no grants (catalog cases `arithmetic-expansion-fails-closed`, `arithmetic-unproved-read-fails-closed`, and `arithmetic-command-fails-closed`)
- **WHEN** the model calls `shell_execute` with `echo $(( $(id) + 1 ))`, `echo $((count + 1))`, or `(( p = 0 ))`
- **THEN** authorization returns `RequiresApproval` for unresolved syntax, with `Once` and `Deny` only

#### Scenario: A decoded ANSI-C path gets the decision of its literal twin

- **GIVEN** an interactive Personal session with a global `cat` grant (catalog case `ansi-c-credential-keys-denied-as-literal`)
- **WHEN** the model calls `shell_execute` with `cat ~/.netclaw/$'\x6beys'/key-1.xml`
- **THEN** authorization returns `Denied` with reason `shell_references_protected_path`

#### Scenario: A word that can glob to an unproved path needs exact consent

The scenario name is historical. The brace word is the only cause that makes
the command exact, so the call gets the rewrite correction.

- **GIVEN** a Personal session with a global `cat` grant (catalog cases `brace-credential-keys-gets-rewrite-correction` and `unattended-brace-credential-keys-gets-rewrite-correction`)
- **WHEN** the model calls `shell_execute` with `cat ~/.netclaw/{keys,config}/key-1.xml`
- **THEN** an interactive run and an unattended run return `RequiresAgentCorrection`
- **AND** the call does not run, and the `cat` grant does not cover it
- **AND** `f=$(date); cat /work/$f` with the same grant returns `RequiresApproval` with the one exact candidate `cat /work/$f` (catalog case `unknown-glob-word-read-needs-exact-consent`)
- **AND** an unattended run of that call returns `Denied` with reason `approval_required_unattended` (catalog case `unattended-unknown-glob-word-read-denies`)

#### Scenario: A control-transfer builtin needs no grant

- **GIVEN** an unattended Personal session with grants for anywhere for `cd` and `make` (catalog case `unattended-cd-or-exit-grant-allows`)
- **WHEN** the model calls `shell_execute` with `cd /netclaw-approval-external/cd-list || exit 1; make`
- **THEN** authorization returns `Allowed` with allow reason `StoredApproval`
- **AND** `exit` needs no grant

#### Scenario: A quoted unknown output part is data

- **GIVEN** an interactive Personal session with no grants (catalog cases `quoted-unknown-output-part-is-data` and `unknown-glob-word-output-keeps-glob-rule`)
- **WHEN** the model calls `shell_execute` with `d=$(date); echo pre"$d"`
- **THEN** authorization returns `Allowed`
- **AND** `d=$(date); echo "${d}ret"/*` returns `RequiresApproval` with the one exact candidate `echo "${d}ret"/*`

#### Scenario: Unresolved syntax in a headless run

- **GIVEN** a headless Personal session with `shell_execute` in `Approval` mode
- **WHEN** the model calls `shell_execute` with `cat "$FILE"`
- **THEN** authorization returns `Denied` with reason `approval_required_unattended`
- **AND** no prompt is shown

#### Scenario: A known-words command with an unquoted expansion gets a quote correction

- **GIVEN** a Personal session with grants for anywhere for `git rev-list` and `git branch` (catalog cases `substitution-word-with-known-words-gets-quote-correction`, `unattended-substitution-word-with-known-words-gets-quote-correction`, and `quoted-substitution-word-uses-verb-grant`)
- **WHEN** the model calls `shell_execute` with `git rev-list --left-right --count HEAD...origin/$(git branch --show-current) 2>/dev/null`
- **THEN** an interactive run and an unattended run return `RequiresAgentCorrection`, and the correction names the word `HEAD...origin/$(git branch --show-current)`
- **AND** the call does not run
- **AND** the quoted retry `git rev-list --left-right --count "HEAD...origin/$(git branch --show-current)" 2>/dev/null` returns `Allowed` with allow reason `StoredApproval`
- **AND** `$(date) rev-list HEAD...origin/$(git branch --show-current)` keeps one exact answer for the call (catalog case `unknown-program-word-with-glob-word-keeps-prompt`)
- **AND** `f=$(date); git log origin/$f > "$f".log` returns `RequiresApproval` with the one exact candidate `git log origin/$f > "$f".log` (catalog case `glob-word-with-unknown-redirect-keeps-prompt`)

#### Scenario: A data command after an unproved directory change keeps its exemption

- **GIVEN** a Personal session with grants for anywhere for `cd` and `git fetch` (catalog cases `data-commands-after-failing-cd-are-exempt` and `unattended-data-commands-after-failing-cd-are-exempt`)
- **WHEN** the model calls `shell_execute` with `cd sub && n=$(git fetch) && git fetch "$n"; echo "---"; echo "== $n =="; [ 3 -gt 2 ]`
- **THEN** an interactive run and an unattended run return `Allowed` with allow reason `StoredApproval`
- **AND** `cd sub && n=$(git fetch) && git fetch "$n"; echo "---" > /netclaw-approval-external/marker` returns `RequiresApproval` with the one exact candidate `echo "---" > /netclaw-approval-external/marker` (catalog case `data-command-redirect-after-failing-cd-keeps-prompt`)
- **AND** `cd sub && n=$(git fetch) && git fetch "$n"; echo $n` returns `RequiresApproval` with the one exact candidate `echo $n` (catalog case `unquoted-unknown-echo-after-failing-cd-keeps-prompt`)

#### Scenario: The live CPM survey prompts only for its unscoped reads

- **GIVEN** an interactive Personal session with grants for anywhere for `cd`, `find`, and `grep` (catalog case `live-cpm-props-survey-prompts-only-for-unscoped-reads`)
- **WHEN** the model calls `shell_execute` with the survey command of the F2 report, which runs `echo`, `find`, and `grep` after a `cd` that can fail
- **THEN** authorization returns `RequiresApproval`
- **AND** the candidates are only the `find` and `grep` commands whose directory is not known
- **AND** no `echo` command is a candidate

#### Scenario: A contained directory alias supports the existing diagnostic policy

- **GIVEN** the Bash 5.2 host with a proved HOME and existing read authority
- **AND** `repositories/alias` names `repositories/owner/project` inside the covering directory
- **AND** every reachable target passes protection and the applicable authority rules
- **WHEN** Netclaw evaluates `ls -d ~/repositories/*/ ~/repositories/*/*/ 2>/dev/null | head -60`
- **THEN** the existing reviewed diagnostic policy can permit the complete call

#### Scenario: A leaf alias cannot transfer scope to a sibling directory

- **GIVEN** a Personal Approval-mode folder grant for `touch` covers `<P>`
- **AND** `<P>/a/alias.txt` links to `<P>/b/item.txt`
- **WHEN** the user calls `touch '<P>'/*/*.txt`
- **THEN** the call requires exact consent because the leaf target leaves its immediate directory
- **AND** the same-directory leaf alias retains its existing grant behavior

#### Scenario: A leaf chain can return within the original covering boundary

- **GIVEN** a Personal Approval-mode folder grant for `touch` covers `<P>`
- **AND** `<P>/a/alias.txt` links through `<P>/.bridge/hop.txt` to `<P>/a/item.txt`
- **WHEN** the user calls `touch '<P>'/a*/*.txt`
- **THEN** the existing grant covers the call because every hop stays inside `<P>`
- **AND** the final leaf target stays inside its immediate directory `<P>/a`

#### Scenario: A cycle retains the literal failed-resolution denial

- **GIVEN** `<P>/alias` links to itself
- **WHEN** the user calls `touch '<P>'/a*/*.txt`
- **THEN** the protected-path screen denies the call with `shell_references_protected_path`
- **AND** the equivalent literal path also denies under R13
- **AND** the denial does not assert that the cycle reveals a credential path

#### Scenario: An outside alias stays unresolved under a global grant

- **GIVEN** a bounded glob with a matched directory alias outside the original covering directory
- **WHEN** Netclaw evaluates the call in Personal Approval mode with a global grant
- **THEN** the grant does not replace the missing bounded scope proof
- **AND** an interactive call retains exact consent unless a prior screen denies it
- **AND** an unattended call denies the unresolved call


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
  With no such grant, each file operation that needs consent SHALL have its own exact candidate.
  The prompt SHALL offer `Once` and `Deny` only. Otherwise the candidate
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

For a bounded glob that crosses a directory alias, reusable approval SHALL
require existing grant coverage for the authored covering scope and every target scope.
Each grant SHALL retain its ordinary folder or repository membership rules.
A folder grant SHALL NOT cover an outside target. A repository grant SHALL prove each target's
repository identity through the existing checkout and registered-worktree rules.
The same rule SHALL apply to argument and redirect targets.
Reviewed diagnostic policy SHALL check read authority for every known target scope.
Only complete bounded reach SHALL support reusable scope or reviewed-safe coverage.
The resolved path view SHALL retain those target scopes for later path checks.

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

Scratch composition SHALL apply only to a Personal Bash occurrence with resolved command candidates and proved redirects.
The occurrence SHALL have at least one file target inside the current run's managed temporary directory.
Every other file target SHALL stay inside that directory or name the null device.
Every proved scratch target SHALL pass the file-tool authority checks.
An ineligible occurrence SHALL retain its ordinary command and redirect scopes.
The command grant SHALL still cover the occurrence directory and every ordinary operand scope.
The redirect target SHALL remain subject to protected-path checks, file-tool admission, consent modes, and launch verification.
A redirect through a link, an unresolved target, or a target outside that managed directory SHALL keep its ordinary scope.
This composition SHALL NOT change any persistent grant or extend a repository's identity.

#### Scenario: Repository command writes managed scratch
- **GIVEN** a Personal session with a repository grant for `uv export` and file-write permission for its managed temporary directory
- **WHEN** the command runs in that repository and redirects to a proved file in that managed directory
- **THEN** the command runs without another prompt
- **AND** an ordinary operand outside the repository still needs its own coverage

#### Scenario: Scratch composition cannot bypass file consent
- **GIVEN** the same command and a file-write mode of `Deny`
- **WHEN** the command redirects to its managed temporary directory
- **THEN** Netclaw denies the call before a shell grant can cover it
- **AND** an `Approval` file-write mode needs file-tool consent or a covering file-tool grant

#### Scenario: Exact scratch consent preserves command and file identities
- **GIVEN** a scratch redirect that needs file consent
- **WHEN** the operator selects `Once`
- **THEN** the unchanged retry is allowed
- **AND** a different command or file-operation set still needs consent
- **AND** punctuation in one filename cannot authorize two different targets

#### Scenario: Another run's scratch is external
- **WHEN** a repository command redirects to another run's temporary directory or an arbitrary external file
- **THEN** the repository grant supplies no scratch exemption

#### Scenario: A command that runs no program needs no prompt

- **GIVEN** an interactive Personal session on the Bash 5.2 host with no grants
- **WHEN** the model calls `shell_execute` with `printf 'a\tb\n' > drafts-harvest.tsv && : > drafts-harvest.json` (catalog case `no-program-harvest-drafts-allows`), `> drafts.json`, or `x=1`
- **THEN** authorization returns `Allowed` with no grant lookup
- **AND** an unattended call returns the same result

#### Scenario: A command that runs no program cannot reach a refused path

- **GIVEN** an interactive Personal session on the Bash 5.2 host
- **WHEN** the model calls `shell_execute` with `: > <config dir>/secrets.json` or `echo x > <config dir>/tool-approvals.json`
- **THEN** authorization returns `Denied`
- **AND** with bounded read roots, `: < <outside>/notes.txt` returns `Denied` with reason `shell_redirect_read_denied`
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

#### Scenario: A folder grant does not transfer through an alias

- **GIVEN** a folder grant for the authored project scope
- **AND** a glob alias target does not belong to that grant's scope
- **WHEN** Netclaw checks the bounded glob
- **THEN** the folder grant does not cover the call

#### Scenario: A repository grant proves the target identity

- **GIVEN** a grant for repository A
- **AND** a glob alias reaches repository B inside a shared covering directory
- **WHEN** Netclaw checks the bounded glob
- **THEN** repository A's grant does not cover repository B

## MODIFIED Requirements

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
  `Once` and `Deny` only, and SHALL get no rewrite advice. The data-position
  rule below is the exception: an `echo` or `printf` operand that reads it
  SHALL be data.
- An unresolved command (a dynamic command name, an unknown value, an
  unresolved path or redirect, a command after an unproved directory change
  such as `cd "$x"`, `pushd`, `popd`, or a failed `cd`) SHALL produce one
  exact candidate: its source text, with no reusable grant. In a Bash session,
  attended or not, each other command of the call SHALL keep its own
  candidates and coverage.
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
  run-time value, as in `f=$(date); cat /work/$f`, has no literal spelling, so
  that command SHALL keep its prompt or its unattended denial.
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

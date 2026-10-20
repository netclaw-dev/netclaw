## MODIFIED Requirements

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
- Corrections SHALL precede an `Auto` allow. Temporary advice SHALL keep
  stored-grant and one-time precedence.
- A repeated equivalent call after a managed temporary correction SHALL
  suppress the correction once and SHALL offer only `Once` and `Deny`.
- The parent session and a subagent SHALL use the same corrections.

A resolved Personal Bash command that runs a program in a project SHALL receive temporary-path advice for proved platform-temporary outputs outside the project.
The advice SHALL preserve the command directory and ordinary operands.
A command that runs no program and an output inside the project SHALL retain their ordinary file-consent behavior.
It SHALL name the current run's managed temporary directory and require a new call.
An input redirect, an unresolved target, or a non-temporary output target SHALL prevent this specific advice.
Hard deny and protected-path refusal SHALL retain precedence.

#### Scenario: Repository export receives temporary output advice
- **GIVEN** a Personal session with a project directory
- **WHEN** `uv export` redirects output to `/tmp/reqs.txt` and otherwise needs approval
- **THEN** Netclaw suggests the current run's managed temporary directory
- **AND** Netclaw does not run or rewrite the submitted call

#### Scenario: Temporary advice preserves an external input
- **WHEN** the command also reads through an input redirect
- **THEN** Netclaw does not suggest relocation through this rule

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

#### Scenario: A reviewed phrase in a readable folder gets no project correction

- **GIVEN** a Personal session, attended or unattended, with no grants
- **AND** the shell folder is readable by the audience but is not the declared project directory
- **WHEN** the model calls `shell_execute` with a reviewed phrase, for example `git status`, in that folder
- **THEN** authorization returns `Allowed` through reviewed-safe coverage
- **AND** the result has no correction that asks for `set_working_directory`

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
- A shell consent request SHALL always name what it asks for (owner decision,
  2026-10-07). When it has no candidate and no pattern (a source that does not parse, an
  unknown program word such as `$cmd > x`), its one display candidate SHALL be
  the full command text, and it SHALL offer only `Once` and `Deny`. The
  one-time key SHALL NOT read the display list, so a `Once` answer still
  covers the retry.
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

A reusable shell consent prompt SHALL explain the reach of its command words within the existing display limit.
The full command SHALL take priority over optional explanations when both cannot fit.
A one-word program grant SHALL state that it does not cover subcommands, except for existing reviewed policy exceptions.
A prompt with a proved grant mismatch SHALL give a bounded explanation for the command words, assignment qualifier, shell, or directory mismatch.
This text SHALL NOT change the grant words, saved scope, one-time identity, or offered option keys.

#### Scenario: Prompt explains a program grant
- **WHEN** a reusable prompt asks for the one-word program `uv`
- **THEN** the prompt states that this grant does not cover subcommands

#### Scenario: Prompt explains a near miss
- **GIVEN** a stored grant whose command words do not cover the submitted call
- **WHEN** the call needs consent
- **THEN** the prompt explains the command-word mismatch without changing the saved grant

#### Scenario: A prompt for an unknown program shows the command text

- **GIVEN** an interactive Personal session on the Bash 5.2 host
- **WHEN** the model calls `shell_execute` with `$cmd > drafts.txt`
- **THEN** the prompt shows `$cmd > drafts.txt` as its one candidate and offers `Once` and `Deny` only
- **AND** a `Once` answer allows the retry of the same call

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

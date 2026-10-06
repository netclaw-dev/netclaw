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
  `printf`, `:`, `true`, `false`), or in Bash a test builtin (`test`, `[`).

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

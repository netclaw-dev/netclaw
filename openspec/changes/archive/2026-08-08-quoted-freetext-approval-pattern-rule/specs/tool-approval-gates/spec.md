## MODIFIED Requirements

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands
using tokenization. The verb chain SHALL consist of non-flag tokens from
the start of the command until the first flag (`-`), path, URL, or
call-specific value argument. A token SHALL be classified as a
call-specific value iff it is not a flag, not path-shaped, and contains
a digit — one morphological rule, not a taxonomy of value shapes.
Extraction is greedy: bare-word operands that are neither flags, paths,
URLs, nor digit-bearing values (all-alpha subcommands, remote names,
branch names, refs) SHALL remain in the verb chain — the extractor SHALL
NOT attempt to distinguish all-alpha subcommands from positional
operands.

> **Why digit-bearing tokens are excluded:** Tokens containing digits
> (`123`, `8080`, `0.4.2`, `v0.4.2`, `aa211dcb`, `feature2`) are
> overwhelmingly call-specific values — ticket IDs, ports, timeouts,
> versions, SHAs, refs — that vary between invocations of the same verb
> chain. Baking them into the pattern produces overly-specific approval
> entries that do not generalize: `git tag v0.4.2` vs `git tag v0.5.0`
> would create two unrelated entries, forcing separate approval for each
> release. This generalizes the earlier bare-integer rule (issue #1331).
> All-alpha operands are intentionally NOT classified: no shape rule can
> distinguish a branch name (`dev`) from a subcommand (`worktree`), and
> mis-stripping a subcommand would silently widen a grant. Flags are
> exempt (`-3`, `--max-count=10` carry invocation intent); path-shaped
> tokens are exempt so digit-bearing paths still reach directory scoping.

Where greedy extraction has folded a trailing value token into the verb
chain (e.g. `git tag v0.4.2`, where `v0.4.2` is lowercase-leading and
therefore verb-like to the parser), the system SHALL trim trailing
call-specific value tokens from the chain, always retaining at least the
command word. Trimming SHALL be trailing-only: mid-chain digit-bearing
tokens (`aws s3 ls`) SHALL NOT be removed. Trimming SHALL apply
identically on the gate (candidate) path and the persisted/display
pattern path so the two normalize to the same verb chain.

A multi-line argument — one containing an embedded line break (LF or
CR), which can only arise inside quoting — SHALL also terminate pattern
extraction, excluding the argument and everything after it (issue
#1402). Multi-line quoted strings are call-specific content (message
bodies, inline scripts) that varies between invocations of the same
verb chain, and an embedded line break corrupts the stored pattern's
display and the approval store's formatting; a lone CR additionally
permits cursor-repositioning spoofing in terminal-rendered prompts. A
preceding flag (e.g. `--message`) SHALL be retained in the pattern — it
carries invocation intent. The same termination rule SHALL apply to
redirect targets: a quoted redirect target carrying an embedded line
break (e.g. `>> "$LOGDIR⏎file"`) terminates the redirect walk so the
break never reaches the stored pattern.

A single-line quoted argument whose decoded text holds internal
whitespace SHALL also terminate pattern extraction, excluding the
argument and everything after it (issue #1406). A multi-word quoted
operand — a commit message, a ticket body, a search string — is
call-specific content that varies between invocations of the same verb
chain, so it produces overly-specific approval entries that do not
generalize: every `git commit -m "new message"` would mint a new
pattern and re-prompt. A single-word quoted argument holds no internal
whitespace and SHALL NOT terminate extraction, so a quoted and an
unquoted single token (`git commit -m "fix"` and `git commit -m fix`)
normalize to the same pattern. A path-shaped argument (`IsPath = true`)
SHALL be exempt, so a quoted path that holds whitespace still reaches
directory scoping. A preceding flag (e.g. `--message`) SHALL be retained
because it carries invocation intent. This rule normalizes the stored
and display pattern only; it SHALL NOT change the live authorization
decision, the persisted `(verb, directory)` grant, or the verbatim
command shown at the prompt. The rule SHALL apply identically on the
gate (candidate) path and the persisted/display pattern path.

For shell approval units, `&&`, `||`, and `;` SHALL split into separate
units, while `|` SHALL remain inside the current unit. For `bash -c` or
`sh -c` wrappers, the inner command SHALL be extracted and scanned
recursively.

When `ShellTokenizer.SplitCompoundCommand` detects bash control-flow
tokens or unbalanced quotes/brackets, it SHALL return an empty
verb-chain list. The approval gate SHALL then offer only `Once` and
`Deny`. See the "Pattern extraction refuses bash control-flow"
requirement for details.

The matcher SHALL operate on `ApprovalEntry` records keyed by
`(verb, directory)`. The "is this string a verb chain or a directory
root?" inspection logic of v1 SHALL NOT be present in the v2 matcher.

Approval persistence SHALL store one `ApprovalEntry` per extracted verb
chain. Compound commands SHALL produce N entries from one user click on
`Always here` or `Always anywhere`.

#### Scenario: Verb chain extracted from simple command

- **GIVEN** the command `git push origin main`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `git push origin main`
- **AND** the all-alpha operands `origin` and `main` remain in the verb
  chain because greedy extraction does not strip positional operands

#### Scenario: Verb chain strips bare integer positional argument

- **GIVEN** the command `freshdesk ticket get 123`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `freshdesk ticket get`
- **AND** the digit-bearing token `123` is excluded because it is
  call-specific

#### Scenario: Verb chain generalizes across different integer values

- **GIVEN** commands `nc host 8080` and `nc host 9090`
- **WHEN** patterns are extracted for both
- **THEN** both produce the same pattern `nc host`
- **AND** approval granted for one integer value covers all values of the same verb chain

#### Scenario: Verb chain terminates at value token (not just skips it)

- **GIVEN** the command `timeout 30 curl http://example.com`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `timeout`
- **AND** everything after the value token (including wrapped subcommands like `curl`) is dropped from the pattern

#### Scenario: Digit-bearing operand terminates the pattern

- **GIVEN** the command `docker run --name test123 --port=8080`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `docker run --name`
- **AND** the digit-bearing operand `test123` and everything after it are
  excluded because digit-bearing non-flag, non-path tokens are
  call-specific values
- **AND** the flag `--name` is retained because flags are exempt from
  value classification

#### Scenario: Version arguments normalize to one verb chain regardless of prefix

- **GIVEN** commands `git tag v0.4.2` and `git tag 0.4.2`
- **WHEN** candidate verbs and patterns are extracted for both
- **THEN** both produce the verb chain `git tag`
- **AND** a standing `git tag` grant auto-approves both forms
- **AND** the lowercase-leading form is handled by trimming the trailing
  value token the greedy walk folded into the chain

#### Scenario: Multi-line quoted argument terminates the pattern

- **GIVEN** the command `freshdesk ticket reply --message "Hi,⏎Thanks."`
  where the quoted argument spans two lines
- **WHEN** the pattern is extracted
- **THEN** the pattern is `freshdesk ticket reply --message`
- **AND** the multi-line body and everything after it are excluded
  because multi-line arguments are call-specific content
- **AND** the flag `--message` is retained because flags carry
  invocation intent

#### Scenario: Single-line quoted free-text argument terminates the pattern

- **GIVEN** the command `git commit -m "fix the bug"`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `git commit -m`
- **AND** the multi-word quoted body and everything after it are excluded
  because a quoted argument with internal whitespace is call-specific
  content (issue #1406)
- **AND** the flag `-m` is retained because flags carry invocation intent

#### Scenario: Multi-word quoted operands generalize across values

- **GIVEN** commands `git commit -m "first message"` and
  `git commit -m "second message"`
- **WHEN** patterns are extracted for both
- **THEN** both produce the same pattern `git commit -m`
- **AND** one `git commit -m` grant covers every commit message

#### Scenario: Single-word quoted argument is not dropped

- **GIVEN** the commands `git commit -m fix` and `git commit -m "fix"`
- **WHEN** patterns are extracted for both
- **THEN** both produce the same pattern `git commit -m fix`
- **AND** the single-word quoted token is retained because it holds no
  internal whitespace

#### Scenario: Quoted path with whitespace keeps directory scoping

- **GIVEN** the command `cat "my file.txt"`
- **WHEN** the candidate is extracted
- **THEN** the quoted path is exempt from the free-text rule because it
  is path-shaped (`IsPath = true`)
- **AND** the directory of `my file.txt` still reaches directory scoping

#### Scenario: Digit-bearing ref folded into the chain is trimmed

- **GIVEN** the command `git show aa211dcb`
- **WHEN** the candidate verb is extracted
- **THEN** the verb chain is `git show`
- **AND** the alpha-leading SHA normalizes the same way as a
  digit-leading SHA (`git show 1234abcd`)

#### Scenario: Trailing-only trim never removes mid-chain tokens

- **GIVEN** the command `aws s3 ls`
- **WHEN** the candidate verb is extracted
- **THEN** the verb chain is `aws s3 ls`
- **AND** the mid-chain digit-bearing token `s3` is untouched because
  only trailing value tokens are trimmed

#### Scenario: Verb chain stops at flag

- **GIVEN** the command `ls -la /tmp`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `ls`
- **AND** the flag and path are not part of the persisted verb chain

#### Scenario: Multi-level verb chain

- **GIVEN** the command `docker compose up -d`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `docker compose up`

#### Scenario: Control operators create separate approval units

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** `git add`, `git commit`, and `git push` are checked as
  separate approval units against the v2 matcher

#### Scenario: Compound segments batched in one prompt

- **GIVEN** none of `git add`, `git commit`, `git push` are approved
- **WHEN** the command `git add . && git commit -m "fix" && git push`
  is checked
- **THEN** a single approval prompt lists all three verbs as bullets
- **AND** one click on `Always here` persists three `(verb, cwd)` entries

#### Scenario: bash -c inner command scanned recursively

- **GIVEN** the command `bash -c "git push --force"`
- **WHEN** approval and hard deny are checked
- **THEN** the inner command `git push --force` is extracted and scanned
- **AND** verb chain `git push` is checked through the v2 matcher

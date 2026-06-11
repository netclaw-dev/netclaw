# Delta: tool-approval-gates — shell approval value-token normalization

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

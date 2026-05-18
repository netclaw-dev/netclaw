## MODIFIED Requirements

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands
using tokenization. The verb chain SHALL consist of non-flag tokens from
the start of the command until the first flag (`-`), path, or URL
argument. Extraction is greedy: bare-word operands that are neither flags,
paths, nor URLs (subcommands, remote names, branch names, refs) SHALL
remain in the verb chain — the extractor SHALL NOT attempt to distinguish
subcommands from positional operands. For shell approval units, `&&`,
`||`, and `;` SHALL split into separate units, while `|` SHALL remain
inside the current unit. For `bash -c` or `sh -c` wrappers, the inner
command SHALL be extracted and scanned recursively.

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
- **AND** the bare-word operands `origin` and `main` remain in the verb
  chain because greedy extraction does not strip positional operands

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

## ADDED Requirements

### Requirement: Global grant precedence over folder-scoped grants

A persisted global `ApprovalEntry` (`directory: null`) SHALL authorize its
verb in every directory. When both a global entry and one or more
folder-scoped entries exist for the same verb within the same audience and
tool, the global entry SHALL be sufficient for approval in any directory;
the folder-scoped entries become redundant for matching but SHALL be
retained on disk. Adding a global grant SHALL NOT remove, supersede, or
rewrite existing folder-scoped grants for the same verb — retaining them
preserves the operator's ability to revoke the global grant and fall back
to the narrower folder-scoped grants.

The matcher SHALL evaluate a candidate against every persisted
`ApprovalEntry` for the verb and approve when any entry matches. It SHALL
NOT stop at the first verb-matching entry whose directory check fails.

#### Scenario: Global grant approves verb in an unrelated directory

- **GIVEN** `tool-approvals.json` contains both
  `{"verb":"dotnet","directory":"~/repos/foo/"}` and
  `{"verb":"dotnet","directory":null}`
- **WHEN** the agent invokes `dotnet --info` with cwd `~/repos/bar/`
- **THEN** the matcher returns approved via the global entry
- **AND** no prompt is rendered

#### Scenario: Adding a global grant retains folder-scoped grants

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"dotnet","directory":"~/repos/foo/"}`
- **WHEN** the user clicks `Always anywhere` for verb `dotnet`
- **THEN** `tool-approvals.json` contains both the existing folder-scoped
  entry and a new `{"verb":"dotnet","directory":null}` entry
- **AND** the folder-scoped entry is NOT removed or rewritten

#### Scenario: Revoking a global grant restores folder-scoped scope

- **GIVEN** `tool-approvals.json` contains both
  `{"verb":"dotnet","directory":"~/repos/foo/"}` and
  `{"verb":"dotnet","directory":null}`
- **WHEN** an operator removes the `{"verb":"dotnet","directory":null}`
  entry via `netclaw approvals revoke`
- **THEN** `dotnet` invocations with cwd under `~/repos/foo/` still
  auto-approve via the retained folder-scoped entry
- **AND** `dotnet` invocations outside `~/repos/foo/` prompt again

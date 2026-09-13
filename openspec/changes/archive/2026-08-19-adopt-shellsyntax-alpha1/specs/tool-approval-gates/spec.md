## ADDED Requirements

### Requirement: Bounded Bash stdin data has a constrained receiver grammar

Netclaw SHALL treat Bash heredoc and here-string data as resolved only when all
required receiver and data facts are complete. The initial receiver grammar
SHALL accept only argument-free `cat`. It SHALL require a complete literal
heredoc or an exact or finite here-string target.

The grammar SHALL use the heredoc expansion mode and authored body provenance.
It SHALL use `RedirectAnalysis.Target` for here strings. It SHALL reject
expanding heredocs, unknown domains, incomplete redirects, path-relevant
redirects, non-stdin source descriptors, authored arguments, receiver wrappers,
and every other receiver. A complete direct shell dispatch MAY expose its inner
receiver through Netclaw's established recursive analysis.

Netclaw SHALL evaluate every other redirect on the occurrence independently.
Stored approval SHALL NOT bypass an unresolved stdin redirect.

#### Scenario: Exact here string to cat can use the trusted scope

- **GIVEN** an argument-free `cat` command in a trusted project directory
- **WHEN** its complete here string has exact data
- **THEN** the stdin redirect does not require a separate approval
- **AND** the normal safe-verb and path rules decide the command

#### Scenario: Literal heredoc to cat can use the trusted scope

- **GIVEN** an argument-free `cat` command in a trusted project directory
- **WHEN** its complete literal heredoc has complete authored body provenance
- **THEN** the stdin redirect does not require a separate approval
- **AND** the normal safe-verb and path rules decide the command

#### Scenario: Unknown here-string data stays strict

- **GIVEN** `cat <<< "$value"` in a trusted project directory
- **WHEN** the parser cannot prove the data value
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent approval candidate

#### Scenario: Interpreter stdin stays strict

- **GIVEN** an interpreter receives a complete literal heredoc or here string
- **WHEN** Netclaw evaluates the redirect
- **THEN** Netclaw requires one-shot approval or deny
- **AND** an existing interpreter grant does not bypass the stdin decision

### Requirement: Bash command-resolution mutation stays strict

Netclaw SHALL use the pinned ShellSyntaxTree result as the structural authority.
An unparseable command-resolution mutation or reserved execution form SHALL
produce no persistent approval candidate.

This rule SHALL cover unsupported `exec`, mutating `hash`, alias changes,
shell-option changes, builtin-enable changes, `time`, negation, coprocesses, and
current-shell brace groups.

#### Scenario: Command-resolution mutation cannot reuse a grant

- **GIVEN** a command changes command resolution before another occurrence
- **AND** stored grants cover each visible command name
- **WHEN** ShellSyntaxTree marks the full command unparseable
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent approval candidate

#### Scenario: Reserved execution form cannot flatten into a safe command

- **GIVEN** an unsupported reserved execution form contains a safe verb
- **WHEN** ShellSyntaxTree marks the full command unparseable
- **THEN** Netclaw does not authorize the visible safe verb
- **AND** Netclaw offers no persistent approval candidate

## MODIFIED Requirements

### Requirement: Five-button approval prompt with verb-and-directory framing

When the approval gate prompts the user, the prompt SHALL render five
buttons in one row: `Once`, `This chat`, `Always here`, `Always anywhere`,
`Deny`. The buttons `Always anywhere` and `Deny` SHALL be styled as
danger (Slack `style: "danger"`, Discord `ButtonStyle.Danger`). All
button labels SHALL fit within Slack's 76-character and Discord's
80-character button-text caps.

The prompt body SHALL show the cwd in the header
(`Approve in <cwd> ?`) and the extracted verb chains as a bulleted list.
Single-verb commands MAY collapse the list into the header
(`Approve <verb> in <cwd> ?`). The body SHALL NOT render separate
"Patterns" or "Directory Roots" sections.

The display text for a shell command SHALL be single-line. A command
containing embedded line breaks (LF or CR) SHALL be reconstructed from
its parse tree: statement separators render as explicit operators (`;`,
`&&`, `||`, `|`) and each multi-line argument or redirect target is
replaced with a `(N lines, M chars)` size summary instead of its
verbatim content (issue #1402) — channel renderers embed the display
text in single-line code fences, and dumping a multi-line quoted blob
verbatim corrupts the prompt layout. When the parser cannot decompose
the command, line breaks SHALL be flattened to spaces.

Commands with heredocs, here strings, or subshell groups SHALL NOT use the
compatibility-clause reconstruction. The formatter SHALL detect heredocs and
here strings from their typed v0.3 redirect operations. It SHALL encode each
raw line break as a visible `⏎` marker so the display keeps each redirect
operator, data body, and execution boundary. Subshell groups SHALL use the same
fallback because a flat clause sequence cannot preserve their grouping.

Button semantics:

- `Once` SHALL run the command this one time and persist nothing.
- `This chat` SHALL allow the extracted verbs in the prompt's directory
  for the rest of the session, stored in session-scoped memory only.
- `Always here` SHALL persist `(verb, prompt's directory)` entries to
  `tool-approvals.json` for each extracted verb.
- `Always anywhere` SHALL persist `(verb, null)` entries for each extracted
  verb — the global wildcard.
- `Deny` SHALL refuse this call only. Denying a verb SHALL NOT ban it
  for future invocations.

#### Scenario: Compound command shows verbs as bullets

- **GIVEN** the agent invokes `shell_execute` with command
  `cd ~/repos/foo && git remote -v && git rev-parse HEAD`
  and cwd `~/repos/foo/`
- **WHEN** the approval prompt is rendered on Slack
- **THEN** the body header reads `Approve in ~/repos/foo/ ?`
- **AND** the verbs `cd`, `git remote`, `git rev-parse` appear as bullets
- **AND** the action row contains five buttons
- **AND** `Always anywhere` and `Deny` are styled as danger

#### Scenario: Always here persists folder-scoped entries

- **GIVEN** an approval prompt for verbs `git remote`, `git rev-parse`
  in cwd `~/repos/foo/`
- **WHEN** the user clicks `Always here`
- **THEN** `tool-approvals.json` gains entries
  `{"verb": "git remote", "directory": "~/repos/foo/"}` and
  `{"verb": "git rev-parse", "directory": "~/repos/foo/"}`
- **AND** the resolution message reads
  `Saved: git remote, git rev-parse in ~/repos/foo/`

#### Scenario: Always anywhere persists global entries

- **GIVEN** an approval prompt for verb `freshdesk` in cwd `~/.netclaw/sessions/<id>/`
- **WHEN** the user clicks `Always anywhere`
- **THEN** `tool-approvals.json` gains entry
  `{"verb": "freshdesk", "directory": null}`
- **AND** the resolution message reads `Saved: freshdesk anywhere`

#### Scenario: This chat persists session-scoped only

- **GIVEN** an approval prompt for verb `jsonlint` in cwd `~/repos/foo/`
- **WHEN** the user clicks `This chat`
- **THEN** session-scoped memory records `(jsonlint, ~/repos/foo/)`
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session prompts again

#### Scenario: Deny refuses only the current call

- **GIVEN** an approval prompt for verb `git push`
- **WHEN** the user clicks `Deny`
- **THEN** the current call is refused
- **AND** `tool-approvals.json` is NOT modified
- **AND** a later `git push` call still prompts

#### Scenario: Multi-line quoted argument summarized in display text

- **GIVEN** the agent invokes `shell_execute` with command
  `freshdesk ticket reply 605 --message "Hi,⏎We've rolled out a fix. Please verify."`
  where the quoted argument spans two lines
- **WHEN** the approval prompt is rendered
- **THEN** the display text reads
  `freshdesk ticket reply 605 --message (2 lines, 42 chars)`
- **AND** the display text contains no newline characters

#### Scenario: Heredoc display keeps the full raw command

- **GIVEN** a multi-line command contains a complete heredoc
- **WHEN** the approval prompt formats the command
- **THEN** the single-line display keeps the `<<` operator and body text
- **AND** the formatter does not rebuild the command from compatibility clauses

#### Scenario: Here-string display keeps the authored operator

- **GIVEN** a multi-line command contains a complete `<<<` redirect
- **WHEN** the approval prompt formats the command
- **THEN** the single-line display keeps the `<<<` operator and data text
- **AND** each authored line break renders as a visible `⏎` marker
- **AND** the formatter does not replace `<<<` with `<`

#### Scenario: Heredoc display keeps a following command boundary

- **GIVEN** a complete heredoc is followed by another command after its terminator
- **WHEN** the approval prompt formats the command
- **THEN** the single-line display places a visible `⏎` marker between the terminator and the following command
- **AND** the following command does not appear to be part of the heredoc body

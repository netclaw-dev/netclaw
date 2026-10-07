## Context

See proposal.md. ShellSyntaxTree 0.4.0-beta.19 adds
`AnalyzedArgument.MayPathnameExpand` and `MayFieldSplit`. The facts come from
the authored word, so they hold when the value is `Unknown`. They are true
when the parser is not sure.

## Goals / Non-Goals

**Goals:**
- Use the parser facts instead of raw-text quote scans.
- Deny or ask for each word that can reach the credential store.

**Non-Goals:**
- Expanding a brace word or a glob in Netclaw. The Shell Approval Abstraction
  Rule keeps shell grammar in the parser.

## Decisions

- The pathname rule makes the command unresolved (`Command`), not only the
  operand. An unresolved operand is covered by decision D1, which would let a
  global `cat` grant read `~/.netclaw/{keys,config}/key-1.xml`.
- A proved authored value with no `*`, `?`, or `[` is exempt. Bash then has
  nothing to expand. This keeps `for r in 1 2; do gh run view $r; done`
  covered by its grant.
- `$?` is exempt, because an exit status has no glob character.
- The default credential store text hints also apply to each proved path
  value, as they apply to a glob scope (D5).

## Risks / Trade-offs

- [More exact prompts for unquoted unknown words, such as `cat $f`.] → The
  agent can quote the word. A quoted unknown value keeps decision D1.
- [An unattended brace program word loses its rewrite advice.] → The call is
  denied, which also does not run it. The ledger records the change for owner
  review.

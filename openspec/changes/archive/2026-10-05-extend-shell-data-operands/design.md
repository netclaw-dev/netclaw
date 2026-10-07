## Context

See proposal.md for the motivation. PR #2315 made a dynamic operand of an
output command data. The rule reads one policy list,
`ShellVerbPolicyData.SingleTokenSideEffectVerbs`. That list also drives the
approval exemption, the verb short-circuit, and the path-scope skip. The
analysis, the matcher, and the coordinator are call-local. No actor state and
no persistence change.

## Goals / Non-Goals

**Goals:**

- Remove the prompt for `test` and `[` when their operands are proved data.
- Make a run-time value in an `echo` or `printf` operand data.

**Non-Goals:**

- A parser for the test operators. The Shell Approval Abstraction Rule forbids it.
- `continue` and `break`. ShellSyntaxTree rejects them in a loop, so the
  call never reaches Netclaw policy.

## Decisions

- One data command check. `ShellVerbPolicyData.IsDataCommand(verb, shell)`
  returns true for the output commands, and in Bash for `test` and `[`. Each
  consumer of the old list calls it. Alternative: add `test` and `[` to the
  output list. Rejected, because that list also applies to PowerShell, and
  PowerShell `test` can run a program.
- Bounded values without `[`. A test builtin can read an operand as a name,
  and Bash evaluates a subscript as arithmetic, which runs `$(...)`. The check
  reads only the parser value domain of each operand. Alternative: find a
  `-v` operand. Rejected, because it parses the test grammar, and a dynamic
  word can become `-v`.
- No assignment digest for a Bash data command without a redirect. The
  digest exists so that a grant does not cover a changed assignment. A data
  command without a redirect gets no grant, so the digest has no use.
- First verb token. The parser folds a plain operand into the verb
  (`echo yes`, `[ abc`), so the check reads the first token.

## Risks / Trade-offs

- [`[ -f /x ]` reveals only whether `/x` exists, outside the read roots] →
  The protected-path screen denies a literal or proved protected path. An
  unknown value is not data, so a computed path keeps its prompt.
- [A glob loop such as `for f in src/*; do [ -f "$f" ]; done` keeps a prompt
  or rewrite advice] → A file name can hold `[` and `$(...)`. The safe fix is
  a parser fact for the test operators, not a Netclaw parser.
- [A saved global `test` grant still covers `test -v 'a[x]'` under D1] →
  This is the earlier behavior. Only the owner's own grant does it.

Failure mode: an unknown value or domain fails closed to one exact
candidate. Recovery is the normal one-time consent.

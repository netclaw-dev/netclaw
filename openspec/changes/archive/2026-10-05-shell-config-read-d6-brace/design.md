## Context

See proposal.md. The D6 text screen matched each guarded-directory marker as a
plain substring of the raw command text. The parser reports a brace word as one
exact path with no brace fact.

## Goals / Non-Goals

**Goals:** keep the denial for each brace word and each spelling of the config
directory, with small changes in the two existing checks.

**Non-Goals:** no parser for program text. The #2341 gaps stay open, and #2341
will replace the text markers.

## Decisions

- `ToolPathPolicy` (call-local data) builds the screen text: the command text
  without each exempt read operand, plus the unquoted value of each other
  argument. A word with `{` is never exempt.
- The marker check reads the screen text and a copy with `//`, `/./`, and
  `name/../` collapsed. It reads both, because a collapsed `dir/../x` no
  longer names the directory. A trailing `/.` needs no rule.
- `ToolAccessPolicy` (call-local data) treats a word with `{` as not a
  read-only operand. Alternative rejected: an exemption for a fully quoted
  brace, which adds a quote rule for a small gain.

Schematic flow:

```
screen = text - exempt read operands + unquoted argument values
deny if marker in screen or marker in collapse(screen)
read-only operand = no "{" and bounded value and (path or not an entry name)
```

## Risks / Trade-offs

- [`jq '{a: .x}' <config file>` is denied] → `cat <file> | jq '{a: .x}'` and
  `file_read` work.
- [A program that builds the directory name at run time is not seen] → #2341.

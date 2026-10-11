## Context

See proposal.md. The first D6 text screen allowed any token that named one
exact file and denied only directory, glob, and `..` forms. Program text does
not have that shape.

## Goals / Non-Goals

**Goals:** keep the base text screen, and exempt only proved read operands.

**Non-Goals:** no parser for program text, and no new read-only programs.

## Decisions

- `ToolPathPolicy` removes from the command text only each exact path argument
  of a read-only occurrence that names one file below a guarded directory. Any
  other mention keeps the denial. Alternative rejected: a shape rule on each
  token, which program text can bypass.
- The redirect classification moves to the path fact: an input redirect is a
  read, and any other redirect keeps write protection. A null-device redirect
  is skipped, as before.
- The webhook route files return to the read-deny and shell lists.

## Risks / Trade-offs

- [A read of a config file through an input redirect is denied by the text
  screen] → `cat <file>` and `file_read` work.

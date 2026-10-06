## Why

Review of https://github.com/netclaw-dev/netclaw/pull/2359 found two problems
with the link rule of the `verb-grant-covers-arguments` change (PRD-002 SEC-003,
SEC-009):

- A link word added its link path as a path scope. A folder grant refuses a
  link below its root, so a call that names a link to a file in the same folder
  went from Allowed to a prompt, and to a denial in an unattended run.
- Only command words got the link check. A link word with a digit is an
  argument, so `git add keys2` could reach the keys folder under a grant.

## What Changes

- A link word no longer adds a path scope. Grant coverage does not change for
  a link word.
- The protected-path screen checks each plain word after the program word,
  command word or argument. When the word names a link in the command's
  directory and the link target is protected, Netclaw denies the call before
  grant lookup.

In scope: the link rule of TA-8. Out of scope: the grant reach rule, path
operands with a slash, and any command-specific knowledge.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-8 replaces the link path scope with the
  protected-target screen.

## Impact

- Code: `ToolPathPolicy` (new screen), `ShellApprovalMatcher` (link scope
  removed), `ShellGrantFileWords.NamesLink`.
- Security: a link to a protected path is denied for every plain word, command
  word or argument, also under a grant. This closes the older digit-word gap.
- Operations: a folder grant again covers a link to a file in its folder, so
  scheduled jobs with such grants keep running.

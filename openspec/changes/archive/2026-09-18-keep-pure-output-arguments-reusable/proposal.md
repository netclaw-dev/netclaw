## Why

Source PRD: [PRD-002 SEC-003](../../../docs/prd/PRD-002-gateway-security-envelope.md).

A static shell list can lose all reusable approval candidates when `echo $?` has an unknown value. This creates a repeated one-time prompt after the operator approves the other verbs.

## What Changes

- Keep a complete, static shell list eligible for reusable grants when an approval-exempt output command has a bare `$?` argument.
- Keep executable substitutions, redirects, paths, unknown identities, and incomplete syntax under their current strict rules.
- Add a sanitized regression from the post-swap approval sample and negative authority cases.

This change covers the narrow output case in the MVP. It does not change Bash initial-state proof, dynamic path authority, or command execution.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Define when an unknown data value on an approval-exempt output command leaves other static candidates reusable.

## Impact

`ShellCommandAnalysis` and the shell approval tests change. The approval store, shell executor, public API, and configuration do not change.

Security impact: The parser must still account for every executable region. Redirects and unknown paths must still require their usual authority.

Operational impact: A command such as `git push; echo $?` can reuse a scoped `git push` grant. A command with an unapproved verb still prompts.

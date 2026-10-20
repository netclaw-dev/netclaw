## Why

Repository commands require repeated consent when a shell redirect writes to scratch space outside the repository.
PR #2563 reproduces this friction but asks a repository grant to cover an arbitrary external file.

Source requirements: PRD-002 SEC-003, SEC-006, SEC-007, and SEC-009.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## What Changes

- Compose command consent with file authority for proved redirects into the current run's managed temporary directory.
- Keep repository grants confined to their registered worktrees and preserve ordinary operand scopes.
- Suggest the managed temporary directory for a proved platform-temporary redirect from a repository command.
- Keep corrections advisory and require a new call through every security gate.
- Explain the reach of reusable grants and the reason that an existing grant does not cover a call.
- Preserve the narrow reach of one-word program grants.

MVP scope includes parent and child calls through the existing tool gate.
The change excludes executable-specific parsers, general external-file authority, new grant formats, and OS process isolation.
The separate command-word ambiguity around unknown options remains outside this change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: compose managed scratch redirects with command grants and extend temporary-path advice.

## Impact

The change affects shell candidate projection, file-tool consent, temporary-path advice, and consent text.
It updates the architecture document, regression tests, focused mutation proof, and the netclaw-operations skill.
It requires no configuration property, persistent grant migration, or package update.

## Security and Operational Impact

Filesystem authority must prove that each eligible target stays inside the current run's managed directory without a link escape.
Command grants still cover the command directory and every ordinary operand scope.
File-tool restrictions, protected paths, hard deny, audience boundaries, and launch checks remain mandatory.
Operators receive narrower prompts and an explicit reason for a grant mismatch.

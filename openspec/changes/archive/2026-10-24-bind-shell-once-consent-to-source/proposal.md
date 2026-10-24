## Why

A shell `Once` answer binds parsed approval candidates but not the exact `Command` value. Different Bash source can produce the same candidates, so the retry can differ from the source that received consent.

This change binds one-time shell consent to the exact structured command. It preserves the current candidate and path checks.

## What Changes

- Add a call-local source key from the exact structured shell command. Do not use prompt display text, because display text can sanitize or truncate source.
- Include the key in one-time consent identity for shell calls. Keep it out of persistent grants.
- Rebuild the same key from the original tool call in session history during live and recovered redrive. Do not add a journal or protocol field.
- Fail closed when recovered history lacks a valid source command.
- Test exact retry, source changes with equal candidates, and recovered prompt redrive.

Scope: shell `Once` consent for the main session and live child approval paths. Persistent grant identity and non-shell consent stay unchanged.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: a shell `Once` answer covers only a retry with the same exact structured command source.

## Impact

- Code: shell command extraction, approval context creation, one-time key generation, and session redrive planning.
- Tests: real shell authorization, workflow redrive, and cold recovery.
- Security: source changes require a new consent request, even when candidates remain equal.
- Persistence: no journal or protobuf fields change. The original assistant tool call already stores the exact arguments.
- PRD: PRD-002 covers the gateway security envelope and per-audience consent.

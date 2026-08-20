## Why

The shell approval policy defines schema version 3. It does not define each
JSON member or each version-2 migration result. The code needs one wire
contract. The contract must keep current authority exact and reject bad data.

This change supports PRD-005 and PRD-016. It can reduce approval fatigue
without an implicit change to a user grant.

## What Changes

- Define each JSON form for shell and non-shell entries.
- Keep the version-2 field form for non-shell entries.
- Convert each valid version-2 shell phrase to `LegacyExact`.
- Do not add token-prefix authority as part of conversion.
- Emit `directory` for each version-3 shell entry. Use JSON null for a global
  entry.
- Define whole-file checks, a cross-process lock, a backup, atomic replace,
  and manual recovery.
- Exclude controls and phrases that have no safe representation.

The file version changes from 2 to 3. The current code supports this migration.
A version-2 binary does not have to read a version-3 file.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Define the schema version 3 wire contract and the
  exact version-2 conversion result.
- `netclaw-cli`: Define typed phrase list, revoke, trust, and recovery output.

## Impact

This change affects the approval-store data types, serializer, loader,
comparer, actor snapshot, approval CLI, tests, and operator guide. It does not
change shell text, tool history, or private command rules.

Security impact: conversion keeps exact current authority. A bad file cannot
authorize. Operations impact: the code creates `.v2.bak` before it replaces
the active file. An operator can stop the daemon and restore that backup.

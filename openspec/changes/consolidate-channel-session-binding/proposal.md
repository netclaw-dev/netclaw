## Why

Netclaw needs fewer application mechanisms and less repeated integration proof.
Three provider bindings repeat session lifecycle and journal projection around shared engines.

This change supports PRD-001 session reliability, PRD-002 authority, and PRD-009 provider adapters.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## What Changes

- Give the common session-binding lifecycle one owner for Discord, Mattermost, and Slack.
- Retain provider adapters for transport calls, identifiers, files, prompts, formatting, and activity indicators.
- Reuse the existing output, hydration, approval, and session-pipeline engines.
- Remove the old lifecycle implementations as callers migrate.
- Preserve each provider's persistence ID, cursor representation, and approval event format.
- Characterize provider differences before the source moves.
- Consolidate common proof only after each retained assertion has a clear owner.
- Report production, tests, documents, and support code separately.

## Capabilities

### New Capabilities

None. The change introduces no user-visible capability.

### Modified Capabilities

`channel-binding-parity`: Require current initialized pipeline identity before output effects, and retire obsolete retry timers.

## Impact

The source scope includes the three provider binding actors and their conversation-actor factory seams.
The shared channel assembly owns the common lifecycle.
Provider code retains provider-specific behavior.
The session actor, model loop, reminder schedule protocol, and gateway reconnect policy remain outside this change.

The implementation uses small dependent PR slices where the migration needs them.
The actual `gh stack` command manages those slices.
The user forbids automatic merges. No PR merge or auto-merge request forms part of delivery.

## Security and operational impact

The common owner retains the existing injection checks, requester checks, trusted admission, and restart ingress closure.
The refactor must preserve their order and required dependencies.
The canonical authorization document will describe the resulting owners in the same source diff.

Journal readers must recover existing cursor and approval records without a data migration.
Provider admission order, authority, cursor scheduling, and resource disposal retain their contracts.
The controlled reliability correction rejects obsolete output and cancels a superseded retry timer.
The change introduces no package, configuration property, journal event, or additional journal write.

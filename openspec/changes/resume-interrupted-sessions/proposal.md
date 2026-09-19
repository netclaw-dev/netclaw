## Why

A graceful stop can acknowledge input before the journal stores it. A model call can also delay drain until the global stop limit. A short restart then leaves accepted work quiet or loses queued context.

Source: [PRD-001 FR-003 and FR-016](../../../docs/prd/PRD-001-netclaw-mvp.md).

## What Changes

- Persist each accepted input, its order, media, source ID, delivery context, and authority before the input ack.
- Record the input IDs that a completed turn consumes. Restore accepted input from the journal after cold recovery.
- Stop an eligible model call after a short grace and wait for its task to stop.
- Save one bounded resume candidate after any graceful stop. The deadline expires ten minutes after interruption.
- Resume the original turn under its recorded authority. Deliver the accepted queue in one ordered follow-up model call.
- Leave a completed reply with no accepted queue quiet. Block auto-resume for approvals, uncertain tool effects, and partial replies.
- Report blocked work to the operator.
- Correct the container contract for stop signals and the persistent state volume.

The MVP change excludes automatic crash recovery, replay of uncertain tool effects, and a new reminder schedule. It does not shorten the global stop limit.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: Durable input admission and bounded restart recovery for confirmed interruptions.
- `daemon-container`: The entrypoint forwards stop signals, and the state volume retains restart data.

## Impact

This change affects the session actor, journal events, protobuf mappings, restart manifest, recovery service, and container contract. It adds no user configuration property.

### Security and operational impact

The actor restores the original authority from a durable record. It rejects an incomplete record and does not replay a tool with an uncertain effect. A stale candidate expires without an agent turn. A process stop must complete its journal and manifest writes before exit.

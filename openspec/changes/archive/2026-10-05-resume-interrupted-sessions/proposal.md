## Why

A session acknowledges user input before the journal stores it. A graceful stop can therefore lose an active request or its queued input.

Source: [PRD-001 FR-003 and FR-016](../../../docs/prd/PRD-001-netclaw-mvp.md).

## What Changes

- Persist each accepted input before its acknowledgment.
- Retain its content, order, source ID, media, and original authority.
- Cancel an eligible model call during graceful drain.
- Put a short lived `current_session` reminder in the restart manifest.
- Register that reminder through the existing reminder manager after startup.
- Restore the pending input under its original authority when the reminder arrives.
- Keep approvals, partial replies, and turns with possible tool effects quiet.

This change adds no channel code and no configuration property. It excludes crash recovery and replay of uncertain tool effects.

## Capabilities

### Modified Capabilities

- `session-resume`: Durable input admission and a bounded restart reminder.
- `daemon-container`: The state volume retains the restart manifest across a graceful pod replacement.

## Impact

This change affects session persistence, graceful drain, the restart manifest, and reminder registration. Existing channel gateways deliver the reminder without new adapters.

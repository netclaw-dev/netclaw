## Why

PRD-008 requires durable scheduled work, but Netclaw stores session-owned automation artifacts in daemon-wide directories. This layout prevents a future session-retention process from deleting all artifacts for one session as a unit.

## What Changes

- Store `CurrentSession` reminder definitions and history under the source session directory.
- Store background job definitions and output logs under the source session directory.
- Keep `Channel` and `None` reminder definitions and history in the daemon-wide reminder directory.
- Keep the current reminder manager, background job manager, execution actors, delivery routes, and trust derivation.
- Persist logical session ownership and derive a validated absolute artifact path before file access.
- Restrict each session-owned path to the exact source session directory and reject path traversal.
- Read current session-owned artifacts from their present daemon-wide locations without an automatic move.
- Use the future session-retention process instead of a background-job-specific artifact timer.
- Leave the planned session-retention process outside this change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: Define the session directory as the lifecycle boundary for session-owned automation artifacts.
- `netclaw-scheduling`: Route `CurrentSession` reminder storage by session owner while daemon-scoped reminders retain global storage.
- `reminder-execution-history`: Store reminder history beside the reminder definition under the same ownership boundary.
- `background-job-execution`: Store each job definition and output log under its source session directory without replacing the singleton execution manager.

## Impact

- **In scope:** PRD-008 storage, reminder lookup, reminder reconciliation, background job lookup, dual-location reads, and path tests.
- **Out of scope:** The planned 30-day session-retention process, a job-specific retention timer, actor ownership changes, scheduler replacement, and delivery-route changes.
- **Security:** The daemon derives paths from typed session identity. It does not accept an arbitrary path as reminder or job authority.
- **Operations:** Session removal can later remove these artifacts with the other session files. Daemon-scoped reminders remain independent of session retention.
- **Compatibility:** Current files remain in place. ID-only scheduler payloads and current reminder schedules remain valid.

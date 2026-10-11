## Why

Several documents still describe tool grant categories (`shell`, `github`,
`config_write`, `schedule_write`) as an access control with allowed senders and
channels. The code has no such control. A category is tool metadata; the
per-audience tool allow lists and MCP allow lists decide which tools an
audience can use. The owner approved this correction on September 29.

Source PRDs: PRD-002 (SEC-003, SEC-008), PRD-008.

## What Changes

- `netclaw-scheduling` "Chat-driven task creation": task creation grants no
  tool authority; each tool call passes tool authorization when the task runs.
  The old text said that creation rejects tasks with "ungrantable tools"; no
  such check exists.
- `background-job-execution` "check_background_job tool": admission follows
  `shell_execute`; the `shell` category is metadata only.
- PRD-002 SEC-003, SEC-008, and acceptance criterion 3, and
  `.prose/security-audit.prose`, get the same correction (ordinary docs).

No code change. No behavior change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: one requirement corrected.
- `background-job-execution`: one requirement corrected.

## Impact

Spec and docs text only.

### Security and operational impact

None at runtime. The text now matches the control that the code enforces.

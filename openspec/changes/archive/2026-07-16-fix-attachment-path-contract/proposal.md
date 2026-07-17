## Why

Inbound attachment announcements expose a collision-safe `inbox/...` path while the session prompt also advertises an internal GUID-backed `media_dir`, causing agents to resolve the attachment against the wrong directory and report an unusable host path. PRD-009 requires channel-independent, reliable attachment handling, so the agent-facing contract must identify one authoritative upload path without exposing persistence internals.

## What Changes

- Keep live and historical collision-safe inbox naming unchanged and make the returned `inbox/...` path authoritative for agents and operators.
- Stop advertising `media_dir` in non-Public session context; `session_dir` remains the only exposed filesystem root.
- Clarify that attachment `path` values are relative to `session_dir`, already include any collision-safe rename, and must not be resolved through internal media storage.
- Apply the shared contract to Slack, Discord, and Mattermost live and historical attachment ingress.
- In scope: model-facing path guidance, shared attachment announcement behavior, specifications, tests, and eval coverage.
- Out of scope: media filename changes, storage deduplication, retention, garbage collection, or changes to original-versus-normalized media persistence.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-input-adapters`: Define the final collision-safe inbox path as the authoritative session-relative attachment path across supported chat adapters.
- `audience-context-filtering`: Remove `media_dir` from trusted session context while preserving `session_dir` exposure and Public path redaction.

## Impact

- Affected runtime surfaces: shared attachment formatting and static session prompt assembly.
- Affected channels: Slack, Discord, and Mattermost, including live ingress and thread-history hydration.
- Security: Public sessions continue receiving no absolute host paths; Team and Personal sessions retain the existing `session_dir` disclosure without an additional internal path.
- Operations: operators can resolve an announced attachment as `{session_dir}/{path}`; internal GUID media copies remain implementation details.
- Dependencies and configuration: no new dependencies, configuration properties, migrations, or schema changes.

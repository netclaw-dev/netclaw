## Why

Source PRDs: PRD-001, PRD-004, PRD-009

`ConfigWatcherService` currently validates `netclaw.json` and immediately requests a daemon restart. That behavior drops active session actors mid-turn, which can discard uncommitted tool chains, buffered output, and transient session context even though the daemon already has actor-owned passivation and restart recovery primitives.

Issue #326 is now urgent because Netclaw can modify its own configuration during conversation. A valid config write should preserve active conversations by draining sessions first, restarting cleanly, and recovering the sessions that were active before the restart began.

## What Changes

- Replace immediate config-triggered restart with a controlled restart sequence: validate config, close new session ingress, drain active sessions, restart the daemon, and warm previously active sessions.
- Add restart-specific session drain behavior so active sessions stop accepting new turns, finish or time out in-flight work, snapshot durable state, and report completion to the daemon restart coordinator.
- Persist a restart manifest for the set of sessions that were active when restart began, then relaunch that set after startup and inject a transient restart notice into recovered sessions.
- Return a clear user-facing rejection for new inbound work that arrives while restart drain is in progress instead of buffering or silently dropping it.
- Align MVP planning artifacts with the new behavior by updating config-reload, session, and session-resume capability requirements.

**In scope (MVP now)**
- Config-change-triggered graceful restart for daemon-managed sessions.
- Recovery of sessions that were active at restart start time.
- A fixed restart drain timeout and explicit rejection of new input during drain.

**Out of scope (defer)**
- Zero-restart true hot-reload of provider, ACL, MCP, or scheduling state.
- Preserving live transport connections or subscriber sockets across restart.
- Cross-process distributed drain coordination or multi-node orchestration.
- Replaying partially completed in-flight work beyond the last durable checkpoint.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-config-hot-reload`: change config handling from in-place hot reload to validate-then-drain-then-restart recovery.
- `netclaw-session`: add restart-drain semantics, ingress rejection during drain, and durable passivation for active sessions.
- `session-resume`: add daemon-driven warmup of sessions that were active before restart and post-restart continuity messaging.

## Impact

- Affected code: `src/Netclaw.Daemon/Services/ConfigWatcherService.cs`, daemon lifecycle/restart services, `src/Netclaw.Actors/Sessions/LlmSessionActor.cs`, session ingress paths, session catalog, and startup recovery wiring.
- APIs and UX: daemon-managed adapters and thin clients may receive an explicit "daemon restarting, try again in a minute" rejection while restart drain is active.
- Dependencies: no new external infrastructure; implementation should reuse actor messaging, existing persistence, and startup hosted services.
- Security impact: preserve fail-closed behavior during restart by rejecting new work once drain starts rather than accepting work that might be lost or partially applied.
- Operational impact: config writes become a short controlled outage instead of a lossy immediate restart; operators need observability into drain timeout vs successful drain outcomes.

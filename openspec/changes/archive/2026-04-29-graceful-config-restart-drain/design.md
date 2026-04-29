## Context

`ConfigWatcherService` currently treats a valid `netclaw.json` change as an immediate restart trigger. It validates JSON syntax, sets `DaemonRestartSignal`, and calls `StopApplication()` without coordinating with active session actors.

That behavior conflicts with the current actor model. `LlmSessionActor` already owns durable passivation, snapshots, and restart recovery, while `SessionCatalogService` already records which sessions are active. The missing piece is a restart-specific coordination path that closes new ingress, drains active sessions without accepting more work, and records which sessions should be rewarmed after the restart loop brings the daemon back up.

Constraints:
- Netclaw is single-process and already restarts inside the same outer loop in `Program.cs`.
- Session actors must remain transport-agnostic; Slack, SignalR, and future adapters should observe the same restart semantics.
- Security posture is fail-closed: once restart begins, new work must be rejected rather than buffered optimistically.
- Recovery can only guarantee the last durable checkpoint; in-flight work that has not been committed cannot be silently reconstructed.

## Goals / Non-Goals

**Goals:**
- Replace immediate config-triggered restart with a coordinated drain-and-restart flow.
- Prevent new turns from entering the system once restart drain begins.
- Let active sessions finish their current unit of work when possible, then snapshot and stop.
- Persist a restart manifest for the sessions that were active when restart began.
- Warm those sessions after restart and inject a transient continuity notice for the next turn.
- Preserve existing transport boundaries and actor-owned persistence behavior.

**Non-Goals:**
- Zero-downtime in-place mutation of provider, ACL, MCP, or scheduling state.
- Preserving live subscriber sockets, SignalR connections, or Slack delivery handles across restart.
- Replaying partially completed tool chains or model turns past the last committed checkpoint.
- Adding multi-node or distributed coordination beyond the current single-process daemon.
- Introducing a new persistent database table for restart recovery in MVP.

## Decisions

### D1: Add a daemon restart coordinator and a global ingress gate

The daemon will introduce a restart coordinator service that owns the flow `validate -> close ingress -> snapshot active sessions -> drain -> restart`. A lightweight ingress gate will be checked by daemon-managed adapters before they enqueue new session input.

This closes the race where `GenericChildPerEntityParent` could still create a fresh session actor after the daemon has already decided which sessions to drain. The ingress gate also centralizes the user-facing rejection text so all adapters return the same restart message.

Alternative considered: send drain messages directly from `ConfigWatcherService` and let adapters continue to race with shutdown. Rejected because it leaves a window for new work to enter after the active-session snapshot is taken.

### D2: Use a restart-specific drain path in `LlmSessionActor`

The session actor will add a restart-drain mode distinct from idle passivation. Idle passivation may still be aborted by new input or delivery feedback, but restart drain must not. Once the actor enters restart drain, it rejects new turns, ignores or negatively acknowledges retry-inducing delivery feedback, and stops only after current work completes or the daemon timeout expires.

This preserves the useful parts of the existing `Passivating` phase while avoiding a semantic overload where daemon restart behaves like idle shutdown. It also keeps the actor as the owner of when a session is durably safe to stop.

Alternative considered: reuse the existing passivation behavior and suppress adapter input only at the edge. Rejected because internal messages such as delivery feedback would still be able to abort passivation and re-open the session.

### D3: Persist restart recovery state as a small manifest file

The coordinator will persist a restart manifest containing the active session IDs captured after ingress closes and before host shutdown begins. The manifest should live under Netclaw-managed local storage (for example, the cache directory) and contain only the minimum data required for restart recovery and observability.

This avoids schema churn in the SQLite session catalog or actor persistence journals while still surviving the brief host tear-down between restart loop iterations.

Alternative considered: add a new database table for restart manifests. Rejected for MVP because the manifest is short-lived coordination state, not durable product data.

### D4: Rewarm only sessions that were active when restart began

A startup recovery hosted service will read the manifest after actor infrastructure is available, warm each recorded session through the session manager, and then clear the manifest. Sessions that were already inactive stay cold and recover lazily on their next normal message.

This matches the issue's goal of preserving mid-session continuity without pointlessly reactivating the entire catalog. Rewarming through the session manager also preserves actor encapsulation and lets the existing journal/snapshot path do the actual state recovery.

Alternative considered: rely entirely on lazy recovery when the next user message arrives. Rejected because it loses the ability to preserve the set of sessions that were actively interrupted by restart and makes continuity messaging harder.

### D5: Reject new work during restart drain with an explicit operator-visible message

Once restart drain begins, adapters and daemon entry points will reject new input with a consistent message such as `Daemon restarting, try again in a minute.` Actor-level asks may return `CommandNack` with the same reason when the gate is encountered behind the transport boundary.

Rejecting work is safer than buffering across restart because it avoids ambiguous ownership of uncommitted turns, duplicate replay, or silent loss.

Alternative considered: accept and buffer new work until the daemon comes back. Rejected because the buffer would need its own durable protocol and failure semantics, which is outside MVP scope.

### D6: Give the host enough time to drain

The current host shutdown timeout is 10 seconds. This change should set a restart drain budget and make the host shutdown timeout longer than that budget so hosted services and actor shutdown are not cut off early.

For the first implementation, the drain timeout should be a fixed constant rather than a new operator-facing config property. That keeps the change focused on correctness and avoids widening config/schema work in the same pass.

Alternative considered: make the timeout configurable immediately. Rejected for MVP because it expands surface area before the base workflow is proven.

## Risks / Trade-offs

- [Risk] In-flight work may still be lost if drain times out before a turn commits.
  -> Mitigation: surface the timeout outcome in logs/notifications, recover only from the last durable checkpoint, and inject a restart continuity notice on the next turn.

- [Risk] The session catalog could be slightly stale when the coordinator snapshots active sessions.
  -> Mitigation: close ingress first, use the actor-owned deactivation callback already in place, and treat the manifest as best-effort warmup state rather than a source of truth for persistence.

- [Risk] Rewarming sessions after restart may increase startup work when many sessions are active.
  -> Mitigation: limit warmup to the manifest set, keep the manifest minimal, and allow the recovery service to log/skip individual failures without blocking the whole daemon forever.

- [Risk] Operators experience a short write outage during restart drain.
  -> Mitigation: keep the drain window bounded, return a clear retry message, and expose restart reason and timeout outcomes through existing lifecycle notifications/logging.

## Migration Plan

1. Land the OpenSpec change and update the affected engineering docs to replace the old true-hot-reload narrative.
2. Implement the restart coordinator, ingress gate, session drain message flow, restart manifest, and startup recovery service in one PR.
3. Raise the host shutdown timeout and add integration tests covering drain success, drain timeout, rejected ingress, and restart recovery.
4. Roll out without a database migration; rollback is a normal code revert because the manifest is ephemeral and can be safely ignored by older binaries.

## Open Questions

- Should the fixed restart drain timeout remain an internal constant after the first release, or should a follow-up change make it operator-configurable once the workflow is proven in practice?

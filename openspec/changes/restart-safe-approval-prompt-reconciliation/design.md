## Context

`LlmSessionActor` already journals pending tool approvals and can re-drive a parked tool batch after cold recovery. The remaining gap lives in the channel bindings: Slack, Mattermost, and Discord each keep a process-local `_pendingApprovalRequests` list containing the original `ToolInteractionRequest` plus the platform-specific prompt handle (`messageTs`, `postId`, `messageId`). When an approval response arrives after the binding has been passivated or the daemon has restarted, the binding can still forward the click to the session, but it no longer knows which prompt message to update. The current code logs a cold-spawn "redraw skipped" path and leaves the prompt in its pre-resolution state.

This creates a split-brain recovery experience. The session is durable; the visible approval prompt is not. The result is a stale interactive control for an approval that may already have been resolved, abandoned, or classified as expired by the session. Coordinated restart makes this more visible because FR-016 deliberately drains and relaunches sessions while transport connections and delivery handles are re-established separately.

Constraints:

- The session actor remains transport-agnostic and authoritative for approval validity.
- Channel bindings own platform-specific prompt handles and redraw mechanics.
- Restart continuity must fail loud, not silently skip reconciliation.
- The smallest safe change is preferred over introducing a general transport-state database if an approval-specific durable projection is enough.

## Goals / Non-Goals

**Goals:**

- Make approval-prompt reconciliation restart-safe for Slack, Mattermost, and Discord.
- Allow a cold-spawned binding to look up the original prompt handle by `SessionId` + `CallId` and reconcile the prompt after the session accepts, denies, or expires the approval.
- Preserve the current trust boundary: the binding forwards user intent, the session decides what happened, and the binding renders that result.
- Cover both warm coordinated restart and ordinary passivation/cold-resume paths.

**Non-Goals:**

- Changing approval decision semantics, requester authorization, or grant persistence behavior.
- Implementing transport-agnostic prompt editing for channels that do not support post creation/update.
- Building a generic persistence framework for arbitrary adapter-local state.
- Reconstructing or mutating prompt messages that were never durably recorded in the first place.

## Decisions

### D1. Persist a narrow approval-prompt reconciliation projection outside adapter memory

**Decision:** introduce a dedicated durable projection keyed by channel session identity and approval `CallId` that stores only the metadata needed to reconcile the original prompt message after recovery.

**Rationale:** the missing state is not conversational or actor-core state; it is a transport handle owned by the adapter. Storing it in `_pendingApprovalRequests` is what creates the restart gap. Persisting a narrow approval-prompt projection keeps the fix scoped to the transport problem without polluting `SessionState` with Slack/Mattermost/Discord message identifiers.

**Alternatives considered:**

- Store prompt handles directly on `SessionSnapshot`. Rejected because session persistence must remain transport-agnostic, and channel-specific message identifiers do not belong in actor-core durable state.
- Re-render a brand-new prompt on restart instead of reconciling the old one. Rejected because it duplicates operator-facing prompts, breaks the original click targets, and turns one approval into two visible artifacts.
- Accept stale prompts as cosmetic debt. Rejected because the stale controls misrepresent the actual security state and train operators to distrust the approval surface.

### D2. Session emits reconciliation outcomes; adapters render them

**Decision:** the session continues to decide whether an approval was resolved, denied, abandoned, or expired, and emits a transport-facing reconciliation outcome that bindings consume to update the original prompt when a durable prompt handle exists.

**Rationale:** only the session has the information to distinguish "approved and re-driven", "expired", "already resolved duplicate", and "abandoned because history moved on". Re-implementing that classification in bindings would duplicate security-sensitive logic and invite drift.

**Alternatives considered:**

- Let bindings infer resolved vs expired from `CommandAck`/`CommandNack` alone. Rejected because those responses are too lossy for accurate redraw state, especially after replay/recovery.
- Let bindings ask the session for prompt state on demand. Rejected as unnecessary round-trip complexity for what is effectively an emitted lifecycle event.

### D3. Bindings reconcile lazily on demand, not by replaying all historical prompts at startup

**Decision:** when a binding is cold-spawned, it does not proactively scan and redraw every outstanding prompt. Instead, it consults the durable prompt projection only when it receives a new `ToolInteractionRequest`, a user approval response, or a session-emitted reconciliation outcome for a known `CallId`.

**Rationale:** this keeps startup simple and bounded. The main correctness requirement is that the prompt updates when the approval path moves forward, not that every binding eagerly hydrates all channel state before any activity occurs.

**Alternatives considered:**

- Full startup hydration of all pending prompts per binding. Rejected as more moving parts, more indexing needs, and slower cold-start behavior for limited UX benefit.
- No hydration at all. Rejected because it preserves the current stale-prompt gap.

### D4. Reconciliation must cover expired and abandoned prompts, not only successful approvals

**Decision:** the durable prompt mapping remains available until the session records a terminal prompt state and the adapter has had a chance to reconcile it. Terminal states include approved, denied, expired, and abandoned/superseded.

**Rationale:** stale buttons are just as misleading for expired or abandoned prompts as they are for approved ones. A restart-safe approval surface must close the loop on every terminal outcome.

### D5. Prompt-handle durability is best-effort but explicit

**Decision:** if a prompt was posted but the handle could not be durably recorded, Netclaw fails loud in logs and metrics and falls back to the existing session-side expired/duplicate handling. It does not silently claim restart-safe reconciliation when the handle was never captured.

**Rationale:** this preserves the constitution's no-silent-fallback rule. The operator-visible behavior should improve when durable prompt state exists, and diagnostics should be explicit when it does not.

## Risks / Trade-offs

- [Transport projection drift between session state and adapter state] → Keep the session as the only source of reconciliation truth; the projection stores handles, not approval decisions.
- [Extra persistence write on every posted approval prompt] → Acceptable because approval prompts are human-paced and rare compared with ordinary message traffic.
- [Platform update failure after recovery leaves the old prompt visible] → Keep the session outcome correct, emit explicit logs/telemetry, and avoid retry loops that could spam edits.
- [Cross-channel abstraction grows too broad] → Scope the first implementation to the existing three interactive adapters and a narrow shared contract based on `CallId` plus prompt handle.

## Migration Plan

No user-facing data migration is required. New durable prompt-handle records begin being written when approval prompts are posted.

- Existing sessions and old prompts without durable handles continue to function at the session layer, but their old prompts cannot be retroactively reconciled.
- Rollout is additive: adapters consult the durable projection when present and otherwise preserve today's fail-loud cold-path behavior.
- Rollback is a code revert plus ignoring any stored prompt-handle records. Because the records are transport-side metadata, losing them does not corrupt session history or approval decisions.

## Open Questions

- Should reconciliation outcomes be modeled as a new `SessionOutput` type, or can an existing output/feedback channel carry enough information without overloading semantics?
- Should the durable prompt projection live in a lightweight JSON store under `~/.netclaw/` or in existing persistence infrastructure associated with session identity? The implementation should choose the smallest option that preserves actor/channel separation.

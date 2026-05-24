## Why

Approval clicks now survive session recovery, but the channel-side prompt state does not. If a Slack, Mattermost, or Discord binding is cold-spawned after passivation or coordinated restart, it can forward the approval response to the session but cannot reconcile the original prompt into a resolved or expired state, leaving the user staring at stale buttons on a request Netclaw has already processed or abandoned.

This breaks the operator-facing half of FR-003 persistent recovery and FR-016 coordinated restart continuity. The session recovers correctly, but the transport surface still looks broken after restart because the original approval prompt handle lived only in adapter memory.

## What Changes

- Persist enough approval-prompt reconciliation state to re-associate a recovered pending approval with the original channel prompt after adapter passivation or daemon restart.
- Add a channel-facing reconciliation path so a cold-spawned binding can resolve, expire, or otherwise disable the original prompt once the session has authoritatively handled the approval response.
- Require coordinated restart and cold-resume flows to preserve approval-prompt continuity: after recovery, the next approval click must both reach the session and reconcile the visible prompt state instead of logging "redraw skipped".
- Keep the session actor as the authority on approval validity, requester authorization, and expiry classification; channel bindings only render the resulting state.
- In scope for MVP: Slack, Mattermost, and Discord approval prompts; resolved-state redraw and expired-state reconciliation after cold binding/session recovery.
- Out of scope: resurrecting lost third-party transport handles that were never durably recorded, editing prompts for channels that do not support updates, or changing approval policy semantics / grant persistence rules.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `tool-approval-gates`: approval prompts become restart-safe at the transport layer as well as the session layer; a recovered approval interaction must reconcile the original prompt to a resolved or expired state instead of leaving stale interactive controls behind.
- `netclaw-input-adapters`: approval-capable adapters must durably track enough prompt metadata to reconcile approval prompts after passivation or restart, and must route reconciliation outcomes from the session back onto the original channel message when the platform supports updates.
- `session-resume`: warm restart and cold-resume behavior for approval-pending sessions now includes restoring prompt reconciliation continuity, not only rehydrating the session's internal pending approval state.

## Impact

- **Source PRDs**: PRD-001 FR-003 (persistent recovery), PRD-001 FR-011 (tool access), PRD-001 FR-016 (config-change restart coordination).
- **Code**: channel binding actors for Slack, Mattermost, and Discord; any shared approval-prompt projection or durable store used to map `CallId` to channel prompt handles across restart; session-side outputs/events used to signal reconciliation results.
- **Tests**: adapter cold-spawn tests, approval rehydration tests, and restart-drain/restart-resume tests need new coverage for post-recovery prompt reconciliation and stale-button suppression.
- **Operational impact**: coordinated restart should no longer leave approval prompts visually orphaned in channels that support prompt updates. Operators receive explicit resolved/expired prompt state instead of stale interactive controls.
- **Security impact**: no widening of approval authority. The session remains the sole source of truth for `CanApprove`, approval persistence scope, and expired-prompt classification. The change removes a misleading UI state without introducing transport-side approval decisions.

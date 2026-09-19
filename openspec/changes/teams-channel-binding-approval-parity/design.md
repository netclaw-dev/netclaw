## Context

See [proposal.md](proposal.md) for motivation and the local parity audit in
`docs/teams/channel-binding-approval-parity-audit.md`. Teams has a durable
binding actor with SDK-free input contracts and its own card replay state.
Slack, Discord, and Mattermost instead share approval response, output, and
transport-failure lifecycle components.

The session journal owns pending approval state and intentionally waits without
a timer. The corrective behavior replaces an expired or failed card binding
without forwarding `Deny`; Teams also applies the shared prompt classifier
before session dispatch.

## Goals / Non-Goals

**Goals:**

- Make generic session approval authority explicit at the Teams transport
  boundary without changing core tool eligibility.
- Apply common approval response and delivery contracts to Teams without
  weakening card callback validation or replay protection.
- Read existing Teams persistence while adding only bounded transport metadata
  needed for card replacement; do not promise that older binaries can read
  newer manifests.
- Add cross-channel and focused replay/expiry/classification test evidence.

**Non-Goals:**

- No Microsoft Graph reads, history hydration capability, or tenant permission
  change.
- No change to the upstream Personal-only host shell boundary. Teams approval
  presentation cannot make an otherwise ineligible tool eligible.
- No Teams-specific policy or grant store and no mutation of operator config.
- No live tenant validation, deployment, upstream push, merge, or automerge.
- No replacement of Teams proactive delivery persistence or channel routing.

## Decisions

### Teams uses shared classifiers and response lifecycle

`TeamsConversationDependencies` gains an additive detector property. The
binding turns inbound handling asynchronous and calls `PromptClassifier` after
ACL/mention acceptance but before durable reservation and pipeline enqueue.
Safe input keeps the already-resolved Teams trust context. Blocked or
unavailable classification does not reserve an activity or create a model turn.

The Teams actor continues to validate opaque correlation, nonce, destination,
prompt locator, requester, and offered option before it invokes the shared
response path. The shared path then owns the session feedback round trip and
whether the response is accepted. This preserves actor serialization rather
than adding locks or a second mutable authorization store.

### Expiry replaces a card binding, not the session decision

When a callback reaches an expired card, Teams persists a replacement nonce
hash before it returns the expiry result. It does not persist an approval
decision and does not forward a `ToolInteractionResponse`. The old card cannot
match the replacement hash. A valid action on the new card uses the normal
consume-once flow.

The current binary reads existing persistence records. Newly written pending
state remains bounded; snapshots capture the current transport binding and its
presentation-recovery state. A replacement never persists a raw nonce: it
persists only the current hash, and a successful unbound send is recorded as
presented even when Teams returns no activity ID.

If presentation delivery fails, Teams replaces the attempted hash with a fresh
one and leaves the presentation pending for recovery. Restart or a later safe
delivery opportunity issues a fresh card once; obsolete queued sends cannot
present a superseded nonce. This avoids a tight self-retry loop while leaving
the session approval pending and replay protection intact.

### Forwarding state is transport-only and recoverable

Teams records the validated selected option as a bounded forwarding state before
the shared flow sends it to the session. This temporarily prevents another card
option from changing the selected decision, but it is not a grant or terminal
approval result. Teams writes its terminal consume record only after the shared
flow receives a session acknowledgement, or after the session deterministically
reports that the call is no longer pending.

If the feedback round trip fails before a usable acknowledgement, the current
card is returned as a retryable single-option card using the same opaque nonce.
After restart, Teams re-drives the same selected option; a session acknowledgement
consumes it once and a stale-session response terminates without a second tool
execution. A recovery retry that cannot reach the session gets a fresh bounded
transport card for the same selected option.

### Delivery failure follows the common escalation contract

Normal Teams text output and approval-card delivery use the same telemetry and
session failure feedback semantics as `SafeTransportCall`; a failure in the
feedback pipe is allowed to escape the actor so supervision can recreate its
pipeline. Typing remains a best-effort effect because it is not user content.

## Risks / Trade-offs

- [Legacy Teams journals lack enough presentation metadata] -> continue to
  render deterministic unavailable terminal cards and never map legacy tokens.
- [Replacement-card delivery fails] -> invalidate the attempted opaque binding,
  keep bounded presentation-recovery state, surface the transport failure, and
  issue a fresh card only on a later recovery opportunity; never synthesize a
  denial.
- [Actor restart occurs after transport forwarding begins but before session
  response] -> re-drive the exact durable selection. The session acknowledgement
  remains authoritative and terminal consumption is written once.
- [Tests formerly build dependencies without a detector] -> test helpers
  explicitly supply a safe detector; a missing production detector is treated
  as unavailable and fails closed.

## Migration Plan

1. Before deployment, take and verify a backup or snapshot of the Teams
   persistence store. Deploy code that reads existing Teams records and writes
   the bounded updated state.
2. Existing active cards with a legacy offered-key record remain unavailable;
   new session requests render a new card under the new binding behavior.
3. Rollback to the previous binary is straightforward only before
   `teams-approval-reissued-v2` or `teams-approval-forwarding-v2` has been
   written. The previous binary cannot read either manifest. Afterward, apply
   a forward fix or restore the verified pre-deployment persistence snapshot
   before starting the previous binary.

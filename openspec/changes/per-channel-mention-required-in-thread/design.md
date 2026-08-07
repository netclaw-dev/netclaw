## Context

Each chat adapter (Slack, Discord, Mattermost) routes an inbound message through two
stages in the conversation actor:

1. ACL and trust — `SlackAclPolicy.EvaluateInbound` (and the Discord/Mattermost peers).
   It decides access and resolves the audience.
2. Routing — `SlackRoutingPolicy.Evaluate` (and peers). It decides if Netclaw engages.

Once a thread has an active session, stage 2 has an "active-session bypass": it forwards
every message to the session without a mention. A separate mechanism, thread-history
hydration (`SlackThreadBindingActor.PerformOneShotHydrationAsync` plus the per-adapter
`*ThreadHistoryFetcher`), runs at most once per actor runtime. On the first inbound or
after a restart, it fetches the gap of prior thread messages, applies the prompt-injection
gate and per-sender trust and audience, and merges the gap into the triggering event.

PR #1783 adds a connector-wide `MentionRequiredInThread` bool. It turns the active-session
bypass into a mention gate, but only at the connector level, and it does not re-run the
backfill for a live (hot) actor.

Constraint to respect: hydration runs at most once per actor runtime. This rule exists to
avoid a duplicate-content bug (PR #733): an in-flight turn leaves the cursor lagging, so a
re-fetch re-includes in-flight messages and re-emits their content.

## Goals / Non-Goals

**Goals:**

- A per-channel toggle that ignores un-mentioned messages in an active thread (the tap).
- On a mention, re-run the existing backfill so Netclaw catches up on the gap the tap held.
- Per-channel storage; delete the PR #1783 connector bool; seed at add time from the
  audience; edit on the `EditAudience` leaf.

**Non-Goals:**

- No connector-wide or workspace-wide default.
- No change to ACL, audience resolution, or the prompt-injection gate. This change reuses them.
- No config-shape migration for `AllowedChannelIds` or `ChannelAudiences`.
- No new routing abstraction. This change reuses the routing policy and the hydration path.

## Decisions

**1. Reuse the existing hydration for catch-up, re-triggered on a mention.**
The mechanism already fetches the gap and applies the multi-party rules. A mention becomes
another trigger for it. Alternative — a new reconcile path — is rejected: it would duplicate
routing and security logic.

**2. Relax "hydrate at most once per runtime" to "re-hydrate on a mention when the tap
gated messages since the last turn."**
Guard the re-hydration: run it only on a mention, only when the cursor shows a real gap, and
only when no turn is in flight. Alternative — fetch on every inbound — is rejected because it
reintroduces the PR #733 duplicate-content bug.

**3. Resolve the per-channel value in the conversation actor, then pass a resolved bool into
the pure `RoutingPolicy.Evaluate`.**
The channel ID is already in scope at the call site. The routing policy stays a pure
function of primitive flags. Alternative — thread the per-channel map or the audience into
the policy — is rejected as unneeded plumbing.

**4. Store the per-channel value with the existing `ChannelAudiences` per-channel-map
pattern.**
The storage stays additive. No shape migration for `AllowedChannelIds` or `ChannelAudiences`.
Alternative — a new nested per-channel object that absorbs the flat collections — is rejected
for this change: it forces a config-shape migration and a config-editor rewrite.

**5. Remove the connector bool.**
The PR #1783 connector-wide `MentionRequiredInThread` was never deployed, so there is no
config to migrate. Delete the property, its readers, and its schema entry. Alternative —
keep the connector bool as a workspace-wide fallback — is rejected: it is a two-level model
the operator does not want.

**6. Seed the per-channel value at add time from the audience (write-time), not at runtime.**
The add-channel step already reads the audience. It writes `true` for a public or team
channel and leaves the value off for a personal channel or a DM. Routing stays free of
audience logic. Alternative — a runtime audience default in the routing policy — is rejected:
the audience heuristic never yields `Personal`, so it would gate almost every channel.

**7. Broaden the `EditAudience` leaf into a per-channel detail page.**
The leaf already edits one channel's audience. It gains the `MentionRequiredInThread` toggle.
Alternative — a new config page — is rejected: the per-channel leaf already exists.

## Actor boundaries and persistence

- The per-channel value is resolved in the conversation actor (the parent). The thread
  binding actor (the child) owns the cursor and the hydration.
- The cursor persists and advances only on `TurnCompleted`. An ignored (un-mentioned) message
  never reaches the binding actor, so it never advances the cursor. On a mention, the gap
  from the persisted cursor to the thread head is exactly the messages the tap held.
- No new persistence type. The change reuses the cursor and the hydration.

## Risks / Trade-offs

- Duplicate content on re-hydration → gate the re-hydration on a real gap and no in-flight
  turn; rely on the cursor invariant (it advances only on `TurnCompleted`).
- Cross-channel drift → the three adapters share the contract through `thread-history-backfill`
  and `netclaw-input-adapters`; contract tests cover parity.
- Dependency on PR #1783 → merged; this branch is rebased onto it. The connector bool is
  deleted, not migrated.
- TUI regression → add a native smoke tape for the `EditAudience` leaf.

## Failure modes and recovery

- Hydration fetch failure on a mention → log and continue without the backfill, as the
  existing hydration already does. The tap still gates; the mention still gets a reply.
- Daemon restart mid-gap → the once-per-runtime hydration on the next spawn covers the gap,
  as today. The re-trigger adds the hot-actor case only.
- In-flight turn when a mention arrives → the guard skips the re-fetch; the cursor invariant
  prevents duplicate content.

## Migration Plan

- No config migration. The connector-wide bool was never deployed, so no stored config
  carries it. The change deletes the bool and adds the per-channel value; the storage is
  additive.
- Deploy order: PR #1783 is merged to `dev`; this branch is rebased onto it. Ship after the
  code phase.

## Open Questions

- File shares (Story 6): confirm the current behavior against issue #1782 before the spec
  requirement is final.
- Per-channel storage shape: a parallel map keyed by channel ID, or a small per-channel
  object. Both are additive. Settle in tasks.

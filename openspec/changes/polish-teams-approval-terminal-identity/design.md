## Context

See proposal.md for motivation. The daemon owns Microsoft Teams SDK objects at
the HTTP edge. It converts an `Action.Execute` activity into an SDK-free
approval action before routing it to a binding actor. The binding actor
persists the canonical sender identifier only and builds the final terminal
card after the core approval flow accepts the decision.

## Goals / Non-Goals

**Goals:**

- Carry one bounded presenter label from the authenticated SDK activity to the
  accepted terminal-card renderer.
- Keep the existing canonical sender identity and persistent approval events
  unchanged.
- Use exact, non-execution wording for a granted terminal card.

**Non-Goals:**

- Query Microsoft Graph, resolve UPNs, configure groups, or change Teams ACLs.
- Add the label to callback data, telemetry, logs, journal events, or recovery
  state.
- Change approval policy, action binding, execution, replay, or expiry logic.

## Decisions

### Treat the SDK account display member as presentation-only

Microsoft.Teams.Apps 2.1 models the activity's human display name as
`TeamsChannelAccount.Name`. The translator will read it only after its existing
authenticated action validation.
It will retain `AadObjectId`, falling back to `Id`, for the canonical identity.
Using the display member as the security ID would let mutable presentation data
affect authorization; using Graph or a UPN would expand dependencies and data
handling without a need.

### Sanitize at the edge and defend at rendering

The SDK translator will reject an empty, GUID-shaped, control-containing, or
Unicode-format-containing label, trim a remaining label, and truncate it with
the shared approval-display formatter to a small renderer budget. Before that
truncation, it will discard a label equal to a known sender, AAD object,
tenant, conversation, activity, team, channel, correlation, or nonce
identifier. The SDK-free action will carry this optional value separately from
its trust context. The renderer
will apply the same normalizer again so direct callers cannot bypass the UI
bound. The alternative of accepting raw text risks layout and bidi spoofing;
storing a separate normalized field would create unnecessary personal data
retention.

### Keep presentation transient across the actor flow

The forwarding message will carry the optional label only for the live
accepted-response path and pass it to the granted or denied card builder. It
will not appear in `TeamsApprovalForwardingStarted`, consumed events, pending
approval state, recovery messages, diagnostics, or logs. Recovered and generic
terminal cards therefore retain the exact fallback label where relevant.

### Use precise execution wording

The granted-card field changes from `Pending execution` to `Execution
Approved`. Existing terminal banner and speech remain approval-only and state
that execution is not confirmed. No code path treats this card as a tool result.

## Risks / Trade-offs

- [Teams omits or changes the display member] → The optional value falls back
  to `Authorized operator` without affecting the canonical identity.
- [A direct renderer caller supplies unsafe text] → The renderer normalizes it
  again and selects the fallback.
- [An actor recovers after forwarding began] → The label is intentionally not
  persisted; recovery preserves approval behavior and does not claim a human
  label that was not durably recorded.
- [A card reader interprets approval as execution] → Exact field, banner, and
  speech tests prohibit success or completion wording.

## Migration Plan

Deploy as a backward-compatible card presentation update. Existing pending and
recovered approvals remain valid because event schemas and canonical identity
handling do not change. Rollback restores the former card text and fallback
label without data migration.

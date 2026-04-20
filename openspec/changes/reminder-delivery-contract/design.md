# Design: reminder-delivery-contract

## Context

`set_reminder` / `ReminderDefinition` / `ReminderExecutionActor` carry
four separate concerns wrapped up in a single mixed API:

- **Execution mode** — re-enter the originating session (today's Mode B)
  vs. spawn a fresh isolated session (Mode A) — inferred from presence
  of `ReportToChannel`.
- **Delivery target** — a Slack channel ID, a user DM, or (implicitly)
  the originating session; stored in `ReportToChannel` + the implicit
  `SessionId`/`OriginChannelType` fallback.
- **Delivery policy** — `NotifyPolicy` (required vs. conditional)
  governs whether missing notifications fail the execution, but today
  the policy is not enforced on the Mode B path at all.
- **Result body** — `NotifyInstructions` is used by the LLM both to
  describe the content of the notification AND to encode *routing*
  intent ("Reply in this session with the result.").

Journal seq=1..4 on session `D0AC6CKBK5K/1776697725.361339` shows the
failure mode: a Mode B reminder fires, the session-level `CommandAck`
comes back from `LlmSessionActor.TryReplyAck` (mailbox accepted the
turn), the execution actor acks the Akka.Reminders envelope, execution
history records `success = true`. But the agent's *reply* to the turn
never surfaces in Slack because nothing observes the outbound side of
the ChannelPipeline. The reminder is marked done; the user sees
nothing; no operational alert fires.

Netclaw is pre-production with one operator (N=1). Schema breaks have
no migration cost.

## Goals / Non-Goals

**Goals:**

- Replace the mixed `ReportToChannel`/`NotifyInstructions`/`NotifyPolicy`
  surface with four independent structured fields:
  `delivery.kind`, `delivery.transport`, `delivery.address`,
  `deliveryRequired`, `deliveryInstructions`.
- Make execution mode a direct function of `delivery.kind`, not an
  inference.
- Fold in #644: key `IReminderTargetResolver` by transport so future
  channels plug in without schema changes.
- Close the observed silent-failure bug by distinguishing session
  mailbox acceptance from actual outbound delivery via a new
  `ReminderDeliveryObserved` gateway signal.
- Restore loud failure on missed required delivery for *all*
  delivery kinds, not just Mode A.

**Non-Goals:**

- Adding new transports (Discord, Teams, email). Transport keying
  lands as a structural hook; second transports are separate work.
- Durable ingress queue on `LlmSessionActor`. The "crash after
  `AckAsync` succeeds but before `TurnRecorded` persists" window
  remains an accepted tradeoff (deferred to drain-on-shutdown work,
  issues #403 / #419).
- Cross-transport qualification syntax (`slack:#general`). Out of scope
  until a second transport actually ships.
- Operator-facing config surface. All internal constants stay internal.

## Decisions

### D1. Four structured fields, no inference

```
delivery: { kind, transport?, address? }
deliveryRequired: bool
deliveryInstructions: string?
```

Rationale: the LLM should never have to encode routing choices in free
text. `delivery.kind` is an enum the LLM picks explicitly. Execution
mode flows from `kind`, not from which optional fields are populated.

Alternative considered: "add `kind` but keep `ReportToChannel` as a
free-text fallback." Rejected — leaving the fallback preserves the
foot-gun. Since N=1, we can break cleanly.

### D2. `delivery.kind = current_session | channel | none`

- `current_session` → re-enter the originating session via the existing
  `DeliverTrustedSessionTurn` pathway. No external notification tool.
  Requires an addressable session context at set time
  (`ChannelType ∈ {Slack, Tui, SignalR}`).
- `channel` → spawn a fresh isolated session; the session's LLM calls
  the transport's canonical notification tool (`send_slack_message`
  today) to post to `delivery.address`.
- `none` → spawn a fresh isolated session; run the task, record
  history, emit no external output. Appropriate for audit / cleanup
  tasks.

Rationale: three kinds map 1:1 onto the three execution behaviors we
already have (session-reentry / isolated-with-notify / isolated-silent),
making mode selection explicit and machine-checkable.

### D3. Transport-keyed `IReminderTargetResolver` (folds in #644)

```csharp
public interface IReminderTargetResolver
{
    string Transport { get; } // "slack", "signalr", ...
    Task<ReminderTargetResolution> ResolveAsync(string address, CancellationToken ct = default);
}
```

`SetReminderTool` injects `IEnumerable<IReminderTargetResolver>` and
dispatches by `Transport`. Unknown transport → fail loud at tool call
time with an actionable error.

Rationale: #644 wants transport-tagged targets and transport-keyed
resolver dispatch. We're rebuilding the schema anyway, so adding the
`Transport` property now costs one field on the interface and avoids a
second schema rewrite when the second transport ships. Cross-transport
qualification syntax (`slack:#general`) and selection-when-ambiguous
rules stay with #644 proper.

### D4. `ReminderDeliveryObserved` outbound gateway signal for `current_session`

For `delivery.kind = current_session` with `deliveryRequired = true`:

1. `ReminderExecutionActor` Ask<CommandAck>s the gateway (as today).
2. On `CommandAck` (session mailbox accepted), the actor does NOT
   immediately call `_client.AckAsync(envelope)`.
3. Instead, it waits (with a timeout slightly longer than
   `DefaultAckTimeout`) for a `ReminderDeliveryObserved(reminderId, channelType)`
   signal.
4. The signal is emitted by `ChannelPipeline`'s outbound stage when an
   assistant reply whose source turn carried a matching
   `SourceReminderId` actually flows out through the channel's
   subscriber sink (Slack post API returns success, SignalR frame
   accepted, etc.).
5. Miss → `ReminderExecutionCompleted(success=false, "delivery not observed")`,
   `OperationalAlert.ReminderExecutionFailed` fires, envelope held for
   redelivery.

For `deliveryRequired = false`, `CommandAck` alone is sufficient — the
signal wait is skipped.

Alternatives considered:

- **Synchronous Ask-chain all the way out to the gateway post API.**
  Rejected: would force the session actor to block on outbound I/O,
  breaking Akka pipeline semantics and coupling message acceptance to
  external API latency.
- **Separate observer actor subscribed to gateway post events.**
  Rejected for now: emitting a one-shot signal from `ChannelPipeline`
  with the already-persisted `SourceReminderId` is simpler and doesn't
  introduce a new actor or subscription.

### D5. Transport-aware notification tool name for `delivery.kind = channel`

`ExecutionOutputAccumulator` currently hardcodes
`new ToolName("send_slack_message")` as the notification tool it
watches for in Mode A. This becomes a parameter derived from
`Delivery.Transport`:

```
Transport → Expected notification tool
"slack"   → send_slack_message
"signalr" → (no canonical tool; channel delivery on SignalR is rejected at set time)
```

SignalR / TUI are session-oriented transports; they don't have a
"post to channel" tool. `delivery.kind = channel` with
`transport = signalr` fails validation at set time — SignalR targets
must use `current_session`.

### D6. Hard-delete stale reminders at startup

`ReminderDefinitionStore` on load: any row whose on-disk shape doesn't
deserialize under the new protobuf schema gets dropped with a warning.
No migration code. Netclaw N=1; the user re-creates reminders after
upgrade.

Alternatives considered: in-place migration. Rejected — pointless
complexity for a single user.

### D7. `deliveryRequired` default = `true`

Per-ask intent: silence-on-failure is surprising; failure-is-loud is
the posture we want by default. `deliveryRequired = false` exists but
the LLM must explicitly opt in (e.g., audit tasks).

## Risks / Trade-offs

- **Risk**: `ReminderDeliveryObserved` requires new plumbing in
  `ChannelPipeline`'s outbound stage. → **Mitigation**: single-actor
  signal path, no cross-actor protocol changes; unit-testable in
  isolation.
- **Risk**: Gateway post API latency plus observer-signal timeout could
  exceed `DefaultAckTimeout` and produce spurious redelivery. →
  **Mitigation**: dedicated delivery-observed timeout (e.g., 30s) set
  longer than `DefaultAckTimeout`; make it an `internal const` on
  `ReminderExecutionActor` with dimensions obvious from the name.
- **Risk**: Stale reminders are deleted at startup without warning the
  operator. → **Mitigation**: emit an `OperationalAlert` with the count
  and list of dropped reminder IDs so the one operator can re-create
  them; log at `Warning` for visibility.
- **Risk**: Redelivery after `ReminderDeliveryObserved` miss could
  double-post to Slack if the first post actually succeeded but the
  signal was lost. → **Mitigation**: the existing
  `ProcessedReminderIds` dedup set on the session catches redeliveries
  at the mailbox; for `current_session` the agent's LLM context also
  already reflects the prior turn so a duplicate post is rare.
  Out-of-band: Slack's own thread-TS idempotency on the outbound post
  (same `thread_ts` + identical body within a short window) is relied
  upon as a best-effort second layer — not guaranteed.
- **Trade-off**: "crash after `AckAsync` succeeds but before
  `TurnRecorded` persists" window unchanged. Same accepted tradeoff
  documented in existing spec; no new failure class.
- **Trade-off**: transport-keyed resolver interface adds a property
  every resolver implementer must return, but today there's only one
  (Slack). Cost is one line.

## Failure Modes

| Failure | Envelope acked? | Alert? | Redelivered? |
|---------|-----------------|--------|--------------|
| `current_session` — `CommandAck` received, `ReminderDeliveryObserved` received | Yes | No | No |
| `current_session` — `CommandAck` received, `ReminderDeliveryObserved` times out (`deliveryRequired=true`) | No | Yes | Yes |
| `current_session` — `CommandAck` received, `ReminderDeliveryObserved` times out (`deliveryRequired=false`) | Yes | No | No |
| `current_session` — Ask<CommandAck> times out or `CommandNack` | No | Yes | Yes |
| `channel` — notification tool called successfully, session completes | Yes | No | No |
| `channel` — notification tool never called / failed (`deliveryRequired=true`) | Yes (from mgr; Mode A still eager-acks envelope) | Yes (execution marked failed, failure counter increments) | No (see D8 below) |
| `none` — session completes | Yes | No | No |
| Stale schema on disk at startup | N/A | Yes | N/A |

### D8. Mode A (`channel` / `none`) envelope ack timing — unchanged

For `delivery.kind ∈ {channel, none}`, `ReminderManagerActor` continues
to call `_client.AckAsync(envelope)` eagerly after spawning the
execution actor, as today. The execution's own success/failure signal
flows through `ReminderExecutionCompleted` and the existing
`FailurePauseThreshold` / `OperationalAlert.ReminderExecutionFailed`
machinery.

Rationale: Mode A execution is synchronous with respect to the tool's
observable outputs (`ExecutionOutputAccumulator` tracks the notification
tool call). The existing path already converts a missed notification
into `ReminderExecutionCompleted(success=false)`; the envelope ack
semantic is orthogonal to that. Holding the envelope open across an
entire LLM turn would starve Akka.Reminders' delivery budget without
adding meaningful recovery semantics.

`current_session` is different — the session actor's in-memory receipt
(`CommandAck`) is *not* equivalent to delivery; that's the gap we're
closing with `ReminderDeliveryObserved`.

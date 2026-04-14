# Design: reminder-session-reentry

## Context

Netclaw uses `Aaron.Akka.Reminders 0.6.0-beta2` to schedule deferred work
requested by the LLM via a `set_reminder` tool. Today `ReminderExecutionActor`
runs every reminder in an isolated session keyed `reminder/{id}/{fireTs}` and
relies on the LLM to post results outward via `send_slack_message` or similar
tools. This works for external notification ("ping #ops when the build fails")
but breaks for the more interesting check-back use case ("in 5 minutes, look
at PR #123 again and continue the conversation") because:

1. Results land in an isolated session, not the session that requested them.
2. `SetReminderTool` papers over the gap by extracting `context.SessionId` into
   a synthetic `ReportToChannel`, which fails Slack ACL validation and coaches
   the LLM to call `send_slack_message` with a target that was never resolved
   by the target-resolver (issue #660).
3. Even without that defect, the original session's output transport binding
   (`SlackThreadBindingActor`, TUI binding, SignalR binding) may have
   passivated between the reminder being set and fired; there is no way to
   reactivate it on demand.
4. `ReminderManagerActor.HandleReminderFiredAsync` acks the Akka.Reminders
   envelope immediately after `StartExecution`, collapsing the package's
   at-least-once guarantee into fire-and-forget — exactly where we'd most
   want the retry behavior.

The spike of `LlmSessionActor` (phase-1 investigation) found that the existing
`SendUserMessage` ingress path already does everything Mode B needs:
`HandleIncomingUserMessage` already fires `TryReplyAck()` after in-memory
state update; `CommandAck`/`CommandNack` are the existing ack types;
`MessageSource` is per-message ephemeral metadata that can carry a transient
dedup key; `JoinSession` is already how any subscriber attaches to a session's
output stream. The design below reuses all of this rather than inventing new
commands.

Netclaw has no users yet, so this design ignores backward compatibility
concerns for persisted reminder definitions — broken reminders from the
issue-#660 path will simply be reset by operators who encounter them. Protobuf
evolution on `TurnRecorded` remains additive (ProtoMember 5) because that
costs nothing and is good hygiene.

## Goals / Non-Goals

**Goals:**

- Fix issue #660 so `set_reminder` without `reportToChannel` works when
  invoked from a Slack thread, TUI session, or SignalR session.
- Establish **session re-entry** as a first-class mode for reminders: a
  reminder fires → a new turn is deposited into the *originating* session's
  mailbox → the session rehydrates from Akka.Persistence if idle → processes
  the turn normally → output flows back through the original transport.
- Close the eager-ack gap: Mode B holds the Akka.Reminders envelope open
  until the target session replies `CommandAck`. On timeout or `CommandNack`,
  the envelope stays un-acked and Akka.Reminders redelivers per its built-in
  policy (no custom retry layer).
- Provide an idempotency boundary so redelivery does not double-process a
  reminder turn that was successfully accepted.
- Establish a generic `ISessionTransportReanimator` contract so any channel
  (Slack, TUI, SignalR, future adapters) can be reactivated from outside an
  inbound event. This is the same infrastructure the drain-on-shutdown
  follow-up will reuse.
- Surface the Akka.Reminders tunables that already exist in 0.6.0-beta2
  (`AckTimeout`, `MaxDeliveryAttempts`, `MaxDeliveryWindow`) as Netclaw
  config so operators can tune redelivery behavior.

**Non-Goals:**

- Durable ingress queue on `LlmSessionActor` (session-wide feature; belongs
  in the drain-on-shutdown follow-up, issues #403 / #419).
- Automatic shutdown-drain via self-reminder. The reanimation contract makes
  this possible as a follow-up, but it is not implemented here.
- Output delivery to disconnected TUI or SignalR clients. Reminder turns
  persist into session state normally; clients see them when they reconnect.
  Documented as a known limitation.
- Backward compatibility for persisted reminder definitions. Netclaw has no
  users yet.
- Upgrade or modification to `Aaron.Akka.Reminders 0.6.0-beta2`. DLL
  inspection confirms the needed machinery is already present.

## Decisions

### D1. Reuse `SendUserMessage` ingress instead of a new `EnterSessionFromReminder` command

**Decision**: Mode B delivers reminders via the existing `SendUserMessage` →
`HandleIncomingUserMessage` → `TryReplyAck` path. Reminder provenance rides
on two new fields: an ephemeral `MessageSource.ReminderId` (dedup key) and
a persistent `TurnRecorded.SourceReminderId` (ProtoMember 5, additive).

**Alternatives considered**:

- *New `EnterSessionFromReminder` command with its own handler, persistent
  `ReminderDepositSeen` event, and orphan-deposit recovery logic.* Originally
  in the plan, rejected after spiking `LlmSessionActor` — the existing
  user-message path already fires an ack after in-memory state update, which
  is the signal `ReminderExecutionActor` needs. Adding a parallel persistent
  event type would duplicate recovery logic and expand the journal surface
  for no durability benefit the existing path doesn't already provide.
- *Overload `MessageSource` with a full `Reminder` discriminated union.*
  Rejected as over-engineering. A single `string? ReminderId` on
  `MessageSource` is sufficient and matches how other transient correlation
  fields (`TurnId`, `MessageId`) are already carried.

**Rationale**: Minimizes new surface area, matches the user's framing of
reminders as "deposit a new message in the mailbox," and composes naturally
with the existing drain/restart mechanism. The ack semantic is "session
accepted the message and will process it" — explicitly *not* "durably
committed" — which matches how `SendUserMessage` already works today. The
crash-during-processing failure mode is the same as for regular user
messages and is subsumed by the drain-on-shutdown follow-up.

### D2. Dedup via `TurnRecorded.SourceReminderId` rebuilt on recovery

**Decision**: Persist `SourceReminderId` (format: `"{reminderId}:{fireTs}"`)
as an optional field on `TurnRecorded`. `SessionState` maintains an
`ImmutableHashSet<string> ProcessedReminderIds` folded in the
`Apply(TurnRecorded)` handler. `HandleIncomingUserMessage` (and the
`Processing`-phase `Command<SendUserMessage>` buffer handler) pre-checks
`cmd.Source?.ReminderId` against this set and replies `CommandAck` without
processing on a hit.

**Alternatives considered**:

- *New `ReminderDepositSeen` persistent event separate from `TurnRecorded`.*
  Rejected (see D1). Doubles journal events per reminder-originated turn
  for no benefit.
- *In-memory-only dedup (no persistence).* Rejected — would lose dedup
  across daemon restarts. Akka.Reminders redelivers across restarts, so
  the dedup ledger must also survive.
- *Bounded LRU instead of unbounded set.* Deferred. For MVP, one string
  per reminder-originated turn is cheap (tens of KB even at thousands of
  reminders). If it becomes a problem a follow-up can bound to
  `MaxDeliveryWindow × 2` worth of entries.

**Rationale**: Reuses the existing `TurnRecorded` event stream, which is
already the canonical record of "things the session processed." The dedup
set reconstructs naturally on recovery with zero new replay logic. Additive
protobuf evolution is backward-compatible with any existing journals (Netclaw
has none yet, but the hygiene is free).

### D3. `ISessionTransportReanimator` registry, not per-reminder logic

**Decision**: Introduce an `ISessionTransportReanimator` interface with one
method `Task EnsureBindingAsync(SessionId, CancellationToken)`, plus a
`SessionTransportRegistry` singleton mapping `ChannelType → reanimator`. Each
channel project registers its implementation via DI at host startup.
`ReminderExecutionActor` in Mode B looks up the reanimator by
`ReminderDefinition.OriginChannelType` and awaits its ensure call before
dispatching the `SendUserMessage`.

**Alternatives considered**:

- *Hardcoded Slack-only path in `ReminderExecutionActor`.* Rejected — the
  user explicitly chose broad scope upfront. Slack-only would leave TUI and
  SignalR reminders silently broken in the same way.
- *Session actor discovers and reactivates its own transport via a
  reverse-lookup on a persistent `TransportMetadata` event.* Rejected as too
  invasive. Would require every inbound path to stamp its metadata on the
  session, which pollutes `LlmSessionActor`'s contract with transport
  specifics.
- *`ReminderExecutionActor` parses `SessionId` format heuristically to pick a
  transport.* Rejected — couples the execution actor to the session-ID naming
  conventions of every channel. Using `OriginChannelType` captured at set
  time is explicit.

**Rationale**: Cleanly separates "wake up the output path" (transport
concern) from "deposit a new turn" (session concern). The contract is small
enough to be trivially implemented as a no-op by channels without durable
outbound (TUI) and best-effort by channels with transient clients (SignalR).
Slack carries the real work: a new idempotent
`SlackGatewayActor.EnsureThreadBinding` message that looks up or creates the
conversation → thread binding actor chain, reusing the existing binding
materialization code. The same infrastructure is what the drain-on-shutdown
follow-up will call on startup to reactivate sessions.

### D4. Envelope ack gated on session ack, delegated to execution actor

**Decision**: `ReminderManagerActor.HandleReminderFiredAsync` acks the
Akka.Reminders envelope eagerly for Mode A (unchanged) and defers the ack for
Mode B. In Mode B, the envelope reference is passed to
`ReminderExecutionActor`, which acks it back through the manager via a new
`AckReminderEnvelope` message after receiving `CommandAck` from the target
session. On timeout, `CommandNack`, or reanimator failure, the execution
actor reports failure and does **not** request an ack; Akka.Reminders'
built-in `AckTimeout` / `ProcessAckTimeouts` / `MaxDeliveryAttempts`
machinery redelivers.

**Alternatives considered**:

- *Ack in the manager after `Ask`ing the session directly from
  `HandleReminderFiredAsync`.* Rejected — would block the manager's mailbox
  on the Ask. The manager is a singleton and must stay responsive to other
  reminder events.
- *Custom retry loop in the manager with a sidecar table of un-acked
  envelopes.* Rejected — Akka.Reminders already implements this, we just
  need to let it do its job by not eager-acking.
- *Wait for `TurnCompleted` before acking.* Rejected — LLM turns commonly
  exceed any reasonable `AckTimeout`. The session's in-memory
  `CommandAck` is the right boundary because it is fast (no persist
  roundtrip) and semantically "session will process this."

**Rationale**: Uses the existing Akka.Reminders retry machinery (which is
already in `Aaron.Akka.Reminders 0.6.0-beta2` — confirmed via DLL
inspection) without building a parallel retry layer. Keeps the manager
responsive. The execution actor is a short-lived child, so Ask timeouts are
locally scoped.

### D5. Config surface in `ReminderConfig`, schema-synced

**Decision**: Add three properties to `ReminderConfig`: `AckTimeout`
(TimeSpan), `MaxDeliveryAttempts` (int), `MaxDeliveryWindow` (TimeSpan).
Wire them into the `ReminderClient` construction in
`NetclawAkkaHostingExtensions`. Default values match Akka.Reminders' own
defaults (read from the package at implementation time). Update
`netclaw-config.v1.schema.json` per the Configuration Schema Sync Rule in
CLAUDE.md, using string `default` values with named enum discriminators
where applicable so that `netclaw doctor --fix` can auto-correct stale
configs.

**Alternatives considered**:

- *Hardcode the Akka.Reminders defaults and never expose them.* Rejected —
  operators need to be able to tune redelivery behavior for their
  deployment (e.g., longer `AckTimeout` when replay latency is high on a
  congested journal).
- *Put the knobs on individual reminders via `set_reminder`.* Rejected as
  over-engineering for MVP. Per-reminder tunables can come later if there's
  a real demand signal.

**Rationale**: Standard Netclaw config pattern. No novel surface area.

## Risks / Trade-offs

**[R1] Crash-during-processing loses the reminder turn** → Mitigation: same
failure mode as regular user messages today. Subsumed by the
drain-on-shutdown follow-up (issues #403 / #419). Documented in the
`netclaw-scheduling` spec delta under "delivery guarantees." Accepted trade-off.

**[R2] Journal replay latency on rehydrate can exceed a short `AckTimeout`**
→ Mitigation: default `AckTimeout` to 30 seconds (well above expected
rehydrate p99 on SQLite journals). Log rehydrate duration so operators can
tune. Config knob is schema-validated.

**[R3] Dedup ledger growth unbounded for long-lived sessions with many
reminders** → Mitigation: one short string per reminder turn (~30 bytes);
order-of-magnitude negligible compared to transcript size. Follow-up can
bound to `MaxDeliveryWindow × 2` entries if needed. Not a blocker.

**[R4] Mode B output "invisible" on disconnected TUI or SignalR clients** →
Mitigation: the reanimator no-ops when no client is connected; reminder
turns still persist into session state and are visible on reconnect.
Documented in the spec under "delivery to absent transport." Accepted
trade-off.

**[R5] Ephemeral vs persistent dedup mismatch** — if a turn starts
processing but fails mid-LLM-call without persisting `TurnRecorded`, a
redelivery will not be in the dedup set and will re-process → Mitigation:
this is the *desired* behavior. Failed turns should retry. Add a test case
proving this.

**[R6] Reanimator race** — a reminder fires for a session that is
concurrently being re-created by an inbound event. Two actors race to
create the same binding → Mitigation: `EnsureThreadBinding` is idempotent
by contract. The underlying gateway/binding actor creation path is already
idempotent (`GenericChildPerEntityParent` routes by ID). Test case: two
`EnsureThreadBinding` calls in parallel must produce exactly one binding
actor.

**[R7] Audience escalation via reminder re-entry** — a reminder could be
created in a low-audience session and then deliver into a higher-audience
context if the session's effective audience shifts → Mitigation: the
reminder's stored audience (enforced by the archived
`reminder-audience-authorization` change) is used to construct the
`MessageSource`, not the live session's current audience. Validated by a
test case.

## Actor Topology (after this change)

```
ReminderManagerActor (singleton)
 └── ReminderExecutionActor (child-per-execution, short-lived)
       ├── Mode A: ISessionPipeline.CreateAsync(reminder/{id}/{ts}) — unchanged
       └── Mode B:
             1. SessionTransportRegistry.LookUp(OriginChannelType)
             2. reanimator.EnsureBindingAsync(originalSessionId)
                  └── (Slack) SlackGatewayActor ! EnsureThreadBinding
                         └── existing SlackConversationActor / SlackThreadBindingActor
             3. sessionManager.Ask<CommandAck>(SendUserMessage{
                   SessionId = originalSessionId,
                   Source = MessageSource{ ReminderId = "{id}:{fireTs}", ChannelType = OriginChannelType, Audience = storedAudience }
                })
             4. On ack: Context.Parent ! AckReminderEnvelope(envelope)
                On timeout/nack/failure: Context.Parent ! ReminderExecutionCompleted(failure, ackEnvelope=false)
```

The original `SlackThreadBindingActor` — whether freshly created by
`EnsureThreadBinding` or already alive — is a normal session subscriber and
receives the reminder turn's streaming output through the existing
`JoinSession` → `SessionOutput` fan-out. No new output plumbing.

## Migration Plan

None. Netclaw has no users. This change ships with the next dev build.

## Open Questions

None blocking. All design decisions are resolved; implementation can proceed
to the specs and tasks artifacts.

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

### D3. Route Mode B through each channel's existing inbound path; no new abstraction

**Decision**: Mode B delivers reminders by routing them through each
channel's existing inbound handling path — the same path that wakes up
passivated sessions every time a real user message arrives. A shared
protocol message `DeliverTrustedSessionTurn(SessionId, Content,
MessageSource)` in `Netclaw.Actors.Protocol` is the channel-agnostic
envelope. Each channel's gateway adds one handler that parses the
SessionId and runs the same lookup-or-create chain as its inbound-event
handler, tagging the `ChannelInput` as trusted so the channel-level
inbound ACL is bypassed (the reminder's stored audience was validated at
minting time).

The daemon currently hosts two channels, both with full server-side
gateway infrastructure:

- **Slack** (`Netclaw.Channels.Slack`) — `SlackGatewayActor` +
  `SlackConversationActor` + `SlackThreadBindingActor`, session keyed
  `{channelId}/{threadTs}`. Server-side binding lifecycle is decoupled
  from any client — there is no "user connection" to track; the Slack
  API is always reachable via the outbound client.
- **SignalR** (`src/Netclaw.Daemon/Gateway/`) — `SessionHub` (ASP.NET
  Core SignalR) + `SessionRegistry` + `SignalRGatewayActor` +
  `SignalRSessionActor`, session keyed `signalr/{guid}`. TUI
  (`netclaw chat`) is one client of this hub, not a separate channel —
  `DaemonClient` wraps a `HubConnection`. `SessionRegistry` preserves
  the caller's self-identifying `ChannelType` value (`ChannelType.Tui`
  for TUI clients) but both `Tui` and `SignalR` route through the same
  gateway chain on the daemon side. For the spec TUI is not a first-class
  channel; it is a client of SignalR.

Both gateways gain a `Receive<DeliverTrustedSessionTurn>` handler that
reuses the channel's existing lookup-or-create chain. The reminder
dispatcher's Mode B switch has two cases: `ChannelType.Slack` →
`SlackGatewayActor`; `ChannelType.Tui | ChannelType.SignalR` →
`SignalRGatewayActor`. Any other `OriginChannelType` (`Headless`,
`Webhook`, `Reminder`, `null`) has no gateway and Mode B is rejected at
`set_reminder` time.

**Connected-client semantics for SignalR are free from the existing
disconnect path.** When a TUI client disconnects today, `SessionRegistry`
tells `SignalRSessionActor` `ShutdownSignalRSession`, which calls
`Context.Stop(Self)` and tears down the bridge's input queue and output
subscriber. The underlying `LlmSessionActor` (a separate persistent actor
routed by `GenericChildPerEntityParent` — not a child of
`SignalRSessionActor`) **keeps running**: any in-flight tool call
completes, `TurnRecorded` persists to the journal, streaming output goes
to the now-empty subscriber slot and is dropped per
`OverflowStrategy.DropHead`. When the user reconnects via
`ResumeSessionAsync`, they see the completed turn in the transcript.

Reminder-on-disconnected-client is the identical scenario with a
different trigger: deliver the reminder's `SendUserMessage` to the
session, let the underlying session actor process it, persist the turn,
drop the streaming output if nobody's subscribed, user sees it on
reconnect. No new disconnect-handling code is needed in the SignalR
gateway — the existing subscriber fan-out handles the
connected/disconnected cases uniformly. The gateway's
`DeliverTrustedSessionTurn` handler can just run its lookup-or-create
chain and let whatever subscribers exist (or don't) consume the output.

A tiny `ChannelInput.AckTarget` extension (optional `IActorRef`,
propagated through `ChannelPipeline.MapToCommand` as the `Tell` sender)
lets the session's existing `TryReplyAck()` reply directly to the
reminder dispatcher. Regular inbound messages leave `AckTarget = null`,
`MapToCommand` falls back to `ActorRefs.NoSender`, and the existing
fire-and-forget user-message semantics are preserved exactly.

**Alternatives considered**:

- *`ISessionTransportReanimator` interface + `SessionTransportRegistry`.*
  Rejected after review. The inbound path already handles lookup-or-create
  of binding actors, subscribes them to session output, and triggers
  Akka.Persistence rehydration as natural side effects. Introducing a
  parallel "ensure binding" abstraction duplicates well-tested machinery
  behind ceremony. Also split the delivery into two operations (ensure
  binding, then deliver) that had to be coordinated, where the existing
  inbound path is atomic.
- *Treat TUI and SignalR asymmetrically from Slack, with
  direct-to-session-manager fallback for non-Slack reminders.* Rejected
  after verifying that SignalR *is* a first-class channel with a gateway
  chain parallel to Slack's; the earlier draft was based on an incorrect
  assumption about the codebase. Symmetric handling is simpler and
  uniform.
- *Synthesizing a fake `SlackInboundMessage` and telling the gateway.*
  Rejected — `SlackInboundMessage` carries fields that don't apply to a
  trusted delivery (userId, slack event ID) and goes through inbound dedup
  and ACL checks that aren't meaningful for a reminder. A dedicated
  handler is cleaner than lying with a synthetic payload.
- *Subscriber-based ack (reminder dispatcher joins the session's output
  stream and watches for a "turn started" event).* Rejected — fragile
  correlation (which turn is which?) and adds a new output event type just
  for ack. `ChannelInput.AckTarget` extension is one-field-and-one-line
  and works for every channel uniformly.

**Rationale**: This is the pattern the user pushed toward: "routing the
message through the channel infrastructure and having that reactivate the
session, even though there wasn't really a user message there." Both
Slack and SignalR inbound paths already do exactly what's needed —
lookup-or-create the conversation/session bridge, materialize the
pipeline, subscribe to session output, deliver the payload. Every
long-running turn already exercises them in both directions and they are
the best-tested code paths in the channel layer. Reusing them verbatim
(with one deviation: skip ACL because audience is already validated) is
the minimum change that gets reminder re-entry working correctly, and
uniform across both channels.

### D4. Envelope ack gated on session receipt via `IReminderClient.AckAsync`; gateway does not reply

**Decision**: Mode B envelopes are acked via
`IReminderClient.AckAsync(envelope)` — the public documented API — called
by `ReminderExecutionActor` after it has confirmed the target session
has received the reminder turn. The ack semantic is "the target session
has accepted the message" (in-memory state updated), not "the gateway
has accepted it for delivery."

Concretely, the dispatch chain is:

1. `ReminderManagerActor.HandleReminderFiredAsync` (Mode B branch)
   spawns `ReminderExecutionActor`, passes the `ReminderEnvelope`
   explicitly as a constructor arg. Does NOT call
   `_client.AckAsync(envelope)`.
2. The execution actor acquires `IReminderClient` via
   `ReminderClientExtension.Get(Context.System)` at startup (standard
   extension pattern).
3. The execution actor builds `DeliverTrustedSessionTurn(SessionId,
   Content, MessageSource)` and issues `Ask<CommandAck>` against the
   appropriate channel gateway (`SlackGatewayActor` or
   `SignalRGatewayActor`, selected by `OriginChannelType`). The Ask's
   temp actor becomes `Sender` when the gateway handler runs.
4. The gateway handler reads `Sender` (the Ask temp actor), runs the
   shared lookup-or-create helper for its channel's inbound chain, and
   offers a `ChannelInput { AckTarget = Sender, ... }` into the
   pipeline queue via `inputQueue.OfferAsync(...)`. **The gateway handler
   does not reply itself** — the reply is expected to come from the
   downstream session via `AckTarget`. The gateway only replies
   directly when `OfferAsync` returns a non-`Enqueued` result (queue
   closed, backpressure dropped, failure), in which case it replies
   `CommandNack` to signal that the channel refused the message.
5. `ChannelPipeline.MapToCommand` propagates `input.AckTarget` as the
   sender when it `Tell`s `SendUserMessage` to the session manager.
6. `LlmSessionActor.HandleIncomingUserMessage` adds the user message to
   in-memory state and calls `TryReplyAck()`, which replies
   `CommandAck` to `Sender` — which is the Ask's temp actor. The
   execution actor's `await Ask<CommandAck>(...)` completes.
7. The execution actor calls
   `await _client.AckAsync(_envelope)`, inspects the
   `ReminderAckResponse.ResponseCode`, logs on non-success, and then
   tells `Context.Parent` a `ReminderExecutionCompleted(success=true)`
   for bookkeeping (failure counters, history).
8. The session's turn execution proceeds asynchronously. `TurnRecorded`
   is persisted whenever the LLM turn completes.

On any failure before the session acks (gateway refuses with
`CommandNack`, Ask times out because the queue drained slowly or the
session never replied, transport exception), the execution actor does
**not** call `AckAsync`. It tells the parent
`ReminderExecutionCompleted(success=false)` with an error message, then
stops. The un-acked envelope is redelivered by `Aaron.Akka.Reminders`
per its configured `AckTimeout` / `ProcessAckTimeouts` /
`MaxDeliveryAttempts`.

**Why session-gated instead of gateway-gated**: a gateway-level ack (the
simpler alternative — "reply as soon as I've accepted the ChannelInput
into the pipeline queue") leaves a narrow crash window between "gateway
offered to queue" and "stream stage processed the ChannelInput and
reached the session actor." That window is in-process and small, but
it's a delivery hole that Design A closes for free because we already
have `ChannelInput.AckTarget` in the plan. Closing this gap costs us
nothing — the gateway handler just doesn't reply itself and lets the
session reply flow back via `AckTarget`. See "Known failure modes and
explicit tradeoffs" below for the documented gaps that remain.

**Why `_client.AckAsync(envelope)` instead of reply-to-sender with a
hand-constructed `ReminderAck`**: `IReminderClient.AckAsync` is the
public documented API. Its implementation in
`Aaron.Akka.Reminders 0.6.0-beta2` is itself an
`Ask<ReminderAckResponse>` against the scheduler proxy, so it's already
Ask-based with response-code visibility. Using the client insulates us
from any future changes to the library's internal actor topology or ack
message format. Acquiring the client from the ActorSystem extension is
a one-line standard pattern, not "plumbing."

**Alternatives considered**:

- *Gateway-level ack ("reply when the pipeline queue has accepted the
  ChannelInput").* Rejected — leaves an in-process crash window between
  gateway-offer and stream-stage-processing that Design A closes for
  free with `ChannelInput.AckTarget`.
- *Reply-to-sender from the execution child with a hand-constructed
  `ReminderProtocol.ReminderAck`, bypassing `IReminderClient`.*
  Rejected — `IReminderClient.AckAsync` is the public supported API and
  insulates us from implementation changes. The "no client plumbing"
  argument was premature optimization of a non-problem — extensions are
  the designed-for singleton-lookup pattern.
- *Bouncing the ack through `ReminderManagerActor` via a new
  `AckReminderEnvelope` message.* Rejected — unnecessary mailbox hop,
  serializes ack processing on the manager's singleton mailbox for no
  benefit.
- *Wait for `TurnCompleted` before acking.* Rejected — LLM turns
  commonly exceed any reasonable `AckTimeout`. The session's in-memory
  `CommandAck` (fired after `_state.AddUserMessage` but before the LLM
  call) is the right boundary because it is fast and semantically
  "session will process this."
- *Custom retry loop in the manager with a sidecar table of un-acked
  envelopes.* Rejected — Akka.Reminders already implements this, we
  just need to let it do its job by not eager-acking.

### Known failure modes and explicit tradeoffs

This section is normative — the spec's "Reminder delivery guarantees"
requirement references it.

**Guaranteed** (at-least-once, dedup-safe):

- *Crash before gateway receives `DeliverTrustedSessionTurn`*: envelope
  un-acked, Akka.Reminders redelivers on next fire.
- *Crash after gateway offered to pipeline queue but before stream stage
  processed the `ChannelInput`*: Ask temp actor never receives a reply
  from the session, execution actor's Ask times out without calling
  `AckAsync`, envelope un-acked, Akka.Reminders redelivers. Design A
  closes this gap that Design B would have left open.
- *Crash after session received the message but before execution actor
  calls `AckAsync`*: envelope un-acked, Akka.Reminders redelivers. On
  redelivery, if `TurnRecorded` already persisted, the session's
  `ProcessedReminderIds` dedup catches it. If `TurnRecorded` has not yet
  persisted, the redelivery is processed as a fresh turn (desired
  retry).
- *Ack message lost in flight between execution actor and scheduler
  proxy*: Akka.Reminders redelivers on `AckTimeout`, dedup catches the
  duplicate.

**Not guaranteed, explicit tradeoff**:

- **Crash after execution actor calls `AckAsync` and before the session
  persists `TurnRecorded`**: the envelope is acked from Akka.Reminders'
  perspective, but the session may have only reached in-memory state
  (via `_state.AddUserMessage`) and not yet written `TurnRecorded` to
  the journal. If the daemon crashes in this window — which spans the
  entire LLM turn execution, potentially minutes — the reminder turn
  is lost on restart.

  This is **the same failure mode that applies to every regular
  `SendUserMessage` today**. A normal Slack user sending a message
  faces the identical window: `TryReplyAck` fires after in-memory state
  update, the LLM call runs, and a crash before `TurnRecorded` loses
  that message. We are not making reminders worse than user messages.

  Fixing this gap requires a **durable ingress queue on
  `LlmSessionActor`** (persist user messages on receipt, mark them
  processed when `TurnRecorded` is written) — a session-wide change
  that affects every `SendUserMessage` code path, not reminders
  specifically. That work belongs in the drain-on-shutdown follow-up
  (issues #403 and #419) where it can be designed holistically across
  all ingress.

  **Explicit tradeoff decision**: accept this gap for Mode B reminders
  in this change. Document it in the spec under "Reminder delivery
  guarantees" as an explicit out-of-scope item. Operators who need
  stronger guarantees should track the drain-on-shutdown follow-up.

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
Mitigation: direct session-manager delivery persists the turn into session
state, which is the authoritative record; clients see it on reconnect.
Documented in the spec under "delivery to absent transport." Accepted
trade-off.

**[R5] Ephemeral vs persistent dedup mismatch** — if a turn starts
processing but fails mid-LLM-call without persisting `TurnRecorded`, a
redelivery will not be in the dedup set and will re-process → Mitigation:
this is the *desired* behavior. Failed turns should retry. Add a test case
proving this.

**[R6] Audience escalation via reminder re-entry** — a reminder could be
created in a low-audience session and then deliver into a higher-audience
context if the session's effective audience shifts → Mitigation: the
reminder's stored audience (enforced by the archived
`reminder-audience-authorization` change) is used to construct the
`MessageSource`, not the live session's current audience. Validated by a
test case.

**[R7] Race between reminder delivery and concurrent inbound** — a
reminder fires for a session while a real inbound event is also
materializing the same thread binding → Mitigation: the existing
conversation and thread binding actor lookup-or-create chain is already
idempotent; Akka actor supervision guarantees a single child per entity
key. Two parallel attempts to materialize the same binding produce exactly
one actor. No new race surface is introduced.

## Actor Topology (after this change)

```
ReminderManagerActor (singleton)
 └── ReminderExecutionActor (child-per-execution, short-lived)
       ├── Mode A: ISessionPipeline.CreateAsync(reminder/{id}/{ts}) — unchanged
       └── Mode B:
             let msg = new DeliverTrustedSessionTurn {
                 SessionId = originalSessionId,
                 Content = BuildPrompt(_definition),
                 Source = new MessageSource {
                     ReminderId = "{id}:{fireTs}",
                     ChannelType = _definition.OriginChannelType,
                     Audience = storedAudience,
                     Principal = VerifiedAutomation,
                     Provenance = {
                         SourceKind = "reminder",
                         TransportAuthenticity = LocalProcess,
                         PayloadTaint = Trusted
                     }
                 }
             }

             switch (_definition.OriginChannelType):

               case ChannelType.Slack:
                 _slackGateway.Ask<CommandAck>(msg)
                   │
                   ▼
                 SlackGatewayActor.Receive<DeliverTrustedSessionTurn>:
                   parses SessionId → (channelId, threadTs)
                   runs existing lookup-or-create helper:
                     → SlackConversationActor (by channelId)
                       → SlackThreadBindingActor (by threadTs)
                         → materializes pipeline, joins session output
                         → offers ChannelInput{ AckTarget = Sender }
                             (SlackAclPolicy.EvaluateInbound bypassed — trusted provenance)

               case ChannelType.Tui | ChannelType.SignalR:
                 _signalRGateway.Ask<CommandAck>(msg)
                   │
                   ▼
                 SignalRGatewayActor.Receive<DeliverTrustedSessionTurn>:
                   parses SessionId → signalr/{guid}
                   runs existing lookup-or-create helper shared with
                     SessionRegistry's StartSignalRSession path:
                     → SignalRSessionActor (if a client is connected)
                         → existing pipeline subscriber receives streaming output
                     → else: no bridge materialized; session processes and
                       persists the turn regardless, output is dropped per
                       Source.ActorRef OverflowStrategy.DropHead
                   offers ChannelInput{ AckTarget = Sender } into whichever
                   path exists

               default (ChannelType.Headless, Webhook, Reminder, null):
                 // Rejected at set_reminder time; cannot reach here.

             Both gateways: session.HandleIncomingUserMessage → TryReplyAck
               → reply flows through pipeline sender chain
               → reminder dispatcher receives CommandAck

             On CommandAck (reply from LlmSessionActor via AckTarget):
               await _client.AckAsync(envelope)       // public IReminderClient API
               Context.Parent ! ReminderExecutionCompleted(success=true)
             On timeout/CommandNack/gateway exception:
               // do NOT call AckAsync
               Context.Parent ! ReminderExecutionCompleted(success=false, error)
               (envelope un-acked → Akka.Reminders redelivers per configured policy)

  The gateway handler does NOT reply to the Ask directly. It reads
  `Sender` (the Ask temp actor), constructs ChannelInput with
  `AckTarget = Sender`, and offers to the pipeline queue. The reply
  flows from the LlmSessionActor's TryReplyAck back through AckTarget
  to the Ask's temp actor, which completes the execution child's Task.
  Gateway replies CommandNack directly only when OfferAsync returns
  a non-Enqueued result (queue closed, dropped, failure).
```

In both channels, the freshly materialized (or already-live) binding
actor is a normal session subscriber — it receives the reminder turn's
streaming output through the existing `JoinSession` → `SessionOutput`
fan-out and delivers it to the client transport (Slack API or SignalR
hub). For SignalR with no connected client, the turn still persists into
session state via the existing `LlmSessionActor` → `TurnRecorded` path;
the user sees it on next `ResumeSessionAsync`. This mirrors the current
disconnect semantics: when a TUI client exits mid-tool-call today, the
session keeps running, the tool completes, and `TurnRecorded` is
persisted without anyone subscribed.

## Migration Plan

None. Netclaw has no users. This change ships with the next dev build.

## Open Questions

None blocking. All design decisions are resolved; implementation can proceed
to the specs and tasks artifacts.

# Design: reminder-session-reentry

## Context

Netclaw uses `Aaron.Akka.Reminders 0.6.0-beta2` to schedule deferred work
requested by the LLM via a `set_reminder` tool. Today `ReminderExecutionActor`
runs every reminder in an isolated session keyed `reminder/{id}/{fireTs}` and
relies on the LLM to post results outward via `send_slack_message` or similar
tools. This works for external notification ("ping #ops when the build fails")
but breaks for the more interesting check-back use case ("in 5 minutes, look at
PR #123 again and continue the conversation") because:

1. Results land in an isolated session, not the session that requested them.
2. `SetReminderTool` papers over the gap by extracting `context.SessionId` into
   a synthetic `ReportToChannel`, which fails Slack ACL validation and coaches
   the LLM to call `send_slack_message` with a target that was never resolved
   by the target-resolver (issue #660).
3. Even without that defect, the original session's output transport binding
   may have passivated between the reminder being set and fired; there is no
   way to re-reach it without going through the channel's normal inbound
   routing path.
4. `ReminderManagerActor.HandleReminderFiredAsync` acks the Akka.Reminders
   envelope immediately after `StartExecution`, collapsing the package's
   at-least-once guarantee into fire-and-forget — exactly where we'd most
   want the retry behavior.

The spike of `LlmSessionActor` found that the existing `SendUserMessage`
ingress path already does everything Mode B needs: `HandleIncomingUserMessage`
fires `TryReplyAck()` after in-memory state update; `CommandAck`/`CommandNack`
are the existing ack types; `MessageSource` is per-message ephemeral metadata
that can carry a transient dedup key and an ack reply target; the
`GenericChildPerEntityParent` on the SignalR gateway and the hand-rolled
`Context.Child.GetOrElse` chain on the Slack gateway both already lookup-or-
create the right child by session identity. The design below reuses all of
this — the reminder dispatcher adds exactly one new message type plus one
`Receive<>` handler per actor level in each existing routing chain, and the
actor identity schemes naturally solve the routing problem without new
abstractions.

Netclaw has no users yet, so this design ignores backward compatibility for
persisted reminder definitions. Broken reminders from the issue-#660 path will
simply be reset by operators who encounter them. Protobuf evolution on
`TurnRecorded` remains additive (new `SourceReminderId` field at
`[ProtoMember(5)]`) because that costs nothing and gives us forensic
observability ("which turns originated from which reminder") whether or not
we use it for dedup.

## Goals / Non-Goals

**Goals:**

- Fix issue #660 so `set_reminder` without `reportToChannel` works when
  invoked from a Slack thread, TUI session (SignalR-backed), or any future
  SignalR client.
- Establish **session re-entry** as a first-class mode for reminders: a
  reminder fires → the reminder dispatcher sends a `DeliverTrustedSessionTurn`
  to the originating channel's gateway → the gateway uses its existing
  inbound-routing logic (`Forward` down the hierarchy) to reach the session
  actor → the session rehydrates from Akka.Persistence if idle → processes
  the turn normally → output flows back through the original transport via
  the same subscriber machinery that handles inbound user messages.
- Close the eager-ack gap: Mode B holds the Akka.Reminders envelope open
  until the target session replies `CommandAck`. On timeout or `CommandNack`,
  the envelope stays un-acked and Akka.Reminders redelivers per its built-in
  policy (no custom retry layer).
- Expose enough Akka.Reminders tuning for operators to adjust delivery
  behavior without drowning them in knobs.
- Accept that duplicate reminder processing is a tolerable outcome in rare
  crash-window scenarios, and design accordingly (best-effort in-memory
  dedup rather than durable guarantees).

**Non-Goals:**

- Durable ingress queue on `LlmSessionActor`. A session-wide change that
  affects every `SendUserMessage` code path, belongs in the drain-on-shutdown
  follow-up (issues #403 / #419).
- Automatic shutdown-drain via self-reminder. The infrastructure this change
  builds makes it feasible as a follow-up, but it is not implemented here.
- Snapshot persistence of the dedup ledger. In-memory only; duplicates
  across snapshot recovery boundaries are acceptable.
- Any new abstraction layer for "session transport reanimation." Rejected —
  each channel's existing routing hierarchy already handles lookup-or-create
  and session rehydration as natural side effects of its inbound path.
- Backward compatibility for persisted reminder definitions. Netclaw has no
  users yet.
- Upgrade or modification to `Aaron.Akka.Reminders 0.6.0-beta2`. DLL
  inspection confirms the needed machinery is already present.
- Extracting `src/Netclaw.Daemon/Gateway/*` into a standalone
  `Netclaw.Channels.SignalR` project for architectural symmetry with
  `Netclaw.Channels.Slack`. Worth doing, filed as a follow-up issue; out of
  scope for this change.

## Decisions

### D1. Reuse `SendUserMessage` ingress; no new session command

Mode B delivers reminders via the existing `SendUserMessage` →
`HandleIncomingUserMessage` → `TryReplyAck` path. Reminder provenance rides
on two new fields: an ephemeral `MessageSource.ReminderId` (dedup key,
forensic tag) and a persistent `TurnRecorded.SourceReminderId` (`ProtoMember
5`, additive).

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
messages and is an accepted tradeoff.

### D2. Best-effort in-memory dedup, rebuilt from event replay, not snapshot-persisted

Duplicate reminder processing is an accepted tradeoff. The LLM itself can
typically detect a duplicate reminder prompt in its recent context and
respond appropriately. Dedup is a nice-to-have that catches the common "ack
lost in flight, same actor lifetime" case cheaply — not a correctness
requirement.

Concretely:

- `TurnRecorded` gains an optional `string? SourceReminderId`
  (`[ProtoMember(5)]`), populated as `{reminderId}:{fireTimestampMs}` when
  the turn originated from a reminder. Persisted in the journal.
- `SessionState` maintains an in-memory
  `ImmutableHashSet<string> ProcessedReminderIds`. `Apply(TurnRecorded evt)`
  folds `evt.SourceReminderId` into the set when non-null.
  `Apply(SessionCompacted evt)` preserves the set.
- `LlmSessionActor.HandleIncomingUserMessage` (and the `Processing`-phase
  `Command<SendUserMessage>` buffer handler) pre-checks
  `cmd.Source?.ReminderId` against the set before accepting the message. On
  a hit, reply `CommandAck` and return without processing.
- The set is **not** serialized to `SessionSnapshot`. On snapshot-based
  recovery, the set starts empty and is rebuilt from post-snapshot
  `TurnRecorded` replay via the existing `Apply` handler. Reminders that
  were processed before the snapshot are no longer in the set, but by then
  Akka.Reminders' internal `MaxDeliveryWindow` has almost certainly expired
  anyway.

**Alternatives considered**:

- *Drop dedup entirely.* Rejected in favor of keeping it as a cheap
  best-effort catch for the common case. `TurnRecorded.SourceReminderId` is
  also useful for forensics ("which turns were reminder-originated")
  independent of dedup.
- *Persist the dedup ledger to `SessionSnapshot`.* Rejected — the user has
  explicitly stated that duplicate reminder processing is acceptable. The
  additional protobuf field, snapshot round-trip tests, and recovery-path
  logic are not worth preventing a rare edge-case duplicate.
- *Bounded LRU with a TTL tied to `MaxDeliveryWindow`.* Deferred. For MVP,
  one short string per reminder turn is cheap (tens of KB for thousands of
  entries) and the set only grows within a single actor lifetime.

**Rationale**: Reuses the existing `TurnRecorded` event stream and its
recovery replay path. Zero new persistent schema surface. Matches the
"duplicates are acceptable" framing the user articulated explicitly.

### D3. Reuse existing channel routing hierarchies; no new abstraction

Mode B delivery routes through each channel's existing inbound-routing
actor hierarchy. Each actor level in the chain gets one new
`Receive<DeliverTrustedSessionTurn>` handler that mirrors the actor's
existing inbound handlers: lookup-or-create the appropriate child for this
SessionId's component, then `Forward(msg)` down. `Forward` preserves the
original `Sender` (the Ask temp actor from the reminder dispatcher's
`Ask<CommandAck>`), so by the time the message reaches the leaf binding
actor, `Sender` is still the dispatcher's temp actor.

**Slack side** (`SlackGatewayActor` → `SlackConversationActor` →
`SlackThreadBindingActor`):

1. `SlackGatewayActor.Receive<DeliverTrustedSessionTurn>`: parse
   `SessionId → (channelId, threadTs)`, lookup-or-create conversation by
   `channelId` using the same `Context.Child(name).GetOrElse(...)` pattern
   the existing `SlackInboundMessage` handler uses, `conversation.Forward(msg)`.
2. `SlackConversationActor.Receive<DeliverTrustedSessionTurn>`: lookup-or-
   create thread binding by `threadTs` (same pattern), `binding.Forward(msg)`.
   **No `SlackAclPolicy.EvaluateInbound` call** — it's a different message
   handler from the inbound-event handler, so there's no "shared code with a
   flag" situation.
3. `SlackThreadBindingActor.Receive<DeliverTrustedSessionTurn>`: read
   `Sender`, build `ChannelInput` with the reminder content, the supplied
   `MessageSource` (carrying `ReminderId`, trusted provenance, stored
   audience), and `MessageSource.AckTarget = Sender`. Offer into the
   pipeline queue via `inputQueue.OfferAsync(...)`. On non-`Enqueued` offer
   result, Tell `Sender` a `CommandNack` directly.

**SignalR side** (`SignalRGatewayActor` using `GenericChildPerEntityParent`
with `SignalRMessageExtractor` → `SignalRSessionActor`):

1. Extend `SignalRMessageExtractor.EntityId` to also match `IWithSessionId`
   as a fallback:
   ```csharp
   public override string? EntityId(object message) => message switch
   {
       ISignalRSessionMessage msg => msg.SessionId.Value,
       IWithSessionId wid => wid.SessionId.Value,
       _ => null
   };
   ```
   One-line change. `ISignalRSessionMessage` stays `internal` — no upstream
   leak. `IWithSessionId` is an existing public interface in
   `Netclaw.Actors.Protocol` that `DeliverTrustedSessionTurn` implements.
2. `SignalRSessionActor.Receive<DeliverTrustedSessionTurn>`: read `Sender`,
   build `ChannelInput` with `MessageSource.AckTarget = Sender`, offer into
   the session's pipeline queue. Same shape as the Slack binding actor's
   handler.

**Reminder dispatcher switch** (`ReminderExecutionActor` Mode B path):

```csharp
var msg = new DeliverTrustedSessionTurn(sessionId, content, source);
var gateway = ResolveGatewayFor(_definition.OriginChannelType);
var ack = await gateway.Ask<object>(msg, ReminderSettings.DefaultAckTimeout);
```

Where `ResolveGatewayFor` looks the gateway ref up in the `ActorRegistry`
at runtime (the execution actor is created by `Context.ActorOf`, not via
DI, so it can't use `IRequiredActor<T>` directly):

```csharp
private IActorRef? ResolveGatewayFor(ChannelType originChannelType) =>
    originChannelType switch
    {
        ChannelType.Slack    => registry.TryGet<SlackGatewayActorKey>(out var r)   ? r : null,
        ChannelType.Tui      => registry.TryGet<SignalRGatewayActorKey>(out var r) ? r : null,
        ChannelType.SignalR  => registry.TryGet<SignalRGatewayActorKey>(out var r) ? r : null,
        _                    => null  // rejected at set_reminder time
    };
```

Both marker keys are declared in
`src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs` alongside the existing
marker types (`SessionManagerActorKey`, etc.). The `SignalRGatewayActorKey`
type moves from `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` to
`ActorRegistryKeys.cs`; the new `SlackGatewayActorKey` is added as a
sibling; `SlackChannel.StartAsync` adds a one-line `registry.Register<
SlackGatewayActorKey>(_gateway)` call alongside its existing lifecycle
management.

**`MessageSource.AckTarget` propagation**: `MessageSource` gains an optional
`IActorRef? AckTarget` field. It is already the ephemeral, non-persisted
metadata carrier on `SendUserMessage` (`[ProtoIgnore]`), so an `IActorRef`
is safe to add. `ChannelPipeline.MapToCommand` reads
`input.Source?.AckTarget` when the stream sink `Tell`s the session manager,
using it as the sender if present or `ActorRefs.NoSender` otherwise. The
session's existing `TryReplyAck()` then replies to `Sender` (the Ask temp
actor) and the dispatcher's `Ask<CommandAck>` completes.

**Alternatives considered**:

- *`ISessionTransportReanimator` interface + `SessionTransportRegistry`.*
  Rejected. The inbound path already handles lookup-or-create of binding
  actors, subscribes them to session output, and triggers Akka.Persistence
  rehydration as natural side effects. Introducing a parallel "ensure
  binding" abstraction duplicates well-tested machinery behind ceremony.
- *Factor a "lookup-or-create conversation → binding" helper shared between
  the inbound handler and the trusted-delivery handler.* Rejected. Each
  level of the hierarchy already knows how to route its children via a
  trivial `Context.Child(name).GetOrElse(...)` pattern. Adding one more
  `Receive<>` handler per level is smaller than extracting a helper and
  keeps the new code visible next to the existing routing logic it mirrors.
- *Treat SignalR asymmetrically (direct-to-session-manager bypass).*
  Rejected after verifying that SignalR has the same gateway-based routing
  shape as Slack via `GenericChildPerEntityParent`. The `IWithSessionId`
  extractor fallback lets the shared `DeliverTrustedSessionTurn` route
  through the SignalR gateway without channel-specific wrapping.
- *Synthesize a fake `SlackInboundMessage` / SignalR hub event instead of
  adding a new message type.* Rejected — the existing inbound messages
  carry fields that don't apply to trusted delivery (userId, event IDs,
  connection IDs) and go through dedup and ACL checks that aren't
  meaningful for a reminder. A dedicated handler is cleaner than lying with
  a synthetic payload.

**Rationale**: The actor identity scheme already naturally solves the
routing problem. For Slack, each level of the two-level hierarchy knows how
to route by its piece of the SessionId (channelId, threadTs). For SignalR,
`GenericChildPerEntityParent` routes by the full SessionId via the
extractor. All we need is one new message type, a trivial one-line
extractor fallback, and one new `Receive<>` handler per actor level. No
new abstraction, no registry, no interface.

### D4. Envelope ack gated on session receipt via `IReminderClient.AckAsync`

Mode B envelopes are acked via `IReminderClient.AckAsync(envelope)` — the
public documented API — called by `ReminderExecutionActor` after it has
received `CommandAck` from the target session. The ack semantic is "the
target session has accepted the message into its in-memory state" (the
session's `_state.AddUserMessage(...)` + `TryReplyAck()` has fired), not
"the turn has completed and `TurnRecorded` is persisted."

Concretely:

1. `ReminderManagerActor.HandleReminderFiredAsync` (Mode B branch) spawns
   `ReminderExecutionActor`, passes the `ReminderEnvelope` explicitly as a
   constructor arg. Does **not** call `_client.AckAsync(envelope)`.
2. The execution actor acquires `IReminderClient` via
   `ReminderClientExtension.Get(Context.System)` at startup (standard
   extension pattern).
3. The execution actor builds `DeliverTrustedSessionTurn(SessionId, Content,
   MessageSource)` and issues `Ask<CommandAck>` against the appropriate
   channel gateway (`SlackGatewayActor` or `SignalRGatewayActor`, selected
   by `OriginChannelType`) with a timeout of
   `ReminderSettings.DefaultAckTimeout` (Akka.Reminders library default).
4. The gateway routes the message down its existing hierarchy as D3
   describes. The session's `TryReplyAck` fires a `CommandAck` reply to the
   Ask temp actor via `MessageSource.AckTarget` → `ChannelPipeline`'s
   sender propagation.
5. On `CommandAck`, the execution actor calls
   `await _client.AckAsync(_envelope)`, inspects the
   `ReminderAckResponse.ResponseCode`, logs on non-`Success`, then tells
   `Context.Parent` a `ReminderExecutionCompleted(success=true)` for
   bookkeeping (failure counters, history records).
6. On `CommandNack`, Ask-timeout, or any gateway exception, the execution
   actor does **not** call `AckAsync`. It tells the parent a
   `ReminderExecutionCompleted(success=false)` with an error message. The
   un-acked envelope is redelivered by Akka.Reminders per its configured
   policy.

**Why `_client.AckAsync(envelope)` and not reply-to-sender**:
`IReminderClient.AckAsync` is the public documented API. Decompiled, it
constructs a `ReminderProtocol.ReminderAck` and `Ask`s the scheduler proxy
with a 15-second timeout — the same thing reply-to-sender would do, but
behind a stable API that insulates us from any future changes to the
library's internal actor topology or ack message format. Acquiring the
client from the ActorSystem extension is a one-line standard pattern.

**Duplicate-ack safety** (from decompiling the scheduler's `ReminderAck`
handler): if the execution actor calls `AckAsync` twice for the same
envelope (e.g., after a redelivery that was deduped by the session), the
scheduler handles the duplicate cleanly — appends the second sender to an
existing `BufferedAckWrite.ReplyTo` list if the first ack hasn't flushed
yet, or creates a new entry that the storage layer handles idempotently if
it has. Neither path throws from `AckAsync`. Worst case is a non-`Success`
response code that we log and proceed past.

**Alternatives considered**:

- *Ack in the manager via `Ask`ing the session directly from
  `HandleReminderFiredAsync`.* Rejected — would block the manager's mailbox
  on the Ask. The manager is a singleton and must stay responsive to other
  reminder events.
- *Custom retry loop in the manager with a sidecar table of un-acked
  envelopes.* Rejected — Akka.Reminders already implements this; we just
  need to not eager-ack.
- *Reply-to-sender with a hand-constructed `ReminderProtocol.ReminderAck`,
  bypassing `IReminderClient`.* Rejected — the client is the public API,
  and the "no plumbing" argument was premature optimization of a
  non-problem (extensions are the designed-for singleton-lookup pattern).
- *Bouncing the ack through `ReminderManagerActor` via a new
  `AckReminderEnvelope` message.* Rejected — unnecessary mailbox hop,
  serializes ack processing on the manager's singleton mailbox for no
  benefit.
- *Wait for `TurnCompleted` before acking.* Rejected — LLM turns commonly
  exceed any reasonable `AckTimeout`. The session's in-memory
  `CommandAck` (fired after `_state.AddUserMessage` but before the LLM
  call) is the right boundary because it is fast and semantically "session
  will process this."

### D5. No config surface — library defaults + internal consts

**Revised mid-implementation from an earlier draft that exposed four
`ReminderConfig` properties.** Netclaw has no users yet, and every
proposed knob was either redundant with an Akka.Reminders built-in
default (`AckTimeout`, `MaxRetryBackoff`, `MaxDeliveryAttempts`) or
measuring the same thing in two frames (`SessionDispatchTimeout` vs.
`AckTimeout`). YAGNI: delete `ReminderConfig` entirely. If and when a
real operator asks for a specific tunable, add one knob at that point.

Concretely:

- **`ReminderConfig` is deleted.** No DI registration, no JSON schema
  section, no default-template entry.
- **Akka.Reminders runs with library defaults.** `WithLocalReminders`
  does not call `WithSettings(...)` — `AckTimeout = 10s`,
  `MaxRetryBackoff = 10min`, `MaxDeliveryAttempts = 10`, and friends
  all use the library's shipped values.
- **Mode B execution actor references `ReminderSettings.DefaultAckTimeout`
  directly** (the library's public static field) as the timeout for
  its `Ask<CommandAck>` dispatch to a channel gateway. If the library
  ever changes the default, Netclaw tracks it automatically.
- **Netclaw-specific values live as `internal const` at their
  consumption sites**, not as injected config:
  - `ReminderManagerActor.MaxConcurrentExecutions = 3`
  - `ReminderManagerActor.FailurePauseThreshold = 5`
  - `ReminderExecutionActor.ExecutionTimeoutSeconds = 300`
  - `ReminderScheduleParser.MinIntervalSeconds = 60`
  - `ReminderHistoryStore.MaxRecords = 500`

**Interaction with library `MaxDeliveryAttempts`**: Netclaw's auto-pause
threshold (5) is strictly below the library's default retry budget (10),
so in practice Netclaw's per-reminder auto-pause fires first and
operators see the `paused` state in `netclaw reminders list` before the
library marks an occurrence terminally failed. If a future library
upgrade lowers the default below 5, we'd need to either lower the
Netclaw const or set `MaxDeliveryAttempts` explicitly — both are
one-line changes, easy to discover when it happens.

**Alternatives considered (all rejected)**:

- *Expose the four properties as originally planned.* Rejected on
  YAGNI grounds: no user asks for these, and exposing them creates
  configuration-drift surface (especially between
  `FailurePauseThreshold`, library `MaxDeliveryAttempts`, and the
  redundant `SessionDispatchTimeout`/`AckTimeout` pair). Netclaw has
  no users yet — "if we don't need it today, we don't need it
  tomorrow."
- *Expose only `FailurePauseThreshold`.* Rejected — no actual demand
  signal. Internal const is sufficient, and the invariant "auto-pause
  threshold < library `MaxDeliveryAttempts`" is easier to enforce by
  inspection than by runtime validation.
- *Put tunables on individual reminders via `set_reminder`.* Rejected
  as over-engineering for MVP. Can come later if there's a real demand
  signal.

## Known failure modes and explicit tradeoffs

This section is normative — the spec's "Reminder delivery guarantees"
requirement references it directly.

**Guaranteed (at-least-once, caught by redelivery or dedup)**:

- *Crash before the channel gateway receives `DeliverTrustedSessionTurn`*:
  envelope un-acked, Akka.Reminders redelivers on next fire.
- *Crash after gateway offer but before the stream stage processes the
  `ChannelInput`*: the Ask temp actor never receives a reply from the
  session, execution actor's Ask times out without calling `AckAsync`,
  envelope un-acked, Akka.Reminders redelivers. **This is why
  session-gated ack is strictly better than gateway-level ack** — a
  gateway-level ack would have closed out the envelope in this window and
  lost the reminder.
- *Crash after session received the message but before execution actor
  calls `_client.AckAsync`*: envelope un-acked, Akka.Reminders redelivers.
  On redelivery, the session's in-memory dedup set may catch it (if the
  turn had time to persist `TurnRecorded` before the crash and the actor
  hadn't been forced to restart from snapshot). If the dedup misses, the
  redelivered reminder is processed as a fresh turn — which is the
  desired retry behavior.
- *Ack message lost in flight between execution actor and scheduler
  proxy*: Akka.Reminders redelivers on its own `AckTimeout`, session dedup
  likely catches the duplicate.

**Explicitly not guaranteed (accepted tradeoff)**:

- *Crash after `_client.AckAsync(envelope)` returns success but before the
  session's LLM turn completes and persists `TurnRecorded`*: envelope is
  acked from Akka.Reminders' perspective, session state has the user
  message in memory but not in the journal, crash loses the in-memory
  state. On restart, the reminder is not redelivered (envelope is acked)
  and the session has no record of it — the reminder is lost.

  This window can span the entire LLM turn execution (seconds to minutes
  for tool-heavy reasoning). **It is the same failure mode every regular
  `SendUserMessage` has today** — Mode B reminders do not introduce a new
  failure class. Closing this gap requires a durable ingress queue on
  `LlmSessionActor`, which is session-wide work that belongs in the
  drain-on-shutdown follow-up (issues #403, #419).

- *Duplicate reminder processing across snapshot recovery boundaries*: if
  `LlmSessionActor` is recovered from a snapshot rather than replaying the
  full journal, the `ProcessedReminderIds` dedup set starts empty. A
  redelivery of a pre-snapshot reminder would then be processed as a
  fresh turn. In practice this requires the reminder to still be within
  Akka.Reminders' `MaxDeliveryWindow` after a snapshot has been taken,
  which is a narrow timing window.

  **Accepted tradeoff per the explicit "duplicates are acceptable" framing**:
  the LLM itself typically recognizes a duplicate prompt in its recent
  context and responds appropriately. The few-bytes-per-entry cost of
  persisting the dedup set was not worth preventing a rare edge-case
  duplicate.

Operators who need stronger guarantees should track the drain-on-shutdown
follow-up. This change deliberately ships with the same crash-durability
semantics that regular user messages already have.

## Actor topology (after this change)

```
ReminderManagerActor (singleton)
 └── ReminderExecutionActor (child-per-execution, short-lived)
       ├── Mode A: ISessionPipeline.CreateAsync(reminder/{id}/{ts}) — unchanged
       └── Mode B:
             var msg = new DeliverTrustedSessionTurn {
                 SessionId = _definition.SessionId,
                 Content = BuildPrompt(_definition),
                 Source = new MessageSource {
                     ReminderId = "{id}:{fireTs}",
                     ChannelType = _definition.OriginChannelType,
                     Audience = _definition.Audience,
                     Principal = VerifiedAutomation,
                     Provenance = { SourceKind = "reminder", ... },
                     AckTarget = null   // set by gateway handler from its Sender
                 }
             }

             var gateway = ResolveGatewayFor(_definition.OriginChannelType);
             ack = await gateway.Ask<object>(msg, ReminderSettings.DefaultAckTimeout);
                │
                ▼
             Channel gateway routes msg down the hierarchy via Forward,
             each level reads Sender and preserves it. At the leaf:
                │
                ▼
             SlackThreadBindingActor.Receive<DeliverTrustedSessionTurn>   OR
             SignalRSessionActor.Receive<DeliverTrustedSessionTurn>:
                read Sender
                build ChannelInput {
                    Source = msg.Source with { AckTarget = Sender }
                }
                inputQueue.OfferAsync(channelInput)
                → stream stage runs MapToCommand → sessionManager.Tell(
                    cmd, sender: cmd.Source.AckTarget)
                → LlmSessionActor.HandleIncomingUserMessage runs,
                  pre-check dedup hit? reply CommandAck, return
                  else: _state.AddUserMessage, TryReplyAck (replies to Sender,
                        which is the Ask temp actor), then FireInitialTurnLlmCall
                → Ask temp actor receives CommandAck, completes dispatcher's Task

             On CommandAck: await _client.AckAsync(envelope)
                            log non-Success response codes
                            Context.Parent ! ReminderExecutionCompleted(success=true)
                            Context.Stop(Self)

             On timeout / CommandNack / exception:
                            // Do NOT call AckAsync
                            Context.Parent ! ReminderExecutionCompleted(success=false, error)
                            Context.Stop(Self)
                            (envelope remains un-acked → Akka.Reminders redelivers)
```

In both channels, the reminder turn's streaming output is delivered back
to the client transport (Slack API via the thread binding's existing
output sink, or SignalR hub via the existing `SignalRSessionActor` bridge)
via whatever output subscribers exist at that moment. For SignalR with no
connected client, the output is dropped per the existing
`Source.ActorRef` `OverflowStrategy.DropHead` behavior; the turn still
persists via `TurnRecorded` and is visible on next `ResumeSessionAsync`.
This mirrors the current semantic when a TUI client exits mid-tool-call.

## Migration plan

None. Netclaw has no users. This change ships with the next dev build.

## Open questions

None blocking. All design decisions are resolved; implementation can
proceed to the tasks artifact.

## Follow-ups to file after merge

1. **Graceful drain and restart via reminder reactivation** — on shutdown,
   enumerate live sessions with in-flight turns; schedule one-shot
   reminders per session to fire on next startup; Mode B path deposits the
   resume prompt into each session mailbox. References issues #403 and
   #419 and the "Reminder delivery guarantees" section of this change.
2. **Extract SignalR channel from `Netclaw.Daemon` into
   `Netclaw.Channels.SignalR`** — pure code-organization cleanup,
   eliminates the asymmetry between Slack (standalone channel project)
   and SignalR (colocated with the daemon), gives future channels a
   consistent template to follow.
3. **Expose Akka.Reminders terminal-failure state via `IReminderClient`
   query** — upstream work in `Aaron.Akka.Reminders` to add a
   `ListTerminallyFailedRemindersAsync` or equivalent, plus downstream
   Netclaw changes to expose that state via `netclaw reminders list`.
   Nice-to-have for operator visibility of reminders that exhausted
   `MaxDeliveryAttempts` without Netclaw's auto-pause firing first (which
   can only happen if `FailurePauseThreshold` is pathologically high).

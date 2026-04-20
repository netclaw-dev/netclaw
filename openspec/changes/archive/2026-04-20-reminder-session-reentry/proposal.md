# Proposal: reminder-session-reentry

## Why

Netclaw's reminder system (PRD-008) was built to let the LLM schedule deferred
work — "in 5 minutes, check if PR #123 has been auto-merged." This is a
first-class modernization vector: instead of the agent burning a context
window polling (`Task.Delay` + re-check), it schedules a reminder and yields.
When the reminder fires, a new turn is deposited into the originating session's
mailbox, the `LlmSessionActor` picks it up (rehydrating from persistence if
idle), and the agent resumes where it left off. Event-driven, not blocking.

Today this path is broken end-to-end (issue #660). A reminder created from
a Slack thread without an explicit `reportToChannel` crashes with `Error:
User D0AC6CKBK5K is not in the allowed users list.` because `SetReminderTool`
leaks the session's channel ID as a synthetic notification target, the
execution actor pipes it into `ChannelInput.ChannelId`, and the LLM is
coached to call `send_slack_message` with a target that fails Slack ACL. Even
if those defects were patched, the original session's output binding may
have passivated between the reminder being set and fired, and there's no
way to reach it except by routing through the channel's normal inbound path.

This change fixes the end-to-end path and formalizes session re-entry as a
first-class mode for reminders by reusing each channel's existing actor
routing hierarchy — no new abstractions, no new interfaces, no new
registries. The actor identity schemes already naturally solve the routing
problem; we just need to add one new protocol message and one new
`Receive<>` handler per actor level in each existing chain.

## What Changes

- **NEW**: A second mode for reminders — **Mode B (session check-back)** —
  triggered when `set_reminder` is invoked from an addressable session and
  the LLM does not supply `reportToChannel`. The reminder runs a new turn
  **inside the originating session**, not in an isolated `reminder/{id}`
  session. Output flows back through the originating channel's existing
  subscriber machinery.
- `SetReminderTool` stops leaking `context.SessionId` into
  `ReportToChannel`/`ReportToThreadTs`. Reminders set without an explicit
  target are persisted with `ReportToChannel = null` and a new `SessionId`
  + `OriginChannelType` pair on `ReminderDefinition`.
- **NEW**: A shared protocol message
  `DeliverTrustedSessionTurn(SessionId, Content, MessageSource) : IWithSessionId`
  in `Netclaw.Actors.Protocol`. Both the daemon's channel gateways add one
  new `Receive<>` handler that mirrors the gateway's existing inbound-routing
  logic:
  - **Slack** (`SlackGatewayActor` → `SlackConversationActor` →
    `SlackThreadBindingActor`): three new handlers, one per level, each
    using the same `Context.Child(name).GetOrElse(...)` pattern the existing
    inbound-event handler uses and `Forward(msg)` to preserve `Sender` down
    the chain.
  - **SignalR** (`SignalRGatewayActor` using `GenericChildPerEntityParent`
    with `SignalRMessageExtractor`): one new handler on `SignalRSessionActor`.
    The extractor gets a one-line `IWithSessionId` fallback so the shared
    protocol message is routable without channel-specific wrapping.
    `ISignalRSessionMessage` stays `internal` — no upstream dependency leak.

  Each channel's inbound ACL check (e.g., `SlackAclPolicy.EvaluateInbound`)
  is **not called** from the new handler, because the reminder's audience is
  already validated at minting time by `reminder-audience-authorization`.
  There's no "shared code with a flag" — the two message types (inbound
  event vs. `DeliverTrustedSessionTurn`) have separate handlers with
  separate logic.

- **NEW**: Reminder delivery re-uses the existing `SendUserMessage` ingress
  path. The target session's `LlmSessionActor.HandleIncomingUserMessage`
  fires `TryReplyAck()` as today; the reply flows back through
  `ChannelPipeline`'s sender propagation to the reminder dispatcher's
  `Ask<CommandAck>` temp actor.
- **NEW**: `MessageSource.AckTarget` (optional `IActorRef?`, ephemeral —
  `MessageSource` is already `[ProtoIgnore]` on `SendUserMessage`).
  `ChannelPipeline.MapToCommand`'s stream sink reads
  `cmd.Source?.AckTarget ?? NoSender` when telling the session manager.
  Regular inbound messages leave `AckTarget = null` → existing
  fire-and-forget semantics preserved exactly. Trusted deliveries set it
  and the session's existing `TryReplyAck` naturally routes the ack back to
  the dispatcher.
- **NEW**: Optional ephemeral `MessageSource.ReminderId` dedup key (forensic
  tag and best-effort in-memory redelivery dedup).
- **NEW**: Persistent `TurnRecorded.SourceReminderId` (`[ProtoMember(5)]`,
  additive). Populated when a turn originated from a reminder. Used for
  forensic queries and to rebuild `SessionState.ProcessedReminderIds` from
  event replay.
- **NEW**: `SessionState.ProcessedReminderIds` as an in-memory
  `ImmutableHashSet<string>` field, folded in `Apply(TurnRecorded)` and
  preserved in `Apply(SessionCompacted)`. Used by a dedup pre-check at the
  top of `HandleIncomingUserMessage` to catch redelivered reminders without
  re-processing. **Not persisted to `SessionSnapshot`** — on snapshot-based
  recovery, the set starts empty and rebuilds from post-snapshot event
  replay. Duplicates across snapshot boundaries are an explicitly accepted
  tradeoff.
- **CHANGED**: `ReminderManagerActor.HandleReminderFiredAsync` stops
  calling `_client.AckAsync(envelope)` eagerly for Mode B. The envelope is
  held open until the target session replies `CommandAck`, at which point
  the execution actor itself calls `_client.AckAsync(envelope)`. On
  timeout, transport failure, or `CommandNack`, the envelope stays
  un-acked and `Aaron.Akka.Reminders 0.6.0-beta2`'s built-in `AckTimeout`
  / `ProcessAckTimeouts` / `MaxDeliveryAttempts` machinery redelivers.
- **NEW**: `SlackGatewayActorKey` + `SignalRGatewayActorKey` marker types
  in `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs` (the existing home
  for `SessionManagerActorKey` and four other markers).
  `SignalRGatewayActorKey` moves from
  `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` (namespace
  change). `SlackChannel.StartAsync` adds a one-line
  `registry.Register<SlackGatewayActorKey>(_gateway)` call. Reminder
  dispatcher resolves both via `IRequiredActor<>`.
- **DELETED**: `src/Netclaw.Configuration/ReminderConfig.cs`. No
  reminder config surface is exposed to operators. Akka.Reminders runs
  with its built-in defaults (`AckTimeout = 10s`,
  `MaxRetryBackoff = 10min`, `MaxDeliveryAttempts = 10`); Netclaw-specific
  values (`MaxConcurrentExecutions`, `FailurePauseThreshold`,
  `ExecutionTimeoutSeconds`, `MinIntervalSeconds`, `HistoryMaxRecords`)
  live as `internal const` on their consuming classes. Mode B execution
  actor references `ReminderSettings.DefaultAckTimeout` directly so it
  tracks library defaults automatically. If an operator ever needs to
  tune one of these, add a single knob at that point — see design D5.
- **NEW scenarios** on the `netclaw-scheduling` capability documenting Mode
  B semantics, dedup behavior, and an explicit "Reminder delivery
  guarantees" section that enumerates each crash window and marks it as
  safe (redelivered) or explicitly-accepted-tradeoff.

Explicitly **out of scope** for this change:

- Durable ingress queue on `LlmSessionActor` that would survive crash
  mid-turn. This is a session-wide redesign that applies to every user
  message, not reminders specifically, and belongs in the drain-on-shutdown
  follow-up (related issues #403, #419).
- Automatic shutdown-drain via self-reminder. The infrastructure this
  change builds makes it trivial to add later, but is not implemented here.
- Real-time output delivery to disconnected SignalR clients. Reminder
  turns still persist into session state via `TurnRecorded`; the streaming
  output is dropped per the existing `OverflowStrategy.DropHead` behavior
  and clients see the completed turn on next `ResumeSessionAsync`. This
  mirrors the current semantic when a TUI client disconnects mid-tool-call.
- Snapshot persistence of the dedup ledger. In-memory only; duplicates
  across snapshot recovery boundaries are acceptable.
- Extracting the SignalR channel from `Netclaw.Daemon` into a standalone
  `Netclaw.Channels.SignalR` project. Worth doing for architectural
  symmetry with `Netclaw.Channels.Slack`, filed as a follow-up issue.

## Capabilities

### New Capabilities

None. Session re-entry is a new mode on an existing capability.

### Modified Capabilities

- `netclaw-scheduling` — add Mode B (session check-back) scenarios, the
  ack-gated envelope delivery contract, dedup semantics, the collapsed
  `FailurePauseThreshold`/`MaxDeliveryAttempts` relationship, and the
  explicit "Reminder delivery guarantees" section enumerating crash windows.
- `netclaw-session` — add handling for reminder-originated
  `SendUserMessage` commands, `TurnRecorded.SourceReminderId` persistence,
  and the `ProcessedReminderIds` in-memory dedup ledger (explicitly not
  snapshot-persisted).
- `netclaw-input-adapters` — Mode A/B distinction on the internal timer
  adapter's entity key and session lifecycle, plus the
  `DeliverTrustedSessionTurn` shared protocol message contract
  (implemented by each channel's existing routing actors via new
  `Receive<>` handlers), the `MessageSource.AckTarget` field and its
  propagation through `ChannelPipeline.MapToCommand`, and the one-line
  `SignalRMessageExtractor.EntityId` fallback to `IWithSessionId`.

## Impact

**Source code**:

- `src/Netclaw.Actors/Reminders/` — `SetReminderTool`, `ReminderProtocol`,
  `ReminderExecutionActor`, `ReminderManagerActor`
- `src/Netclaw.Actors/Protocol/Commands.cs` — new
  `DeliverTrustedSessionTurn` shared protocol message
- `src/Netclaw.Actors/Protocol/Events.cs` — `TurnRecorded.SourceReminderId`
  (`ProtoMember 5`, additive)
- `src/Netclaw.Actors/Channels/MessageSource.cs` — ephemeral `ReminderId`
  and `AckTarget` fields
- `src/Netclaw.Actors/Channels/ChannelPipeline.cs` — propagate
  `input.Source?.AckTarget` as the `Tell` sender in the `inputSink` lambda
- `src/Netclaw.Actors/Sessions/SessionState.cs` — `ProcessedReminderIds`
  in-memory field, `Apply(TurnRecorded)` fold, `Apply(SessionCompacted)`
  preservation. No changes to `ToSnapshot` / `FromSnapshot`.
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — dedup pre-check in
  both `Ready`-phase and `Processing`-phase `SendUserMessage` handlers
- `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs` — new
  `SlackGatewayActorKey` and `SignalRGatewayActorKey` marker types
- `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` — remove
  declaration of `SignalRGatewayActorKey` (now upstream); add `using
  Netclaw.Actors.Hosting;`
- `src/Netclaw.Daemon/Gateway/SessionRegistry.cs` +
  `src/Netclaw.Daemon.Tests/Gateway/SessionRegistryTests.cs` — add `using
  Netclaw.Actors.Hosting;` imports (mechanical)
- `src/Netclaw.Daemon/Gateway/SignalRSessionActor.cs` — extend
  `SignalRMessageExtractor.EntityId` with `IWithSessionId` fallback;
  add `Receive<DeliverTrustedSessionTurn>` handler on `SignalRSessionActor`
- `src/Netclaw.Channels.Slack/SlackChannel.cs` — one-line
  `registry.Register<SlackGatewayActorKey>(_gateway)` in `StartAsync`
- `src/Netclaw.Channels.Slack/SlackGatewayActor.cs` — new
  `Receive<DeliverTrustedSessionTurn>` handler that mirrors the existing
  `SlackInboundMessage` routing shape
- `src/Netclaw.Channels.Slack/SlackConversationActor.cs` — new
  `Receive<DeliverTrustedSessionTurn>` handler that mirrors the existing
  thread-binding lookup-or-create
- `src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs` — new
  `Receive<DeliverTrustedSessionTurn>` handler that constructs a
  `ChannelInput` with `MessageSource.AckTarget = Sender` and offers it to
  the pipeline queue
- **DELETED** `src/Netclaw.Configuration/ReminderConfig.cs` — no config
  surface (see design D5). Consumers had their `ReminderConfig` parameter
  removed from constructors and call sites:
  `ReminderManagerActor`, `ReminderExecutionActor`, `SetReminderTool`,
  `ReminderHistoryStore`, `ReminderScheduleParser`, `WithReminderTools`,
  `WithReminderManager`, `WithNetclawActors`, and the daemon's minimal-API
  reminder endpoints in `Program.cs`.
- `src/Netclaw.Actors/Hosting/NetclawAkkaHostingExtensions.cs` — no
  `ReminderSettings` override; `WithLocalReminders` runs with library
  defaults throughout.

**Dependencies**:

- `Aaron.Akka.Reminders 0.6.0-beta2` already in use
  (`Directory.Packages.props:7`). No upgrade required — envelope-ack and
  redelivery machinery is already present (`AckTimeout`, `AckDeadline`,
  `AwaitingAckReminders`, `CheckAckTimeouts`, `MaxDeliveryAttempts`,
  `MaxRetryBackoff` confirmed via DLL inspection). We rely on the
  library's shipped defaults for all of these.

**Persisted state**:

- `TurnRecorded` gains `SourceReminderId` (`ProtoMember 5`, additive). Old
  journals deserialize with `SourceReminderId = null`, which is safe.
- `ReminderDefinition` gains `OriginChannelType`.
- `SessionSnapshot` is **not** modified. `ProcessedReminderIds` is
  in-memory only.
- Netclaw has no users yet; no migration, no backward-compat shims.

## Security and Operational Impact

**Security**:

- The audience enforcement already introduced by
  `reminder-audience-authorization` applies unchanged: the reminder's
  stored audience is authoritative at execution time. Mode B honors this
  by propagating the stored audience into the synthesized `MessageSource`
  on `DeliverTrustedSessionTurn`.
- Removing the synthetic `ReportToChannel` extraction **closes an implicit
  ACL bypass path**: previously, a reminder created from any session would
  be configured to post to that session's channel without going through
  the target resolver's validation. Today this fails loudly because of
  the Slack ACL mismatch; if it had happened to succeed, it would have
  been an unvalidated outbound post path. Mode B eliminates the
  possibility by not configuring an outbound target at all.
- Channel-level inbound ACLs (e.g., `SlackAclPolicy.EvaluateInbound`) are
  explicitly bypassed for `DeliverTrustedSessionTurn` delivery, on the
  grounds that the reminder's audience was validated at minting time and
  the provenance flags (`Principal = VerifiedAutomation`, `SourceKind =
  "reminder"`, `TransportAuthenticity = LocalProcess`, `PayloadTaint =
  Trusted`) mark it as a trusted local delivery. The two message types
  (`SlackInboundMessage` and `DeliverTrustedSessionTurn`) have separate
  handlers — there is no shared code path with an `isTrusted` flag that
  could accidentally leak the bypass into inbound-event handling.
- Fail-loud posture preserved: timeouts, transport failures, and session
  `CommandNack`s all surface as reminder execution failures with explicit
  log lines. No silent fallbacks.

**Operational**:

- **No new config surface.** `ReminderConfig` deleted; Akka.Reminders
  library defaults apply, Netclaw-specific values live as `internal const`
  on consuming classes. See design D5.
- New log events: `reminder_mode_b_dispatch`, `reminder_mode_b_dedup_hit`,
  `reminder_mode_b_session_nack`, `reminder_mode_b_timeout`,
  `reminder_ack_non_success`.
- Existing `reminder-execution-history` capability continues to capture
  Mode B executions; the stored `sessionId` now matches the originating
  session, not a synthetic `reminder/{id}/{ts}` value.
- Existing `FailurePauseThreshold` auto-pause mechanism (now an internal
  const of 5) provides visible operator state when a reminder exceeds its
  failure count — visible via `netclaw reminders list` as today. Fires
  strictly before Akka.Reminders' library default retry cap (10) so the
  paused state is observable to operators.
- No new runtime dependencies or infrastructure. Single-process MVP
  posture preserved.

**Eval suite**: reminder scheduling is an eval-guarded area per
`CLAUDE.md`. This change adds at least one regression case exercising
Mode B end-to-end.

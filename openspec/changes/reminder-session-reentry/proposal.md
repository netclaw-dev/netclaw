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
if those defects were patched, there is no mechanism to deliver the resulting
turn back to the originating Slack thread when the thread's binding has been
torn down — outputs disappear into the persisted session state with no path
back to the user.

This change fixes the end-to-end path, formalizes session re-entry as a
first-class mode for reminders, and establishes the transport-reanimation
contract that the drain-on-shutdown follow-up will also reuse.

## What Changes

- **NEW**: A second mode for reminders — **Mode B (session check-back)** —
  triggered when `set_reminder` is invoked from an addressable session and
  the LLM does not supply `reportToChannel`. The reminder runs a new turn
  **inside the originating session**, not in an isolated `reminder/{id}`
  session. Output flows back through the originating transport (Slack
  thread, TUI, SignalR).
- `SetReminderTool` stops leaking `context.SessionId` into
  `ReportToChannel`/`ReportToThreadTs`. Reminders set without an explicit
  target are persisted with `ReportToChannel = null` and a new `SessionId`
  + `OriginChannelType` pair.
- **NEW**: A shared protocol message
  `DeliverTrustedSessionTurn(SessionId, Content, MessageSource)` in
  `Netclaw.Actors.Protocol`. Channels with a durable server-side transport
  binding (Slack) add one handler on their gateway that runs the same
  lookup-or-create chain as the existing inbound handler, but tagged as
  trusted so the channel-level ACL check is bypassed (reminder audience is
  already validated at minting time by `reminder-audience-authorization`).
  No new interface or registry — reminder dispatch is a small switch on
  `OriginChannelType`, routing Slack reminders to the Slack gateway and
  non-Slack reminders directly to the session manager.
- **NEW**: Reminder delivery re-uses the existing `SendUserMessage` ingress
  path. A new optional `ReminderId` carrier on `MessageSource` (ephemeral)
  and `TurnRecorded.SourceReminderId` (persisted, ProtoMember 5) provide
  idempotency for redelivery. `LlmSessionActor` dedups on the recovered
  `ProcessedReminderIds` set before accepting the message.
- **NEW**: Optional `ChannelInput.AckTarget` (`IActorRef?`) propagated
  through `ChannelPipeline.MapToCommand` as the `Tell` sender. Regular
  inbound messages leave it null → unchanged fire-and-forget semantics.
  Trusted deliveries set it → the session's existing `TryReplyAck` fires
  naturally back to the reminder dispatcher, no session-side special
  casing.
- **CHANGED**: `ReminderManagerActor.HandleReminderFiredAsync` stops acking
  the Akka.Reminders envelope eagerly for Mode B. The envelope is held open
  until the target session replies `CommandAck` (via existing
  `TryReplyAck` in `HandleIncomingUserMessage`). On timeout, transport
  failure, or `CommandNack`, the envelope stays un-acked and
  `Aaron.Akka.Reminders 0.6.0-beta2`'s built-in `AckTimeout` /
  `ProcessAckTimeouts` / `MaxDeliveryAttempts` machinery redelivers — the
  guarantee chain is at-least-once up to the session handoff.
- **NEW**: `ReminderConfig` surfaces `AckTimeout`, `MaxDeliveryAttempts`,
  and `MaxDeliveryWindow` and wires them into the Akka.Reminders client.
  JSON schema is updated per the Configuration Schema Sync Rule.
- **NEW scenarios** on the `netclaw-scheduling` capability documenting Mode B
  semantics, dedup behavior, transport reanimation contract, and a
  "delivery guarantees" section explicitly documenting the
  crash-during-processing gap (same semantic as regular user messages
  today; subsumed by the drain-on-shutdown follow-up).

Explicitly **out of scope** for this change:

- Durable ingress queue on `LlmSessionActor` that would survive crash
  mid-turn. This is a session-wide redesign that applies to every user
  message, not reminders specifically, and belongs in the drain-on-shutdown
  follow-up (related issues #403, #419).
- Automatic shutdown-drain via self-reminder. The infrastructure this
  change builds makes it trivial to add later, but is not implemented here.
- Output delivery to disconnected TUI/SignalR clients. Reminder turns
  persist into session state normally; clients see them when they
  reconnect. Documented as a known limitation.

## Capabilities

### New Capabilities

None. Session re-entry is a new mode on an existing capability; it does not
warrant a new capability spec.

### Modified Capabilities

- `netclaw-scheduling` — add Mode B (session check-back) scenarios, the
  ack-gated envelope delivery contract, dedup semantics, and a
  delivery-guarantees subsection.
- `netclaw-session` — add handling for reminder-originated `SendUserMessage`
  commands, `TurnRecorded.SourceReminderId` persistence, and the
  `ProcessedReminderIds` dedup ledger.
- `netclaw-input-adapters` — Mode A/B distinction on the internal timer
  adapter's entity key and session lifecycle, plus the
  `DeliverTrustedSessionTurn` shared protocol message contract and the
  `ChannelInput.AckTarget` extension that lets session-side ack replies
  flow back through the pipeline to the reminder dispatcher.

## Impact

**Source code**:
- `src/Netclaw.Actors/Reminders/` — `SetReminderTool`, `ReminderProtocol`,
  `ReminderExecutionActor`, `ReminderManagerActor`
- `src/Netclaw.Actors/Protocol/Commands.cs` — new
  `DeliverTrustedSessionTurn` shared protocol message
- `src/Netclaw.Actors/Protocol/Events.cs` — `TurnRecorded.SourceReminderId`
  (ProtoMember 5; additive)
- `src/Netclaw.Actors/Channels/MessageSource.cs` — ephemeral
  `ReminderId` field
- `src/Netclaw.Actors/Channels/ChannelInput.cs` — optional `AckTarget`
  field
- `src/Netclaw.Actors/Channels/ChannelPipeline.cs` — propagate
  `ChannelInput.AckTarget` as the sender on the session-manager `Tell` in
  `MapToCommand`
- `src/Netclaw.Actors/Sessions/` — `SessionState` dedup set,
  `LlmSessionActor` handler dedup pre-check
- `src/Netclaw.Channels.Slack/SlackGatewayActor.cs` — new
  `Receive<DeliverTrustedSessionTurn>` handler sharing the lookup-or-create
  chain with the existing `SlackInboundMessage` handler
- `src/Netclaw.Configuration/ReminderConfig.cs` + `netclaw-config.v1.schema.json`

**Dependencies**:
- `Aaron.Akka.Reminders 0.6.0-beta2` already in use
  (`Directory.Packages.props:7`). No upgrade required — the envelope-ack
  and redelivery machinery is already present (`AckTimeout`, `AckDeadline`,
  `AwaitingAckReminders`, `CheckAckTimeouts`, `MaxDeliveryAttempts`,
  `MaxDeliveryWindow` confirmed via DLL inspection). We are currently
  configuring none of it; this change exposes and uses it.

**Persisted state**:
- `TurnRecorded` gains `SourceReminderId` (ProtoMember 5; additive).
- `ReminderDefinition` gains `OriginChannelType`.
- Netclaw has no users yet; no migration, no backward-compat shims.

## Security and Operational Impact

**Security**:
- The audience enforcement already introduced by
  `reminder-audience-authorization` applies unchanged: the reminder's
  stored audience is authoritative at execution time. Mode B honors this
  by propagating the stored audience into the synthesized `MessageSource`.
- Removing the synthetic `ReportToChannel` extraction **closes an implicit
  ACL bypass path**: previously, a reminder created from any session would
  be configured to post to that session's channel without going through
  the target resolver's validation. Today this fails loudly because of
  the Slack ACL mismatch; if it had happened to succeed, it would have
  been an unvalidated outbound post path. Mode B eliminates the
  possibility by not configuring an outbound target at all.
- The transport reanimation contract does not introduce new outbound
  posting capabilities. Reanimators only (re)materialize an existing
  binding actor type; no new authorization surface is added.
- Fail-loud posture preserved: timeouts, transport reanimation failures,
  and session `CommandNack`s all surface as reminder execution failures
  with explicit log lines. No silent fallbacks.

**Operational**:
- Three new config knobs (`AckTimeout`, `MaxDeliveryAttempts`,
  `MaxDeliveryWindow`) with sensible defaults. Schema-validated.
- New log events: `reminder_mode_b_dispatch`,
  `reminder_mode_b_dedup_hit`, `reminder_mode_b_session_nack`,
  `reminder_mode_b_timeout`, `transport_reanimation_success`,
  `transport_reanimation_noop`, `transport_reanimation_failed`.
- Existing reminder execution history (`reminder-execution-history`
  capability) continues to capture Mode B executions; the stored
  `sessionId` now matches the originating session, not a synthetic
  `reminder/{id}/{ts}` value.
- No new runtime dependencies or infrastructure. Single-process MVP
  posture preserved.

**Eval suite**: reminder scheduling is an eval-guarded area per
`CLAUDE.md`. This change adds at least one regression case exercising
Mode B end-to-end.

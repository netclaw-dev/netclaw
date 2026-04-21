# Proposal: reminder-delivery-contract

## Why

`set_reminder` couples four orthogonal concerns — execution mode,
delivery target, delivery policy, and result body — into a mixed
`ReportToChannel` / `NotifyInstructions` / `NotifyPolicy` surface that
the LLM has to untangle via free text. Observed failure in session
`D0AC6CKBK5K/1776697725.361339` (journal seq=1..4): a Mode B reminder
fired and was marked successful in execution history, but the agent
reply never surfaced in the originating Slack thread and no operational
alert fired. The LLM had coached itself with
`NotifyInstructions = "Reply in this session with the result."` because
today there is no structured way to say "route this back to the session
I was created from." Addresses #690; folds in #644 (multi-transport
routing) so the tool surface and data model are rebuilt exactly once.

## What Changes

- **BREAKING** `set_reminder` tool schema replaces `reportToChannel`,
  `notifyInstructions`, `notifyPolicy` with:
  - `delivery: { kind, transport?, address? }` — structured destination
    selector. `kind ∈ { current_session, channel, none }`. When
    `kind = channel`, both `transport` (e.g. `"slack"`) and `address`
    (e.g. `#general`, `@user`, raw ID) are required.
  - `deliveryRequired: bool` (default `true`) — when `true`, a missed
    delivery fails the execution and emits
    `OperationalAlert.ReminderExecutionFailed`, keeping the envelope
    un-acked so Akka.Reminders redelivers.
  - `deliveryInstructions: string?` — optional body content guidance. Never
    used for routing.
- **BREAKING** `ReminderDefinition` replaces `ReportToChannel` /
  `ReportToThreadTs` / `NotifyInstructions` / `NotifyPolicy` with a new
  `ReminderDelivery { Kind, Transport, Address, SessionId,
  OriginChannelType }` struct plus `DeliveryRequired` and
  `DeliveryInstructions` fields. Execution mode is now a direct function of
  `Delivery.Kind`, not inferred from which optional field is populated.
- **BREAKING** `IReminderTargetResolver` gains a required `Transport`
  property. `SetReminderTool` dispatches by transport key; unknown or
  unregistered transports fail loud at tool call time. Folds in #644.
  `SlackReminderTargetResolver` returns `"slack"`.
- **NEW** `ReminderDeliveryObserved(reminderId, channelType)` internal
  protocol message emitted by the outbound channel pipeline when a
  reminder-sourced turn actually surfaces through a gateway. For
  `Delivery.Kind = current_session` with `DeliveryRequired = true`,
  `ReminderExecutionActor` waits for this signal (with a timeout)
  before acking the Akka.Reminders envelope. Closes the silent-failure
  gap observed in the journal repro.
- **CHANGED** `ExecutionOutputAccumulator`'s hardcoded
  `send_slack_message` tool-name dependency becomes transport-derived.
  For `Delivery.Kind = channel`, the accumulator watches for the
  transport's canonical notification tool and fails the execution if
  the required tool call never happens / fails.
- **CHANGED** `ReminderExecutionActor` naming/flow: branches keyed on
  `Delivery.Kind` rather than an `IsModeB` inference. `None` kind runs
  the task without any notification tool and reports success purely on
  execution completion.
- **BREAKING / NO-MIGRATION** On startup, `ReminderDefinitionStore`
  hard-deletes any persisted reminder whose on-disk shape does not
  match the new protobuf. Netclaw has one user; the upgrade path is
  "re-create your reminders."
- **CHANGED** `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  Scheduling section rewritten against the new surface. Version bump
  per the system skills sync rule.

## Capabilities

### New Capabilities

None. Delivery contract is a structural change to an existing capability.

### Modified Capabilities

- `netclaw-scheduling`: replace "mode is determined at set time by
  whether the reminder carries an explicit `ReportToChannel`" with a
  `DeliveryKind`-driven selector; add transport-keyed
  `IReminderTargetResolver` registration requirement; add
  `ReminderDeliveryObserved` gateway-signal requirement for
  `current_session` deliveries when `DeliveryRequired = true`; remove
  `ReportToChannel` / `ReportToThreadTs` / `NotifyInstructions` from
  the spec and replace with `ReminderDelivery` / `DeliveryRequired` /
  `DeliveryInstructions`.

## Impact

**Source code**:

- `src/Netclaw.Actors/Reminders/SetReminderTool.cs` — tool surface,
  structured validation, transport-keyed resolver dispatch.
- `src/Netclaw.Actors/Reminders/ReminderProtocol.cs` —
  `ReminderDefinition`, new `ReminderDelivery`, `DeliveryKind`,
  `DeliveryRequired`, `DeliveryInstructions`; `ReminderInfo` mirror.
- `src/Netclaw.Actors/Reminders/IReminderTargetResolver.cs` —
  `Transport` property.
- `src/Netclaw.Channels.Slack/SlackReminderTargetResolver.cs` — return
  `"slack"`.
- `src/Netclaw.Actors/Reminders/ReminderManagerActor.cs` — dispatch on
  `Delivery.Kind`; collapse the `isModeB` inference.
- `src/Netclaw.Actors/Reminders/ReminderExecutionActor.cs` —
  `DeliveryKind`-driven branches, transport-aware accumulator tool
  name, outbound `ReminderDeliveryObserved` handshake for
  `current_session`.
- `src/Netclaw.Actors/Reminders/ExecutionOutputAccumulator.cs` (or
  wherever the `ToolName("send_slack_message")` literal lives) —
  configurable per `Delivery.Transport`.
- `src/Netclaw.Actors/Channels/ChannelPipeline.cs` — emit
  `ReminderDeliveryObserved` on outbound reminder-sourced turns.
- `src/Netclaw.Actors/Protocol/Commands.cs` or new protocol module —
  `ReminderDeliveryObserved` internal message.
- `src/Netclaw.Actors/Reminders/ReminderDefinitionStore.cs` —
  startup hard-delete of stale schema rows.
- `feeds/skills/.system/files/netclaw-operations/SKILL.md` —
  Scheduling section rewrite + version bump.

**Persisted state**:

- `ReminderDefinition` protobuf/JSON shape changes. Stale rows
  hard-deleted at startup. No migration code.
- `ReminderInfo` CLI/API response shape changes — `reminders list` and
  any related endpoints reflect the new fields.

**Dependencies**: none new.

**Security**:

- Removing the free-text `NotifyInstructions` routing path closes a
  latent foot-gun where the LLM could coach itself into invalid or
  unvalidated outbound posts. All channel deliveries flow through a
  transport-keyed `IReminderTargetResolver` that validates the address
  at set time and persists only canonical IDs.
- Audience enforcement (per `reminder-audience-authorization`)
  unchanged: `Delivery.OriginChannelType` + stored audience on the
  definition remain authoritative at execution time.

**Operational**:

- Existing `FailurePauseThreshold` / `ReminderExecutionFailed` alert
  paths unchanged. New failures surface loudly through the same sink.
- New log events: `reminder_delivery_observed`,
  `reminder_delivery_missed`, `reminder_tool_transport_unknown`.
- No new config surface. `MaxConcurrentExecutions` and related knobs
  remain `internal const`.

**Eval suite**: reminder scheduling is eval-guarded per `CLAUDE.md`.
Add at least one case per `delivery.kind` value, plus one
regression case for the observed Slack-thread silent-failure bug.

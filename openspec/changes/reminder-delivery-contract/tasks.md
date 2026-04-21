# Tasks: reminder-delivery-contract

## 1. Protocol / data model

- [x] 1.1 Add `DeliveryKind` enum (`CurrentSession = 0`, `Channel = 1`, `None = 2`) and `ReminderDelivery` class to `src/Netclaw.Actors/Reminders/ReminderProtocol.cs`. Carries `Kind`, `Transport`, `Address`, `SessionId`, `OriginChannelType`. Protobuf-attributed for journal serialization.
- [x] 1.2 Replace `ReportToChannel`, `ReportToThreadTs`, `NotifyInstructions`, `NotifyPolicy`, `SessionId`, `OriginChannelType` on `ReminderDefinition` with a single required `Delivery` field (the struct above) plus `DeliveryRequired` (bool, default `true`) and `DeliveryInstructions` (nullable string).
- [x] 1.3 Update `ReminderInfo` list/get response record to mirror the new shape: expose `Delivery`, `DeliveryRequired`, `DeliveryInstructions`; drop removed fields.
- [ ] 1.4 Drop `NotificationPolicy` enum and any call sites that read it once all code paths have migrated to `DeliveryRequired` (boolean is simpler; the enum was redundant).
- [ ] 1.5 Add `ReminderDeliveryObserved(string ReminderDeliveryKey, ChannelType ChannelType) : IWithSessionId`-ish internal record in `src/Netclaw.Actors/Reminders/` (or `Protocol/` if preferred) — fields needed: reminder delivery key (`{id}:{fireTimestampMs}`), `ChannelType`, optional outbound-delivery timestamp. Not serialized (actor-local signal).
- [x] 1.6 Confirm protobuf evolution: new `ReminderDefinition` shape is a clean break; no protobuf member numbers are reused from the old fields.

## 2. Tool surface (`SetReminderTool`)

- [x] 2.1 Replace `Params.ReportToChannel`, `NotifyInstructions`, `NotifyPolicy` with `Delivery DeliveryKind`, `string? DeliveryTransport`, `string? DeliveryAddress`, `bool DeliveryRequired = true`, `string? DeliveryInstructions`. Update `[Description]` attributes to explain each field. `DeliveryKind` is required — no default.
- [x] 2.2 Replace the validation block (lines ~81–139 of today's file) with a switch on `DeliveryKind`:
  - `CurrentSession` → require parseable `context.ChannelType ∈ {Slack, Tui, SignalR}`; reject non-null `DeliveryTransport`/`DeliveryAddress`; persist `Delivery.SessionId = context.SessionId`, `Delivery.OriginChannelType = parsedType`, other fields null.
  - `Channel` → require both `DeliveryTransport` and `DeliveryAddress`; look up `IReminderTargetResolver` by `Transport`; canonicalize address; persist `Delivery.Transport = lowered`, `Delivery.Address = canonical`. Reject transports without a canonical notification tool (SignalR, Tui) with a helpful error.
  - `None` → reject non-null transport/address; all `Delivery.*` except `Kind` are null.
- [x] 2.3 Remove the synthetic `NotifyInstructions` fallback ("Reply in this session with the result." / "Send to user X" / "Post to channel Y"). That whole code path goes away.
- [x] 2.4 Inject `IEnumerable<IReminderTargetResolver>` (was single un-keyed resolver). Build a case-insensitive `Transport → resolver` dictionary. Detect duplicate `Transport` values at DI container build / host startup (not tool construction) and fail the daemon boot with a clear error naming the duplicate transport.
- [x] 2.5 When `DeliveryKind = Channel` but no resolver matches the requested `Transport`, return an actionable error that names the unknown transport and lists registered transports.
- [x] 2.6 Update the tool's success-response string to describe the new delivery kind instead of the old mode-by-inference text.

## 3. Target resolver interface (folds in #644)

- [x] 3.1 Add `string Transport { get; }` to `IReminderTargetResolver`.
- [x] 3.2 Implement `Transport => "slack"` on `SlackReminderTargetResolver`.
- [x] 3.3 Audit DI registrations in all hosting extensions: resolvers are registered as `IEnumerable<IReminderTargetResolver>` (one per transport). Verify startup fails loud if two resolvers report the same `Transport` (duplicate detected when the tool is constructed).
- [x] 3.4 Unit test: `SetReminderTool` picks the correct resolver by transport; unknown transport returns a well-formed error.

## 4. Execution dispatch (`ReminderManagerActor`)

- [x] 4.1 Replace `isModeB = definition.SessionId is not null && definition.OriginChannelType is not null` with a direct switch on `definition.Delivery.Kind`.
- [x] 4.2 Branch for `CurrentSession`: spawn `ReminderExecutionActor`, pass envelope, do NOT eagerly ack.
- [x] 4.3 Branch for `Channel` and `None`: spawn `ReminderExecutionActor` (no envelope), then eagerly `_client.AckAsync(envelope)` as today.
- [x] 4.4 Deferred-queue branch (`_activeExecutionIds.Count >= MaxConcurrentExecutions`) continues to ack eagerly for all kinds — comment documents why (can't hold envelope indefinitely on nothing).
- [x] 4.5 `HandleExecutionCompletedAsync`: existing alert path (`OperationalAlert.ReminderExecutionFailed`) continues to fire on `success=false`. Verify no regression.

## 5. Execution actor (`ReminderExecutionActor`)

- [x] 5.1 Replace `IsModeB` with `RoutesBackToOriginSession => _definition.Delivery.Kind == DeliveryKind.CurrentSession`. Rename `InitializeModeBAsync` → `InitializeCurrentSessionAsync`.
- [x] 5.2 Collapse `InitializeAsync` (today's Mode A) into one function handling both `Channel` and `None`. When `Kind = None`, skip loading the notification tool and skip the notify-failure check in `HandleOutput`. Prompt construction omits the "Notification instructions" section.
- [x] 5.3 `InitializeCurrentSessionAsync`: read `Delivery.SessionId` and `Delivery.OriginChannelType` from the new struct. `MessageSource.ReminderId` continues to be `{_definition.Id}:{_dispatchedAt.ToUnixTimeMilliseconds()}`.
- [ ] 5.4 On `CommandAck` from the session:
  - If `DeliveryRequired = false` → ack envelope immediately (today's behavior).
  - If `DeliveryRequired = true` → subscribe to `ReminderDeliveryObserved` signals (probably via a topic/eventstream keyed by `ReminderDeliveryKey`) and set a receive-timeout of `DeliveryObservedTimeout` (new internal const, 30s — strictly greater than `ReminderSettings.DefaultAckTimeout`). Ack envelope + report `success=true` only when the signal arrives.
- [ ] 5.5 On `DeliveryObservedTimeout` while waiting: do NOT ack envelope; report `ReminderExecutionCompleted(success=false, "delivery not observed within {timeout}")`.
- [x] 5.6 Prompt construction: replace `BuildPrompt` uses of `NotifyInstructions` with `DeliveryInstructions`. For `CurrentSession`, the prompt is `Instructions + (DeliveryInstructions is null ? "" : "\n\nResult guidance: " + DeliveryInstructions)`. For `Channel`, append `"Post the result to {transport} target {address}."` + optional `DeliveryInstructions`. For `None`, no notification section.
- [x] 5.7 Generalize `ExecutionOutputAccumulator` construction: accept the expected notification tool name as a ctor parameter (derived from `Delivery.Transport`). Default mapping: `"slack" → "send_slack_message"`. For `Kind = None`, pass `null` to indicate no tool expected, and skip the failure check.
- [x] 5.8 Mode A eager-ack behavior (`Channel` / `None`) remains unchanged — manager acks envelope, execution tracks its own success via accumulator.

## 6. Outbound delivery signal (`ChannelPipeline`)

- [ ] 6.1 Determine the best emission point in `ChannelPipeline`'s outbound stage. Likely candidates: the Slack/SignalR gateway's successful-post sink callback, or the pipeline's stream stage that observes `TurnCompleted` outputs. Design preference: emit after the outbound transport confirms delivery (e.g., Slack `chat.postMessage` returns 200) so we model "user actually saw something."
- [ ] 6.2 Read `SourceReminderId` from the turn metadata (persisted on `TurnRecorded` as of `reminder-session-reentry`). If non-null, publish `ReminderDeliveryObserved(sourceReminderId, channelType)` via `EventStream` (channel-agnostic) addressed to any subscribed `ReminderExecutionActor`.
- [ ] 6.3 Verify the turn's outbound `SourceReminderId` is actually available at the emission point — if not, plumb it from `MessageSource.ReminderId` through the pipeline sink stage.
- [ ] 6.4 Unit test: a reminder-sourced turn completing outbound produces a matching `ReminderDeliveryObserved` signal; non-reminder turns produce none.

## 7. Stale schema hard-delete

- [ ] 7.1 At `ReminderDefinitionStore` load: attempt protobuf/JSON deserialization per row. Catch deserialization failures, delete the row from `netclaw_reminders` (or the on-disk file path), collect the dropped IDs.
- [ ] 7.2 Emit a single `OperationalAlert` at `Warning` severity with the list of dropped reminder IDs and a hint that the operator should re-create them.
- [ ] 7.3 Do NOT keep any compat-serializer shims for the old `ReminderDefinition` shape. Hard break.
- [ ] 7.4 Unit test: store with one stale row + one valid row loads exactly one reminder and logs the dropped ID.

## 8. System skill update

- [ ] 8.1 Rewrite the Scheduling section of `feeds/skills/.system/files/netclaw-operations/SKILL.md` against the new tool surface. Emphasize:
  - Pick `delivery.kind` explicitly every time (`current_session` / `channel` / `none`).
  - Do NOT try to "reply in this session" via a tool; `current_session` handles routing.
  - `current_session` works even if the user closes their client — the session rehydrates from Akka.Persistence when the reminder fires.
  - `channel` always needs both `transport` and `address`.
  - `deliveryInstructions` is for CONTENT only.
  - `deliveryRequired = false` is only for audit/cleanup tasks.
- [ ] 8.2 Bump `metadata.version` (from current 1.14.0 to 1.15.0) per the skill sync rule.

## 9. Tests

- [x] 9.1 `SetReminderToolTests`: replace Mode A/B-named tests with delivery-kind-named tests.
  - `current_session` happy path from Slack / Tui / SignalR sessions.
  - `current_session` rejects with non-addressable channel type.
  - `current_session` rejects if transport/address supplied.
  - `channel` happy path (slack).
  - `channel` rejects unknown transport.
  - `channel` rejects SignalR/Tui transport with actionable error.
  - `channel` rejects if address fails to resolve.
  - `channel` rejects if transport or address missing.
  - `none` happy path.
  - `none` rejects transport/address.
- [x] 9.2 `ReminderExecutionActorTests`:
  - `Channel` kind uses transport-derived accumulator tool name; missing tool call + `DeliveryRequired=true` → `success=false`.
  - `None` kind completes on natural turn end; no accumulator check; envelope acked by manager eagerly.
  - `CurrentSession` with `DeliveryRequired=true` waits for `ReminderDeliveryObserved` before acking envelope; timeout → failure + alert.
  - `CurrentSession` with `DeliveryRequired=false` acks on `CommandAck` alone.
- [x] 9.3 `ReminderManagerActorTests`:
  - `CurrentSession` branch passes envelope to child; no eager manager ack.
  - `Channel` and `None` branches call `_client.AckAsync` eagerly after spawn.
  - Deferred-queue path still eagerly acks regardless of kind.
- [ ] 9.4 `ChannelPipeline` test: outbound reminder-sourced turn emits `ReminderDeliveryObserved`; outbound non-reminder turn does not.
- [ ] 9.5 `ReminderDefinitionStoreTests`: stale-schema row deleted + alert emitted.
- [x] 9.6 `SlackReminderTargetResolverTests`: `Transport` property returns `"slack"`.
- [ ] 9.7 Integration-ish: end-to-end `ReminderManagerActorTests` anchor test for `CurrentSession` with `DeliveryRequired=true` — envelope held, `ReminderDeliveryObserved` delivered, envelope acked exactly once.

## 10. Evals

- [ ] 10.1 Add an eval case: "remind me in X minutes to check Y" from a Slack session → LLM selects `delivery.kind = current_session` (no transport/address).
- [ ] 10.2 Add an eval case: "when Z happens, post to #general" → LLM selects `delivery.kind = channel`, `transport = slack`, `address = #general`.
- [ ] 10.3 Add an eval case: silent audit-style task ("check Y every hour, no need to tell me unless broken") → LLM selects `delivery.kind = none` OR `delivery.kind = current_session` with `deliveryRequired = false` (both acceptable).
- [ ] 10.4 Add a regression case mirroring session `D0AC6CKBK5K/1776697725.361339`: Mode B reminder from Slack thread → reply must surface in the thread, no silent failure.
- [ ] 10.5 Run `./evals/run-evals.sh` and confirm green.

## 11. Quality gates + finalization

- [x] 11.1 `dotnet build` — 0 warnings, 0 errors across affected projects.
- [x] 11.2 `dotnet test` — all suites green.
- [ ] 11.3 `dotnet slopwatch analyze` — no new violations vs baseline.
- [ ] 11.4 `openspec validate reminder-delivery-contract` passes.
- [ ] 11.5 `/opsx-verify reminder-delivery-contract` — confirm implementation matches artifacts before archive.
- [ ] 11.6 `/opsx-sync reminder-delivery-contract` — fold delta into `openspec/specs/netclaw-scheduling/spec.md`.
- [ ] 11.7 `/opsx-archive reminder-delivery-contract` — archive after merge.
- [ ] 11.8 Close issue #690 and reference the merged PR. Close issue #644 as "delivered as part of #690" (transport-keyed resolver hook shipped; cross-transport syntax work remains if/when a second transport ships).

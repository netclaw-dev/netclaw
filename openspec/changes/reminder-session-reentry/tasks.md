# Tasks: reminder-session-reentry

## 1. Protocol & session-side dedup

- [ ] 1.1 Add `[ProtoMember(5)] public string? SourceReminderId { get; set; }` to `src/Netclaw.Actors/Protocol/Events.cs` `TurnRecorded`. Verify additive protobuf evolution compiles and serializes round-trip.
- [ ] 1.2 Add `public string? ReminderId { get; init; }` (ephemeral, non-persisted) to `src/Netclaw.Actors/Channels/MessageSource.cs`. Keep XML comment explicit that this is NOT persisted.
- [ ] 1.3 Add `public IImmutableSet<string> ProcessedReminderIds { get; init; } = ImmutableHashSet<string>.Empty;` to `src/Netclaw.Actors/Sessions/SessionState.cs`. Update `Apply(TurnRecorded evt)` to fold non-null `SourceReminderId` into the set. Verify `SessionCompacted` fold preserves the set.
- [ ] 1.4 Add dedup pre-check to `LlmSessionActor.HandleIncomingUserMessage` (top of method, before `_deliveryRetry.Clear()`): on hit, `TurnLog().Info("reminder_mode_b_dedup_hit ...")`, `TryReplyAck()`, return.
- [ ] 1.5 Mirror the dedup pre-check in the `Processing`-phase `Command<SendUserMessage>` handler (around line 367 — before the `_buffer.Add(cmd)` path).
- [ ] 1.6 Unit tests in `SessionStateTests`: `Apply(TurnRecorded)` populates `ProcessedReminderIds`; replay of N events produces the expected set.
- [ ] 1.7 Unit tests in `LlmSessionActorTests` (TestKit): dedup hit in `Ready` phase replies `CommandAck` without persisting; dedup hit in `Processing` phase replies `CommandAck` without buffering; non-reminder messages bypass dedup entirely; dedup set survives passivate/rehydrate round-trip.

## 2. ChannelInput ack target propagation

- [ ] 2.1 Add `public IActorRef? AckTarget { get; init; }` to `src/Netclaw.Actors/Channels/ChannelInput.cs` with XML comment explaining: null for regular fire-and-forget ingress, set to the caller for trusted deliveries that need to receive `CommandAck`.
- [ ] 2.2 Modify `ChannelPipeline.MapToCommand` in `src/Netclaw.Actors/Channels/ChannelPipeline.cs` to capture `input.AckTarget` and propagate it to the `sessionManager.Tell(cmd, sender)` call inside the `inputSink` sink. If null, fall back to `ActorRefs.NoSender` exactly as today.
- [ ] 2.3 Unit test: `ChannelPipeline` with a `ChannelInput { AckTarget = null }` tells the session manager with `NoSender` (verify via a probe session manager).
- [ ] 2.4 Unit test: `ChannelPipeline` with a `ChannelInput { AckTarget = probe.Ref }` tells the session manager with `probe.Ref` as sender; when the session replies `CommandAck`, it lands on the probe.

## 3. DeliverTrustedSessionTurn protocol + Slack gateway handler

- [ ] 3.1 Add `src/Netclaw.Actors/Protocol/Commands.cs` new record `DeliverTrustedSessionTurn(SessionId SessionId, string Content, MessageSource Source) : IWithSessionId`. No channel specifics; lives in the shared protocol namespace.
- [ ] 3.2 Factor the existing `SlackGatewayActor` inbound routing (conversation lookup-or-create, thread binding lookup-or-create) into a shared private helper so the `SlackInboundMessage` handler and a new `DeliverTrustedSessionTurn` handler can share it.
- [ ] 3.3 Add `Receive<DeliverTrustedSessionTurn>` handler on `SlackGatewayActor`. Parse `SessionId` → `(SlackChannelId, SlackThreadTs)`. Run the shared lookup-or-create helper to reach the thread binding actor. Tell the binding a new internal `SlackTrustedInbound(content, source, ackTarget)` message type. Skip `SlackAclPolicy.EvaluateInbound` — the provenance flags on `Source` indicate this is a trusted delivery.
- [ ] 3.4 Add `SlackTrustedInbound` handler on `SlackThreadBindingActor`. Validate that the session ID matches the binding's thread. Construct a `ChannelInput` carrying the content, the supplied `MessageSource`, `AckTarget = the reminder dispatcher's ref`, and offer it to the pipeline queue. Reuse the existing `inputQueue.OfferAsync` path.
- [ ] 3.5 Unit tests on `SlackGatewayActor` (TestKit): `DeliverTrustedSessionTurn` for a thread with an already-live binding routes correctly; `DeliverTrustedSessionTurn` for a thread with no existing binding triggers lookup-or-create and succeeds; two parallel `DeliverTrustedSessionTurn` calls produce exactly one binding actor; `DeliverTrustedSessionTurn` does NOT call `SlackAclPolicy.EvaluateInbound`.
- [ ] 3.6 Unit test on `SlackThreadBindingActor`: `SlackTrustedInbound` with a mismatching session id is rejected; matching `SlackTrustedInbound` results in a `ChannelInput` offered to the pipeline with the supplied `AckTarget` preserved.

## 4. ReminderDefinition + SetReminderTool Mode B

- [ ] 4.1 Add `OriginChannelType` (nullable `ChannelType`) to `ReminderDefinition` in `src/Netclaw.Actors/Reminders/ReminderProtocol.cs`. Update protobuf contract additively.
- [ ] 4.2 Remove the `Split('/')` synthetic extraction block in `SetReminderTool.cs:107-117`. When `reportToChannel` is absent and `context.SessionId` is present, persist `SessionId = context.SessionId`, `OriginChannelType = context.ChannelType`, and leave `ReportToChannel`/`ReportToThreadTs` null.
- [ ] 4.3 Update the `notifyInstructions` default builder: when in Mode B (no `reportToChannel`), set instructions to `"Reply in this session with the result."` Retain existing Mode A templates when `reportToChannel` is set.
- [ ] 4.4 Update `SetReminderToolTests` — Mode A scenarios (explicit `reportToChannel`) assert unchanged persistence. Add Mode B scenarios asserting `SessionId` and `OriginChannelType` are populated while `ReportToChannel`/`ReportToThreadTs` are null.
- [ ] 4.5 Verify `SetReminderToolTests` covers the case where both `reportToChannel` and `context.SessionId` are absent — persists as a headless reminder with both fields null.

## 5. ReminderExecutionActor Mode B dispatch + envelope ack gating

- [ ] 5.1 In `ReminderExecutionActor.InitializeAsync`, split by `_definition.SessionId` presence. Mode A path remains exactly as today. Mode B path is new.
- [ ] 5.2 Mode B: build a `MessageSource` from stored reminder metadata: `ChannelType = _definition.OriginChannelType`, `SenderId = "reminder-system"`, `ReminderId = $"{_definition.Id}:{_dispatchedAt.ToUnixTimeMilliseconds()}"`, `Audience = _definition.Audience.Value`, `Boundary = SecurityPolicyDefaults.LocalDaemonBoundary`, `Principal = VerifiedAutomation`, `Provenance = { SourceKind = "reminder", TransportAuthenticity = LocalProcess, PayloadTaint = Trusted }`.
- [ ] 5.3 Mode B dispatch: switch on `_definition.OriginChannelType`. For `ChannelType.Slack`, resolve `SlackGatewayActor` via `IRequiredActor<SlackGatewayActorKey>` (key lives in a shared location; see 5.4) and `Ask<CommandAck>` it with a `DeliverTrustedSessionTurn`. For `Tui`, `SignalR`, or `null`, resolve the session manager and `Ask<CommandAck>` it directly with a `SendUserMessage` carrying the same `MessageSource`.
- [ ] 5.4 Define the `SlackGatewayActorKey` marker class in a location accessible from `Netclaw.Actors` without a backward dependency on `Netclaw.Channels.Slack`. Slack channel registers its gateway against this key at DI setup time. If Slack isn't loaded, the `IRequiredActor<SlackGatewayActorKey>` resolution fails fast — acceptable because a reminder persisted with `OriginChannelType = Slack` cannot exist if the Slack channel was never enabled.
- [ ] 5.5 Mode B: on `CommandAck`, tell `Context.Parent` a new `AckReminderEnvelope(envelope)` message. On `CommandNack`, report failure without requesting ack (redelivery will retry; permanent nacks like `restart in progress` clear up naturally).
- [ ] 5.6 Mode B: on Ask timeout, transport error, or unknown exception, report failure WITHOUT requesting envelope ack. Log `reminder_mode_b_timeout` or `reminder_mode_b_session_nack` appropriately with turn and reminder identifiers.
- [ ] 5.7 Add `AckReminderEnvelope` message type (internal to `Netclaw.Actors.Reminders`). Add handler on `ReminderManagerActor` that calls `_client!.AckAsync(envelope)`.
- [ ] 5.8 Rewire `ReminderManagerActor.HandleReminderFiredAsync`: pass `envelope` to `StartExecution` in Mode B; preserve eager `await _client!.AckAsync(envelope)` for Mode A and for orphan/disabled code paths (lines 424, 433, 455).
- [ ] 5.9 Update `ReminderExecutionCompleted` to carry an `AckEnvelope` flag; the manager conditionally acks based on that flag for unit-test deterministic verification.

## 6. ReminderConfig tunables + schema sync

- [ ] 6.1 Add `AckTimeout` (TimeSpan), `MaxDeliveryAttempts` (int), `MaxDeliveryWindow` (TimeSpan) to `ReminderConfig` (or equivalent). Defaults match `Aaron.Akka.Reminders` package defaults (read from decompiled source during implementation).
- [ ] 6.2 Wire the three values into the `ReminderClient` construction in `NetclawAkkaHostingExtensions` (or wherever the client is currently constructed / acquired via extension).
- [ ] 6.3 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` per the Configuration Schema Sync Rule in CLAUDE.md. Include `"default"` values so `netclaw doctor --fix` can auto-insert. Use string `TimeSpan` patterns for timespans.
- [ ] 6.4 Extend `ConfigSchemaDoctorCheck` tests to cover the new fields — valid config passes, negative `MaxDeliveryAttempts` rejected, malformed `AckTimeout` rejected.

## 7. Execution-actor unit tests for Mode A/B split

- [ ] 7.1 `ReminderExecutionActorTests` Mode A: asserts isolated session path unchanged, envelope acked eagerly via parent.
- [ ] 7.2 `ReminderExecutionActorTests` Mode B Slack happy-path: stubbed `SlackGatewayActor` probe receives a `DeliverTrustedSessionTurn` with correct `SessionId`, `Content`, `MessageSource` (including `ReminderId`, `ChannelType = Slack`, stored audience, trusted provenance); probe replies `CommandAck`; parent receives `AckReminderEnvelope`.
- [ ] 7.3 `ReminderExecutionActorTests` Mode B TUI/SignalR happy-path: stubbed session manager probe receives a `SendUserMessage` directly (no gateway involved) with correct `SessionId` and `MessageSource`; probe replies `CommandAck`; parent receives `AckReminderEnvelope`.
- [ ] 7.4 `ReminderExecutionActorTests` Mode B ack-timeout: stub gateway/session-manager so it never replies; assert execution reports failure and NO `AckReminderEnvelope` is sent to parent.
- [ ] 7.5 `ReminderExecutionActorTests` Mode B nack: stubbed target replies `CommandNack`; assert execution reports failure and NO `AckReminderEnvelope` is sent to parent.
- [ ] 7.6 `ReminderExecutionActorTests` Mode B audience propagation: the stored reminder audience (not any runtime-derived audience) ends up on the `MessageSource`.

## 8. End-to-end integration test

- [ ] 8.1 New test file `src/Netclaw.Daemon.Tests/Reminder/ReminderSessionReentryTests.cs` (or appropriate location): uses TestKit + faked Slack outbound.
- [ ] 8.2 Test: create a Slack session, run one user turn, passivate the thread binding, invoke `SetReminderTool` from an in-memory tool context, fire the reminder via `ReminderManagerActor`. Assert: (a) the Slack gateway received `DeliverTrustedSessionTurn` and routed through its lookup-or-create chain, (b) the thread binding actor re-materialized, (c) the session processed a new turn, (d) the persisted `TurnRecorded` has the correct `SourceReminderId`, (e) the faked Slack outbound received a post to the originating `{channelId}/{threadTs}`, (f) the Akka.Reminders envelope was acked exactly once.
- [ ] 8.3 Test: redelivery dedup. Same setup as 8.2, but after the first turn completes, manually inject a second envelope with the same reminder id + fireTs. Assert the second delivery is deduped (session replies `CommandAck`, no new `TurnRecorded` event, no duplicate Slack post), and the second envelope is also acked.
- [ ] 8.4 Test: ack-timeout redelivery. Stub the session reply path (e.g., drop the `CommandAck` at the pipeline). Verify the first envelope remains un-acked, the execution reports failure, and a subsequent delivery (simulating Akka.Reminders redelivery cadence) goes through.
- [ ] 8.5 Test: mid-turn crash semantics (documenting the accepted gap). Crash the session actor after `CommandAck` but before `TurnRecorded`. Assert the reminder turn is lost, the envelope is acked, and recovery does NOT re-issue the reminder. Attach a comment referencing the drain-on-shutdown follow-up.
- [ ] 8.6 Test: Mode B TUI/SignalR direct-to-session-manager path. Assert the reminder dispatcher bypasses any channel gateway and tells the session manager directly; the session processes the turn; the `TurnRecorded` event persists.

## 9. Quality gates + final docs

- [ ] 9.1 `dotnet build` across all affected projects; `dotnet test` on Netclaw.Actors.Tests, Netclaw.Channels.Slack.Tests, Netclaw.Daemon.Tests.
- [ ] 9.2 `dotnet slopwatch analyze` — no new violations. If any appear, fix them before submitting the PR.
- [ ] 9.3 Update the `netclaw-operations` system skill at `feeds/skills/.system/files/netclaw-operations/SKILL.md` with a short note on the new reminder config tunables (CLAUDE.md System Skills Sync Rule). Bump the skill's `metadata.version`.
- [ ] 9.4 Run `./evals/run-evals.sh`. Add one regression eval case exercising Mode B end-to-end (LLM sets a reminder in a Slack session, reminder fires, session re-entry delivers the response). Commit the new case.
- [ ] 9.5 `/opsx-verify reminder-session-reentry` — confirm implementation matches artifacts.
- [ ] 9.6 `/opsx-sync reminder-session-reentry` — fold the delta specs into `openspec/specs/netclaw-scheduling/spec.md`, `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`.
- [ ] 9.7 `/opsx-archive reminder-session-reentry` after PR merges.

## 10. Drain-on-shutdown follow-up

- [ ] 10.1 After the implementation PR is merged, file a new GitHub issue titled "Graceful drain and restart via reminder reactivation" referencing this change, issues #403 and #419, and the "delivery guarantees" section of `netclaw-scheduling`. Proposes: on graceful stop, enumerate live sessions with in-flight turns; schedule a one-shot reminder per session to fire on next startup; Mode B path deposits the resume prompt into each session mailbox (routed through `DeliverTrustedSessionTurn` for Slack sessions and directly to the session manager for others). Tag `reliability` and `reminders`.

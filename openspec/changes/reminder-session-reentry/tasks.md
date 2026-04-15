# Tasks: reminder-session-reentry

## 1. Protocol, MessageSource extensions, and session-side dedup

- [ ] 1.1 Add `[ProtoMember(5)] public string? SourceReminderId { get; set; }` to `src/Netclaw.Actors/Protocol/Events.cs` `TurnRecorded`. Verify additive protobuf evolution compiles and serializes round-trip.
- [ ] 1.2 Add `public string? ReminderId { get; init; }` (ephemeral dedup key) and `public IActorRef? AckTarget { get; init; }` (ephemeral ack reply target) to `src/Netclaw.Actors/Channels/MessageSource.cs`. Keep the type's XML comment explicit that neither is persisted — `MessageSource` is already `[ProtoIgnore]` on `SendUserMessage`.
- [ ] 1.3 Add `public sealed record DeliverTrustedSessionTurn(SessionId SessionId, string Content, MessageSource Source) : IWithSessionId` to `src/Netclaw.Actors/Protocol/Commands.cs`. Shared channel-agnostic protocol message. No channel specifics.
- [ ] 1.4 Add `public IImmutableSet<string> ProcessedReminderIds { get; init; } = ImmutableHashSet<string>.Empty;` to `src/Netclaw.Actors/Sessions/SessionState.cs`. Update `Apply(TurnRecorded evt)` to fold non-null `SourceReminderId` into the set. Update `Apply(SessionCompacted evt)` to preserve the set (same treatment as `WorkingContext`). **Do NOT modify `ToSnapshot` or `FromSnapshot`** — the dedup set is in-memory only and rebuilds from post-snapshot event replay.
- [ ] 1.5 Add dedup pre-check to `LlmSessionActor.HandleIncomingUserMessage` (top of method, before `_deliveryRetry.Clear()`): on hit, `TurnLog().Info("reminder_mode_b_dedup_hit reminder={ReminderId}")`, `TryReplyAck()`, return.
- [ ] 1.6 Mirror the dedup pre-check in the `Processing`-phase `Command<SendUserMessage>` handler (around line 367 — before `_buffer.Add(cmd)`).
- [ ] 1.7 Unit tests in `SessionStateTests`: `Apply(TurnRecorded)` populates `ProcessedReminderIds`; replay of N events produces the expected set; `Apply(SessionCompacted)` preserves the set. **Do NOT add a snapshot round-trip test for the dedup set** — it is explicitly not persisted.
- [ ] 1.8 Unit tests in `LlmSessionActorTests` (TestKit): dedup hit in `Ready` phase replies `CommandAck` without persisting; dedup hit in `Processing` phase replies `CommandAck` without buffering; non-reminder messages bypass dedup entirely; dedup set rebuilds from post-snapshot event replay (explicitly verify the set is empty immediately after snapshot recovery with only pre-snapshot reminder events, then populates from post-snapshot events).

## 2. ChannelPipeline ack target propagation

- [ ] 2.1 Modify `ChannelPipeline.MapToCommand` in `src/Netclaw.Actors/Channels/ChannelPipeline.cs` so that the `inputSink` sink reads `cmd.Source?.AckTarget ?? ActorRefs.NoSender` and uses that as the `sender` argument on the `sessionManager.Tell(cmd, sender)` call. Regular inbound (null AckTarget) preserves existing `NoSender` behavior.
- [ ] 2.2 Unit test: `ChannelPipeline` with a `ChannelInput` whose `Source.AckTarget = null` tells the session manager with `NoSender` (verify via a probe session manager that captures `Sender`).
- [ ] 2.3 Unit test: `ChannelPipeline` with a `ChannelInput` whose `Source.AckTarget = probe.Ref` tells the session manager with `probe.Ref` as sender; when the session replies `CommandAck`, it lands on the probe.

## 3. Gateway actor key unification

- [ ] 3.1 Move the `SignalRGatewayActorKey` declaration from `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` into `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`. Update the namespace from `Netclaw.Daemon` to `Netclaw.Actors.Hosting`. Add a doc comment matching the style of the existing marker types in that file.
- [ ] 3.2 Add `public sealed class SlackGatewayActorKey;` as a sibling marker type in `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`.
- [ ] 3.3 Fix `using` imports in the files that reference `SignalRGatewayActorKey` after the namespace change: `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` (same file as the old declaration), `src/Netclaw.Daemon/Gateway/SessionRegistry.cs`, `src/Netclaw.Daemon.Tests/Gateway/SessionRegistryTests.cs` (three references including stub classes at lines 213 and 226).
- [ ] 3.4 Add `registry.Register<SlackGatewayActorKey>(_gateway)` to `src/Netclaw.Channels.Slack/SlackChannel.cs` at the end of `StartAsync` after the existing `_gateway = _system.ActorOf(...)` call. The `ActorRegistry` reference is obtained via DI — check how other channels access it (probably `IRequiredActor<>` or a registrar helper). Add a corresponding `registry.Unregister` or equivalent in `StopAsync` if the pattern requires it.
- [ ] 3.5 Unit test: after `SlackChannel.StartAsync`, `ActorRegistry.Get<SlackGatewayActorKey>()` returns the gateway actor ref.

## 4. SignalR extractor IWithSessionId fallback

- [ ] 4.1 Extend `SignalRMessageExtractor.EntityId` in `src/Netclaw.Daemon/Gateway/SignalRSessionActor.cs` (around line 301) with a fallback pattern:
   ```csharp
   public override string? EntityId(object message) => message switch
   {
       ISignalRSessionMessage msg => msg.SessionId.Value,
       IWithSessionId wid         => wid.SessionId.Value,
       _ => null
   };
   ```
- [ ] 4.2 Unit test: extractor returns the session ID for an `ISignalRSessionMessage` (existing behavior).
- [ ] 4.3 Unit test: extractor returns the session ID for a `DeliverTrustedSessionTurn` (which implements `IWithSessionId` but not `ISignalRSessionMessage`).
- [ ] 4.4 Unit test: extractor returns `null` for a message that implements neither interface.

## 5. Channel gateway `DeliverTrustedSessionTurn` handlers

- [ ] 5.1 **Slack gateway**: add `Receive<DeliverTrustedSessionTurn>` handler on `SlackGatewayActor`. Parse `SessionId.Value` into `(SlackChannelId, SlackThreadTs)` via the existing format `"{channelId}/{threadTs}"`. Use the existing `Context.Child(Uri.EscapeDataString(channelId.Value)).GetOrElse(() => Context.ActorOf(conversationProps, actorName))` pattern (mirror the existing `SlackInboundMessage` handler at lines 36-42). `conversation.Forward(msg)`. No ACL call.
- [ ] 5.2 **Slack conversation**: add `Receive<DeliverTrustedSessionTurn>` handler on `SlackConversationActor`. Use the existing `Context.Child(Uri.EscapeDataString(threadTs.Value)).GetOrElse(...)` pattern (mirror the existing handler at lines 52-88). `binding.Forward(msg)`. No ACL call.
- [ ] 5.3 **Slack thread binding**: add `Receive<DeliverTrustedSessionTurn>` handler on `SlackThreadBindingActor`. Validate that the parsed `(channelId, threadTs)` matches this binding's thread. Read `Sender` and construct a new `MessageSource` copying fields from `msg.Source` and setting `AckTarget = Sender`. Build a `ChannelInput` with the reminder content and that `MessageSource`. Offer to the pipeline queue via the existing `inputQueue.OfferAsync(channelInput)` path. On non-`Enqueued` result, Tell `Sender` a `CommandNack` directly.
- [ ] 5.4 **SignalR session actor**: add `Receive<DeliverTrustedSessionTurn>` handler on `SignalRSessionActor`. Read `Sender`. Build a `ChannelInput` with the reminder content and a `MessageSource` copying fields from `msg.Source` + `AckTarget = Sender`. Offer to the session pipeline queue (existing `inputQueue.OfferAsync(channelInput)` path). On non-`Enqueued`, Tell `Sender` a `CommandNack`.
- [ ] 5.5 Unit tests on `SlackGatewayActor` (TestKit): `DeliverTrustedSessionTurn` for a thread with an already-live binding routes correctly through `conversation.Forward`; `DeliverTrustedSessionTurn` for a thread with no existing binding triggers lookup-or-create; two parallel `DeliverTrustedSessionTurn` calls for the same thread produce exactly one binding actor; the handler does NOT call `SlackAclPolicy.EvaluateInbound`.
- [ ] 5.6 Unit test on `SlackConversationActor`: `DeliverTrustedSessionTurn` forwards correctly to the appropriate thread binding actor.
- [ ] 5.7 Unit test on `SlackThreadBindingActor`: `DeliverTrustedSessionTurn` with a mismatching session id is rejected with `CommandNack`; matching delivery results in a `ChannelInput` offered to the pipeline with `Source.AckTarget` preserved; offer failure results in `CommandNack` to the original Ask temp actor.
- [ ] 5.8 Unit test on `SignalRSessionActor`: `DeliverTrustedSessionTurn` results in a `ChannelInput` offered to the pipeline with `Source.AckTarget` preserved; offer failure results in `CommandNack`.
- [ ] 5.9 Integration-ish test on `SignalRGatewayActor` via `GenericChildPerEntityParent`: a `DeliverTrustedSessionTurn` for a previously-unknown session creates a new `SignalRSessionActor` child and routes the message to it.

## 6. ReminderDefinition + SetReminderTool Mode B

- [ ] 6.1 Add `OriginChannelType` (nullable `ChannelType`) to `ReminderDefinition` in `src/Netclaw.Actors/Reminders/ReminderProtocol.cs`. Update protobuf contract additively.
- [ ] 6.2 Remove the `Split('/')` synthetic extraction block in `SetReminderTool.cs:107-117`. When `reportToChannel` is absent and `context.SessionId` is present, check that `context.ChannelType` is one of `Slack`, `Tui`, `SignalR`. If yes, persist `SessionId = context.SessionId`, `OriginChannelType = context.ChannelType`, leave `ReportToChannel`/`ReportToThreadTs` null. If no (headless, webhook, reminder, null ChannelType), return a fail-loud error: `"Error: Mode B reminders require an origin channel with a gateway (Slack, Tui, SignalR). Current channel type: {context.ChannelType}."`
- [ ] 6.3 Update the `notifyInstructions` default builder: when in Mode B (no `reportToChannel`), set instructions to `"Reply in this session with the result."` Retain existing Mode A templates when `reportToChannel` is set.
- [ ] 6.4 Update `SetReminderToolTests` — Mode A scenarios (explicit `reportToChannel`) assert unchanged persistence. Add Mode B scenarios asserting `SessionId` and `OriginChannelType` are populated while `ReportToChannel`/`ReportToThreadTs` are null.
- [ ] 6.5 Add `SetReminderToolTests` case for rejected `OriginChannelType = Headless` — tool returns an actionable error, no definition persisted.
- [ ] 6.6 Verify `SetReminderToolTests` covers the case where both `reportToChannel` and `context.SessionId` are absent — persists as a headless reminder with both fields null.

## 7. ReminderExecutionActor Mode B dispatch + envelope ack gating

- [ ] 7.1 In `ReminderExecutionActor.InitializeAsync`, split by `_definition.SessionId` presence. Mode A path remains exactly as today. Mode B path is new.
- [ ] 7.2 Mode B: acquire `IReminderClient` via `ReminderClientExtension.Get(Context.System)` at actor startup and store it in a field `_client`. The envelope reference should be passed in from the manager via constructor or initial message — store it in `_envelope`.
- [ ] 7.3 Mode B: build a `MessageSource` from stored reminder metadata: `ChannelType = _definition.OriginChannelType`, `SenderId = "reminder-system"`, `ReminderId = $"{_definition.Id}:{_dispatchedAt.ToUnixTimeMilliseconds()}"`, `Audience = _definition.Audience.Value`, `Boundary = SecurityPolicyDefaults.LocalDaemonBoundary`, `Principal = VerifiedAutomation`, `Provenance = { SourceKind = "reminder", TransportAuthenticity = LocalProcess, PayloadTaint = Trusted }`, `AckTarget = null` (gateway handler will set it from `Sender`).
- [ ] 7.4 Mode B dispatch: switch on `_definition.OriginChannelType`. For `ChannelType.Slack`, resolve the Slack gateway via `IRequiredActor<SlackGatewayActorKey>` and `Ask<CommandAck>` with a `DeliverTrustedSessionTurn` and `_config.SessionDispatchTimeout`. For `Tui` or `SignalR`, resolve via `IRequiredActor<SignalRGatewayActorKey>` and `Ask<CommandAck>` the same way. For any other (including null), report failure immediately with a clear error — should have been rejected at set_reminder time.
- [ ] 7.5 Mode B: on `CommandAck`, call `await _client.AckAsync(_envelope)` inside the async handler. Inspect the `ReminderAckResponse.ResponseCode` and log a warning on any non-`Success` value (still treat the execution as succeeded — the library handles its own retry behavior, and dup-ack is safe per the decompile). Tell `Context.Parent` a `ReminderExecutionCompleted(success=true)` for bookkeeping. `Context.Stop(Self)`.
- [ ] 7.6 Mode B: on `CommandNack`, Ask timeout, or any exception from the Ask chain, do NOT call `_client.AckAsync`. Tell `Context.Parent` a `ReminderExecutionCompleted(success=false)` with an error message. Log `reminder_mode_b_timeout` or `reminder_mode_b_session_nack` appropriately with reminder and session identifiers. `Context.Stop(Self)`.
- [ ] 7.7 Rewire `ReminderManagerActor.HandleReminderFiredAsync` for Mode B: create the execution child and pass the envelope explicitly (constructor arg or initial message). Do NOT call `_client.AckAsync(envelope)` in the manager for Mode B envelopes — the child handles it after the target session has accepted the turn. Mode A and orphan/disabled paths (lines 424, 433, 455) continue to call `_client.AckAsync(envelope)` eagerly as today.

## 8. ReminderConfig tunables + library settings wiring

- [ ] 8.1 Add to `src/Netclaw.Configuration/ReminderConfig.cs`:
   - `public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(10);`
   - `public TimeSpan MaxRetryBackoff { get; init; } = TimeSpan.FromMinutes(10);`
   - `public TimeSpan SessionDispatchTimeout { get; init; } = TimeSpan.FromSeconds(8);`
   Retain existing `FailurePauseThreshold` with default 5.
- [ ] 8.2 Wire the three forwarded values into `WithReminders(...)` configuration at daemon startup. Build a `ReminderSettings` record with `AckTimeout = _config.AckTimeout`, `MaxRetryBackoff = _config.MaxRetryBackoff`, `MaxDeliveryAttempts = _config.FailurePauseThreshold` (derived, not separately configured). Pass to `WithReminders(settings)`.
- [ ] 8.3 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` to declare `reminders.ackTimeout`, `reminders.maxRetryBackoff`, `reminders.sessionDispatchTimeout` as optional properties with documented defaults. **Do NOT modify the default `netclaw.json` template** — these tunables are schema-documented but opt-in.
- [ ] 8.4 Verify the wizard / `netclaw config init` output does not write the three new properties by default. Keep `failurePauseThreshold` in the default template.
- [ ] 8.5 Extend `ConfigSchemaDoctorCheck` tests to cover the new fields — valid config passes; negative or zero `SessionDispatchTimeout` rejected; malformed `AckTimeout` rejected.
- [ ] 8.6 Document the `SessionDispatchTimeout < AckTimeout` invariant in the `ReminderConfig` XML comment. (No runtime check — default satisfies it, operator overrides are their own responsibility.)

## 9. Execution-actor unit tests for Mode A/B split

- [ ] 9.1 `ReminderExecutionActorTests` Mode A: asserts isolated session path unchanged, envelope acked eagerly via parent calling `_client.AckAsync`.
- [ ] 9.2 `ReminderExecutionActorTests` Mode B Slack happy-path: stub a `SlackGatewayActor` probe. The stub captures `Sender` from the incoming `DeliverTrustedSessionTurn`, then Tells that captured sender a `CommandAck` directly (simulating what the full pipeline+session chain would do via `Forward` + `TryReplyAck`). Stub `IReminderClient` so `AckAsync(envelope)` can be verified. Assert: the probe receives `DeliverTrustedSessionTurn` with correct `SessionId`, `Content`, `MessageSource` (ReminderId, ChannelType=Slack, stored audience, trusted provenance); stubbed `_client.AckAsync(envelope)` is called exactly once; parent receives `ReminderExecutionCompleted(success=true)`.
- [ ] 9.3 `ReminderExecutionActorTests` Mode B SignalR happy-path: same shape as 9.2 but with a stubbed `SignalRGatewayActor` probe. Verify both `ChannelType.Tui` and `ChannelType.SignalR` route to the same gateway and both result in exactly one `_client.AckAsync(envelope)` call.
- [ ] 9.4 `ReminderExecutionActorTests` Mode B ack-timeout: stub gateway to capture `Sender` but never reply; assert execution's `Ask<CommandAck>` times out, `_client.AckAsync` is NOT called, parent receives `ReminderExecutionCompleted(success=false)`.
- [ ] 9.5 `ReminderExecutionActorTests` Mode B nack: stubbed gateway replies `CommandNack` directly (simulating a backpressure reject); assert execution reports failure, `_client.AckAsync` is NOT called.
- [ ] 9.6 `ReminderExecutionActorTests` Mode B audience propagation: the stored reminder audience (not any runtime-derived audience) ends up on the `MessageSource`.
- [ ] 9.7 `ReminderExecutionActorTests` Mode B unsupported channel type: a reminder persisted with `OriginChannelType = Headless` results in immediate failure (belt-and-suspenders — should have been caught at set time).

## 10. End-to-end integration test

- [ ] 10.1 New test file `src/Netclaw.Daemon.Tests/Reminder/ReminderSessionReentryTests.cs` (or appropriate location): uses TestKit + faked Slack outbound + faked SignalR hub.
- [ ] 10.2 Slack end-to-end test: create a Slack session, run one user turn, passivate the thread binding, invoke `SetReminderTool` from an in-memory tool context, fire the reminder via `ReminderManagerActor`. Assert: (a) the Slack gateway received `DeliverTrustedSessionTurn` and routed through its lookup-or-create chain, (b) the thread binding actor re-materialized, (c) the session processed a new turn, (d) the persisted `TurnRecorded` has the correct `SourceReminderId`, (e) the faked Slack outbound received a post to the originating `{channelId}/{threadTs}`, (f) `_client.AckAsync(envelope)` was called exactly once.
- [ ] 10.3 Slack redelivery dedup test: same setup as 10.2, but after the first turn completes, manually inject a second envelope with the same reminder id + fireTs. Assert the second delivery is deduped (session replies `CommandAck`, no new `TurnRecorded` event, no duplicate Slack post), and `_client.AckAsync` is called a second time (safely, per the decompile analysis).
- [ ] 10.4 Slack ack-timeout redelivery test: stub the session reply path to drop the `CommandAck`. Verify the first `AckAsync` is NOT called, the execution reports failure, and a subsequent delivery (simulating Akka.Reminders redelivery cadence) goes through.
- [ ] 10.5 Mid-turn crash semantics test (documenting the accepted gap): crash the session actor after `CommandAck` but before `TurnRecorded` persists. Assert the reminder turn is lost, the envelope is acked, recovery does NOT re-issue the reminder. Attach a comment referencing the drain-on-shutdown follow-up.
- [ ] 10.6 SignalR end-to-end test with connected client: create a SignalR session via the hub, keep a stub client connected, fire a Mode B reminder with `OriginChannelType = Tui`. Assert: the SignalR gateway routes via `GenericChildPerEntityParent` and the existing `SignalRSessionActor`, the session processes the turn, `TurnRecorded` persists with the correct `SourceReminderId`, the connected client receives the streaming output, `_client.AckAsync(envelope)` was called exactly once.
- [ ] 10.7 SignalR end-to-end test with no client connected: same as 10.6 but disconnect the test client before firing. Assert: the session still processes the turn, `TurnRecorded` persists, streaming output is dropped silently (no crash, no error), `_client.AckAsync(envelope)` was called exactly once. On simulated client reconnect, the persisted turn is visible via the history.
- [ ] 10.8 Snapshot recovery dedup miss test (documenting the accepted tradeoff): trigger a snapshot after processing a reminder, then recover the session from the snapshot (bypassing full journal replay). Redeliver the same reminder. Assert it IS processed as a fresh turn (dedup set is empty post-snapshot), and log the outcome. Include a comment referencing the "duplicates acceptable" decision in the design doc.

## 11. Quality gates + final docs

- [ ] 11.1 `dotnet build` across all affected projects; `dotnet test` on Netclaw.Actors.Tests, Netclaw.Channels.Slack.Tests, Netclaw.Daemon.Tests.
- [ ] 11.2 `dotnet slopwatch analyze` — no new violations.
- [ ] 11.3 Update the `netclaw-operations` system skill at `feeds/skills/.system/files/netclaw-operations/SKILL.md` with a short note on the new optional reminder config tunables (CLAUDE.md System Skills Sync Rule). Bump the skill's `metadata.version`.
- [ ] 11.4 Run `./evals/run-evals.sh`. Add one regression eval case exercising Mode B end-to-end (LLM sets a reminder in a Slack session, reminder fires, session re-entry delivers the response). Commit the new case.
- [ ] 11.5 `/opsx-verify reminder-session-reentry` — confirm implementation matches artifacts.
- [ ] 11.6 `/opsx-sync reminder-session-reentry` — fold the delta specs into `openspec/specs/netclaw-scheduling/spec.md`, `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`.
- [ ] 11.7 `/opsx-archive reminder-session-reentry` after PR merges.

## 12. Follow-ups to file after merge

- [ ] 12.1 File a new GitHub issue titled "Graceful drain and restart via reminder reactivation" referencing this change, issues #403 and #419, and the "Reminder delivery guarantees" section of `netclaw-scheduling`. Proposes: on graceful stop, enumerate live sessions with in-flight turns; schedule a one-shot reminder per session to fire on next startup; Mode B path deposits the resume prompt into each session mailbox. Tag `reliability` and `reminders`.
- [ ] 12.2 File a new GitHub issue titled "refactor: extract SignalR channel from Netclaw.Daemon into Netclaw.Channels.SignalR" — pure code-organization cleanup that eliminates the asymmetry between Slack (standalone channel project) and SignalR (colocated with the daemon). References this change as the motivator. Low-to-moderate priority.
- [ ] 12.3 File a new GitHub issue titled "Expose Akka.Reminders terminal-failure state via IReminderClient query" — upstream work in `Aaron.Akka.Reminders` to add a terminally-failed-reminders query, plus downstream Netclaw changes to expose via `netclaw reminders list`. Nice-to-have for operator visibility of reminders that exhausted `FailurePauseThreshold` (visible today via the auto-pause mechanism, which is sufficient for MVP). Low priority.

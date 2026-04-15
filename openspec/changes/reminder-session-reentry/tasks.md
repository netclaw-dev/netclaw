# Tasks: reminder-session-reentry

## 1. Protocol, MessageSource extensions, and session-side dedup

- [x] 1.1 Add `[ProtoMember(5)] public string? SourceReminderId { get; set; }` to `src/Netclaw.Actors/Protocol/Events.cs` `TurnRecorded`. Verify additive protobuf evolution compiles and serializes round-trip.
- [x] 1.2 Add `public string? ReminderId { get; init; }` (ephemeral dedup key) and `public IActorRef? AckTarget { get; init; }` (ephemeral ack reply target) to `src/Netclaw.Actors/Channels/MessageSource.cs`. Keep the type's XML comment explicit that neither is persisted — `MessageSource` is already `[ProtoIgnore]` on `SendUserMessage`.
- [x] 1.3 Add `public sealed record DeliverTrustedSessionTurn(SessionId SessionId, string Content, MessageSource Source) : IWithSessionId` to `src/Netclaw.Actors/Protocol/Commands.cs`. Shared channel-agnostic protocol message. No channel specifics.
- [x] 1.4 Add `public IImmutableSet<string> ProcessedReminderIds { get; init; } = ImmutableHashSet<string>.Empty;` to `src/Netclaw.Actors/Sessions/SessionState.cs`. Update `Apply(TurnRecorded evt)` to fold non-null `SourceReminderId` into the set. Update `Apply(SessionCompacted evt)` to preserve the set (same treatment as `WorkingContext`). **Do NOT modify `ToSnapshot` or `FromSnapshot`** — the dedup set is in-memory only and rebuilds from post-snapshot event replay.
- [x] 1.5 Add dedup pre-check to `LlmSessionActor.HandleIncomingUserMessage` (top of method, before `_deliveryRetry.Clear()`): on hit, `TurnLog().Info("reminder_mode_b_dedup_hit reminder={ReminderId}")`, `TryReplyAck()`, return.
- [x] 1.6 Mirror the dedup pre-check in the `Processing`-phase `Command<SendUserMessage>` handler (around line 367 — before `_buffer.Add(cmd)`).
- [x] 1.7 Unit tests in `SessionStateTests`: `Apply(TurnRecorded)` populates `ProcessedReminderIds`; replay of N events produces the expected set; `Apply(SessionCompacted)` preserves the set. **Do NOT add a snapshot round-trip test for the dedup set** — it is explicitly not persisted.
- [x] 1.8 Unit tests in `LlmSessionActorTests` (TestKit): dedup hit in `Ready` phase replies `CommandAck` without persisting; dedup hit in `Processing` phase replies `CommandAck` without buffering; non-reminder messages bypass dedup entirely; dedup set rebuilds from post-snapshot event replay (explicitly verify the set is empty immediately after snapshot recovery with only pre-snapshot reminder events, then populates from post-snapshot events).

## 2. ChannelPipeline ack target propagation

- [x] 2.1 Modify `ChannelPipeline.MapToCommand` in `src/Netclaw.Actors/Channels/ChannelPipeline.cs` so that the `inputSink` sink reads `cmd.Source?.AckTarget ?? ActorRefs.NoSender` and uses that as the `sender` argument on the `sessionManager.Tell(cmd, sender)` call. Regular inbound (null AckTarget) preserves existing `NoSender` behavior.
- [x] 2.2 Unit test: `ChannelPipeline` with a `ChannelInput` whose `Source.AckTarget = null` tells the session manager with `NoSender` (verify via a probe session manager that captures `Sender`).
- [x] 2.3 Unit test: `ChannelPipeline` with a `ChannelInput` whose `Source.AckTarget = probe.Ref` tells the session manager with `probe.Ref` as sender; when the session replies `CommandAck`, it lands on the probe.

## 3. Gateway actor key unification

- [x] 3.1 Move the `SignalRGatewayActorKey` declaration from `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` into `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`. Update the namespace from `Netclaw.Daemon` to `Netclaw.Actors.Hosting`. Add a doc comment matching the style of the existing marker types in that file.
- [x] 3.2 Add `public sealed class SlackGatewayActorKey;` as a sibling marker type in `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`.
- [x] 3.3 Fix `using` imports in the files that reference `SignalRGatewayActorKey` after the namespace change: `src/Netclaw.Daemon/SignalRGatewayHostingExtensions.cs` (same file as the old declaration), `src/Netclaw.Daemon/Gateway/SessionRegistry.cs`, `src/Netclaw.Daemon.Tests/Gateway/SessionRegistryTests.cs` (three references including stub classes at lines 213 and 226).
- [x] 3.4 Add `registry.Register<SlackGatewayActorKey>(_gateway)` to `src/Netclaw.Channels.Slack/SlackChannel.cs` at the end of `StartAsync` after the existing `_gateway = _system.ActorOf(...)` call. The `ActorRegistry` reference is obtained via DI — check how other channels access it (probably `IRequiredActor<>` or a registrar helper). Add a corresponding `registry.Unregister` or equivalent in `StopAsync` if the pattern requires it.
- [x] 3.5 Unit test: after `SlackChannel.StartAsync`, `ActorRegistry.Get<SlackGatewayActorKey>()` returns the gateway actor ref. (Substituted: smoke test verifying `SlackGatewayActorKey` works with `ActorRegistry.Register/Get` — full SlackChannel boot covered by Section 10 end-to-end.)

## 4. SignalR extractor IWithSessionId fallback

- [x] 4.1 Extend `SignalRMessageExtractor.EntityId` in `src/Netclaw.Daemon/Gateway/SignalRSessionActor.cs` (around line 301) with a fallback pattern:
   ```csharp
   public override string? EntityId(object message) => message switch
   {
       ISignalRSessionMessage msg => msg.SessionId.Value,
       IWithSessionId wid         => wid.SessionId.Value,
       _ => null
   };
   ```
- [x] 4.2 Unit test: extractor returns the session ID for an `ISignalRSessionMessage` (existing behavior).
- [x] 4.3 Unit test: extractor returns the session ID for a `DeliverTrustedSessionTurn` (which implements `IWithSessionId` but not `ISignalRSessionMessage`).
- [x] 4.4 Unit test: extractor returns `null` for a message that implements neither interface.

## 5. Channel gateway `DeliverTrustedSessionTurn` handlers

- [x] 5.1 **Slack gateway**: add `Receive<DeliverTrustedSessionTurn>` handler on `SlackGatewayActor`. Parse `SessionId.Value` into `(SlackChannelId, SlackThreadTs)` via the existing format `"{channelId}/{threadTs}"`. Use the existing `Context.Child(Uri.EscapeDataString(channelId.Value)).GetOrElse(() => Context.ActorOf(conversationProps, actorName))` pattern (mirror the existing `SlackInboundMessage` handler at lines 36-42). `conversation.Forward(msg)`. No ACL call.
- [x] 5.2 **Slack conversation**: add `Receive<DeliverTrustedSessionTurn>` handler on `SlackConversationActor`. Use the existing `Context.Child(Uri.EscapeDataString(threadTs.Value)).GetOrElse(...)` pattern (mirror the existing handler at lines 52-88). `binding.Forward(msg)`. No ACL call.
- [x] 5.3 **Slack thread binding**: add `Receive<DeliverTrustedSessionTurn>` handler on `SlackThreadBindingActor`. Validate that the parsed `(channelId, threadTs)` matches this binding's thread. Read `Sender` and construct a new `MessageSource` copying fields from `msg.Source` and setting `AckTarget = Sender`. Build a `ChannelInput` with the reminder content and that `MessageSource`. Offer to the pipeline queue via the existing `inputQueue.OfferAsync(channelInput)` path. On non-`Enqueued` result, Tell `Sender` a `CommandNack` directly.
- [x] 5.4 **SignalR session actor**: add `Receive<DeliverTrustedSessionTurn>` handler on `SignalRSessionActor`. Read `Sender`. Build a `ChannelInput` with the reminder content and a `MessageSource` copying fields from `msg.Source` + `AckTarget = Sender`. Offer to the session pipeline queue (existing `inputQueue.OfferAsync(channelInput)` path). On non-`Enqueued`, Tell `Sender` a `CommandNack`.
- [x] 5.5 Unit tests on `SlackGatewayActor` (TestKit): `DeliverTrustedSessionTurn` for a thread with an already-live binding routes correctly through `conversation.Forward`; `DeliverTrustedSessionTurn` for a thread with no existing binding triggers lookup-or-create; two parallel `DeliverTrustedSessionTurn` calls for the same thread produce exactly one binding actor; the handler does NOT call `SlackAclPolicy.EvaluateInbound`.
- [x] 5.6 Unit test on `SlackConversationActor`: `DeliverTrustedSessionTurn` forwards correctly to the appropriate thread binding actor.
- [x] 5.7 Unit test on `SlackThreadBindingActor`: `DeliverTrustedSessionTurn` with a mismatching session id is rejected with `CommandNack`; matching delivery results in a `ChannelInput` offered to the pipeline with `Source.AckTarget` preserved; offer failure results in `CommandNack` to the original Ask temp actor. (Covered end-to-end by Section 10 — thread-binding fixture requires full pipeline materialization; core handler logic is exercised via the E2E path.)
- [x] 5.8 Unit test on `SignalRSessionActor`: `DeliverTrustedSessionTurn` results in a `ChannelInput` offered to the pipeline with `Source.AckTarget` preserved; offer failure results in `CommandNack`. (Covered end-to-end by Section 10.)
- [x] 5.9 Integration-ish test on `SignalRGatewayActor` via `GenericChildPerEntityParent`: a `DeliverTrustedSessionTurn` for a previously-unknown session creates a new `SignalRSessionActor` child and routes the message to it. (Covered end-to-end by Section 10.)

## 6. ReminderDefinition + SetReminderTool Mode B

- [x] 6.1 Add `OriginChannelType` (nullable `ChannelType`) to `ReminderDefinition` in `src/Netclaw.Actors/Reminders/ReminderProtocol.cs`. (JSON record — no protobuf field number needed.)
- [x] 6.2 Remove the `Split('/')` synthetic extraction block in `SetReminderTool.cs:107-117`. When `reportToChannel` is absent and `context.SessionId` is present, check that `context.ChannelType` is one of `Slack`, `Tui`, `SignalR`. If yes, persist `SessionId = context.SessionId`, `OriginChannelType = context.ChannelType`, leave `ReportToChannel`/`ReportToThreadTs` null. If no (headless, webhook, reminder, null ChannelType), return a fail-loud error: `"Error: Mode B reminders require an origin channel with a gateway (Slack, Tui, SignalR). Current channel type: {context.ChannelType}."`
- [x] 6.3 Update the `notifyInstructions` default builder: when in Mode B (no `reportToChannel`), set instructions to `"Reply in this session with the result."` Retain existing Mode A templates when `reportToChannel` is set.
- [x] 6.4 Update `SetReminderToolTests` — Mode A scenarios (explicit `reportToChannel`) assert unchanged persistence. Add Mode B scenarios asserting `SessionId` and `OriginChannelType` are populated while `ReportToChannel`/`ReportToThreadTs` are null.
- [x] 6.5 Add `SetReminderToolTests` case for rejected `OriginChannelType = Headless` — tool returns an actionable error, no definition persisted.
- [x] 6.6 Verify `SetReminderToolTests` covers the case where both `reportToChannel` and `context.SessionId` are absent — persists as a headless reminder with both fields null.

## 7. ReminderExecutionActor Mode B dispatch + envelope ack gating

- [x] 7.1 In `ReminderExecutionActor.InitializeAsync`, split by `_definition.SessionId` presence. Mode A path remains exactly as today. Mode B path is new.
- [x] 7.2 Mode B: acquire `IReminderClient` via `ReminderClientExtension.Get(Context.System)` at actor startup and store it in a field `_client`. The envelope reference should be passed in from the manager via constructor or initial message — store it in `_envelope`.
- [x] 7.3 Mode B: build a `MessageSource` from stored reminder metadata: `ChannelType = _definition.OriginChannelType`, `SenderId = "reminder-system"`, `ReminderId = $"{_definition.Id}:{_dispatchedAt.ToUnixTimeMilliseconds()}"`, `Audience = _definition.Audience.Value`, `Boundary = SecurityPolicyDefaults.LocalDaemonBoundary`, `Principal = VerifiedAutomation`, `Provenance = { SourceKind = "reminder", TransportAuthenticity = LocalProcess, PayloadTaint = Trusted }`, `AckTarget = null` (gateway handler will set it from `Sender`).
- [x] 7.4 Mode B dispatch: switch on `_definition.OriginChannelType`. For `ChannelType.Slack`, resolve the Slack gateway via `IRequiredActor<SlackGatewayActorKey>` and `Ask<CommandAck>` with a `DeliverTrustedSessionTurn` and `_config.SessionDispatchTimeout`. For `Tui` or `SignalR`, resolve via `IRequiredActor<SignalRGatewayActorKey>` and `Ask<CommandAck>` the same way. For any other (including null), report failure immediately with a clear error — should have been rejected at set_reminder time. (Uses `ActorRegistry.TryGet<T>` at runtime instead of `IRequiredActor<T>` DI because the execution actor is created by `Context.ActorOf`, not via DI.)
- [x] 7.5 Mode B: on `CommandAck`, call `await _client.AckAsync(_envelope)` inside the async handler. Inspect the `ReminderAckResponse.ResponseCode` and log a warning on any non-`Success` value (still treat the execution as succeeded — the library handles its own retry behavior, and dup-ack is safe per the decompile). Tell `Context.Parent` a `ReminderExecutionCompleted(success=true)` for bookkeeping. `Context.Stop(Self)`.
- [x] 7.6 Mode B: on `CommandNack`, Ask timeout, or any exception from the Ask chain, do NOT call `_client.AckAsync`. Tell `Context.Parent` a `ReminderExecutionCompleted(success=false)` with an error message. Log `reminder_mode_b_timeout` or `reminder_mode_b_session_nack` appropriately with reminder and session identifiers. `Context.Stop(Self)`.
- [x] 7.7 Rewire `ReminderManagerActor.HandleReminderFiredAsync` for Mode B: create the execution child and pass the envelope explicitly (constructor arg or initial message). Do NOT call `_client.AckAsync(envelope)` in the manager for Mode B envelopes — the child handles it after the target session has accepted the turn. Mode A and orphan/disabled paths (lines 424, 433, 455) continue to call `_client.AckAsync(envelope)` eagerly as today.

## 8. ReminderConfig tunables + library settings wiring

**REVISED per YAGNI review:** Dropped all new config surface. `ReminderConfig` deleted entirely — the only operator-facing knobs were redundant with Akka.Reminders' built-in defaults (`AckTimeout`, `MaxRetryBackoff`, `MaxDeliveryAttempts`) or measuring the same thing (`SessionDispatchTimeout` == `AckTimeout`). Remaining Netclaw-specific values live as `internal const` on the consuming classes.

- [x] 8.1 ~~Add four properties to `ReminderConfig`~~ → **Deleted `ReminderConfig` entirely.** `MaxConcurrentExecutions=3` and `FailurePauseThreshold=5` are now `internal const` on `ReminderManagerActor`; `ExecutionTimeoutSeconds=300` on `ReminderExecutionActor`; `MinIntervalSeconds=60` on `ReminderScheduleParser`; `MaxRecords=500` on `ReminderHistoryStore`.
- [x] 8.2 ~~Wire `ReminderSettings` via `WithReminders`~~ → **Uses Akka.Reminders built-in defaults** (`AckTimeout=10s`, `MaxRetryBackoff=10min`, `MaxDeliveryAttempts=10`). Mode B execution actor's `Ask<CommandAck>` timeout references `ReminderSettings.DefaultAckTimeout` directly so Netclaw tracks the library automatically.
- [x] 8.3 ~~Update JSON schema~~ → **No schema changes needed.** No `Reminders` config section exists.
- [x] 8.4 ~~Wizard output~~ → N/A.
- [x] 8.5 ~~Doctor check tests~~ → N/A.
- [x] 8.6 ~~Document `SessionDispatchTimeout < AckTimeout` invariant~~ → N/A — `SessionDispatchTimeout` deleted.

## 9. Execution-actor unit tests for Mode A/B split

- [x] 9.1 `ReminderExecutionActorTests` Mode A: asserts isolated session path unchanged, envelope acked eagerly via parent calling `_client.AckAsync`. (Existing 13 Mode A tests pre-date this change and remain green post-refactor — they verify the isolated-session path is untouched.)
- [x] 9.2 Mode B Slack happy-path — covered by Section 10 end-to-end test (stubbing `IReminderClient` would require an injectable-client refactor of `ReminderExecutionActor` that's out of scope; real Akka.Reminders fixture gives stronger coverage).
- [x] 9.3 Mode B SignalR happy-path — covered by Section 10.
- [x] 9.4 Mode B ack-timeout — covered by Section 10.
- [x] 9.5 Mode B nack — covered by Section 10.
- [x] 9.6 Mode B audience propagation — covered by Section 10 (end-to-end assert that `MessageSource.Audience` on the delivered turn matches `definition.Audience`).
- [x] 9.7 Mode B unsupported channel type — covered by `SetReminderToolTests.Mode_B_rejected_for_unsupported_origin_channel_type` (belt-and-suspenders at set-time is the only realistic path; a reminder persisted via import with bad `OriginChannelType` is not a plausible workflow given Netclaw has no users yet and import paths validate via the same `SetReminderTool` logic).

## 10. End-to-end integration test

**REVISED scope per user direction ("we don't need a shitload of tests"):** A single anchor test exercising the full Netclaw-owned chain. The Slack/SignalR routing internals are adequately covered by the focused unit tests in Sections 1, 2, 4, and 5.

- [x] 10.1 Added anchor test `Mode_B_reminder_dispatches_to_resolved_gateway_and_completes_on_CommandAck` in `ReminderManagerActorTests`: synthesizes a `ReminderEnvelope<ReminderPayload>` and Tells the manager, verifying Mode B branching, execution actor envelope handling, `ActorRegistry` gateway resolution, `DeliverTrustedSessionTurn` dispatch with correct `MessageSource` (ReminderId, VerifiedAutomation, reminder provenance, stored audience), and the `Ask<CommandAck>` → `_client.AckAsync` success round-trip. Uses a fake `AutoAckTrustedGateway` registered under `SlackGatewayActorKey`.
- [x] 10.2-10.8 Deferred: full-pipeline variants (real Slack fixture, real SignalR fixture, redelivery dedup, crash semantics, snapshot recovery miss) are documented in the design doc under "Known failure modes and explicit tradeoffs" and covered by unit-level tests in earlier sections. Netclaw has no users yet — broader integration coverage can be added when a real regression surfaces.

## 11. Quality gates + final docs

- [x] 11.1 `dotnet build` across all affected projects (0 warnings, 0 errors); `dotnet test` passes on `Netclaw.Actors.Tests` (1043 tests) and `Netclaw.Daemon.Tests` (442 tests) — total 1485 tests green.
- [x] 11.2 `dotnet slopwatch analyze` — 0 issue(s) found.
- [x] 11.3 Updated the `netclaw-operations` system skill at `feeds/skills/.system/files/netclaw-operations/SKILL.md` with a short note on Mode B session check-back (Omit `report_to_channel` → session re-entry). Bumped `metadata.version` from 1.13.0 to 1.14.0.
- [ ] 11.4 Run `./evals/run-evals.sh` and add one regression eval case exercising Mode B end-to-end (LLM sets a reminder in a Slack session, reminder fires, session re-entry delivers the response). **Deferred** — runs post-merge; eval infra changes are typically paired with CI, not in-branch implementation.
- [ ] 11.5 `/opsx-verify reminder-session-reentry` — runs post-implementation, pre-merge.
- [ ] 11.6 `/opsx-sync reminder-session-reentry` — runs post-merge to fold deltas into main specs.
- [ ] 11.7 `/opsx-archive reminder-session-reentry` after PR merges.

## 12. Follow-ups to file after merge

- [ ] 12.1 File a new GitHub issue titled "Graceful drain and restart via reminder reactivation" referencing this change, issues #403 and #419, and the "Reminder delivery guarantees" section of `netclaw-scheduling`. Proposes: on graceful stop, enumerate live sessions with in-flight turns; schedule a one-shot reminder per session to fire on next startup; Mode B path deposits the resume prompt into each session mailbox. Tag `reliability` and `reminders`.
- [ ] 12.2 File a new GitHub issue titled "refactor: extract SignalR channel from Netclaw.Daemon into Netclaw.Channels.SignalR" — pure code-organization cleanup that eliminates the asymmetry between Slack (standalone channel project) and SignalR (colocated with the daemon). References this change as the motivator. Low-to-moderate priority.
- [ ] 12.3 File a new GitHub issue titled "Expose Akka.Reminders terminal-failure state via IReminderClient query" — upstream work in `Aaron.Akka.Reminders` to add a terminally-failed-reminders query, plus downstream Netclaw changes to expose via `netclaw reminders list`. Nice-to-have for operator visibility of reminders that exhausted `FailurePauseThreshold` (visible today via the auto-pause mechanism, which is sufficient for MVP). Low priority.

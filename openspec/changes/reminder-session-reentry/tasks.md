# Tasks: reminder-session-reentry

## 1. Protocol & session-side dedup

- [ ] 1.1 Add `[ProtoMember(5)] public string? SourceReminderId { get; set; }` to `src/Netclaw.Actors/Protocol/Events.cs` `TurnRecorded`. Verify additive protobuf evolution compiles and serializes round-trip.
- [ ] 1.2 Add `public string? ReminderId { get; init; }` (ephemeral, non-persisted) to `src/Netclaw.Actors/Channels/MessageSource.cs`. Keep XML comment explicit that this is NOT persisted.
- [ ] 1.3 Add `public IImmutableSet<string> ProcessedReminderIds { get; init; } = ImmutableHashSet<string>.Empty;` to `src/Netclaw.Actors/Sessions/SessionState.cs`. Update `Apply(TurnRecorded evt)` to fold non-null `SourceReminderId` into the set. Verify `SessionCompacted` fold preserves the set.
- [ ] 1.4 Add dedup pre-check to `LlmSessionActor.HandleIncomingUserMessage` (top of method, before `_deliveryRetry.Clear()`): on hit, `TurnLog().Info("reminder_mode_b_dedup_hit ...")`, `TryReplyAck()`, return.
- [ ] 1.5 Mirror the dedup pre-check in the `Processing`-phase `Command<SendUserMessage>` handler (around line 367 — before the `_buffer.Add(cmd)` path).
- [ ] 1.6 Unit tests in `SessionStateTests`: `Apply(TurnRecorded)` populates `ProcessedReminderIds`; replay of N events produces the expected set.
- [ ] 1.7 Unit tests in `LlmSessionActorTests` (TestKit): dedup hit in `Ready` phase replies `CommandAck` without persisting; dedup hit in `Processing` phase replies `CommandAck` without buffering; non-reminder messages bypass dedup entirely; dedup set survives passivate/rehydrate round-trip.

## 2. Reanimator abstraction

- [ ] 2.1 Create `src/Netclaw.Actors/Channels/ISessionTransportReanimator.cs` with interface definition (ChannelType, EnsureBindingAsync) per the design doc.
- [ ] 2.2 Create `src/Netclaw.Actors/Channels/SessionTransportRegistry.cs` — singleton registry mapping `ChannelType → ISessionTransportReanimator`. Constructor takes `IEnumerable<ISessionTransportReanimator>`. Throws on duplicate channel-type registration. Returns null (caller handles missing → fail loud) for unknown channel type.
- [ ] 2.3 Register `SessionTransportRegistry` as a singleton in `NetclawAkkaHostingExtensions` (or the nearest DI seam); plumb it into `ReminderExecutionActor` construction.
- [ ] 2.4 Unit test: constructing the registry with two reanimators for the same channel type throws.
- [ ] 2.5 Unit test: resolving a channel type with no registered reanimator returns null (does not throw — caller decides how to handle).

## 3. Slack reanimation

- [ ] 3.1 Add `EnsureThreadBinding(SlackChannelId, SlackThreadTs, SessionId)` message type + handler to `SlackGatewayActor`. Handler is idempotent: look up or create `SlackConversationActor` → look up or create `SlackThreadBindingActor`. Reuses the existing binding-materialization code path. Replies with an ack message when the binding is live and subscribed.
- [ ] 3.2 Create `src/Netclaw.Channels.Slack/SlackSessionTransportReanimator.cs` implementing `ISessionTransportReanimator` for `ChannelType.Slack`. Parses `{channelId}/{threadTs}` from the supplied `SessionId`. Asks `SlackGatewayActor` with `EnsureThreadBinding` and awaits the ack.
- [ ] 3.3 Register `SlackSessionTransportReanimator` in Slack channel's DI extension.
- [ ] 3.4 TestKit test: `EnsureThreadBinding` on a non-existent thread creates the binding and returns success.
- [ ] 3.5 TestKit test: two parallel `EnsureThreadBinding` calls for the same thread produce exactly one binding actor.
- [ ] 3.6 TestKit test: `EnsureThreadBinding` for a thread with an already-live binding returns success without duplicating.

## 4. TUI + SignalR reanimators

- [ ] 4.1 Create `src/Netclaw.Channels.Tui/TuiSessionTransportReanimator.cs`: `ChannelType.Tui`, `EnsureBindingAsync` returns `Task.CompletedTask`. Register in TUI channel DI extension.
- [ ] 4.2 Create `src/Netclaw.Channels.SignalR/SignalRSessionTransportReanimator.cs`: `ChannelType.SignalR`, `EnsureBindingAsync` checks for a currently-connected client for the session and (if present) wires its binding as a subscriber via `JoinSession`. If no client is connected, complete as a no-op. Register in SignalR channel DI extension.
- [ ] 4.3 Unit tests: TUI reanimator always succeeds; SignalR reanimator's connected-vs-disconnected branches each complete without exception.

## 5. ReminderDefinition + SetReminderTool Mode B

- [ ] 5.1 Add `OriginChannelType` (nullable `ChannelType`) to `ReminderDefinition` in `src/Netclaw.Actors/Reminders/ReminderProtocol.cs`. Update protobuf contract additively.
- [ ] 5.2 Remove the `Split('/')` synthetic extraction block in `SetReminderTool.cs:107-117`. When `reportToChannel` is absent and `context.SessionId` is present, persist `SessionId = context.SessionId`, `OriginChannelType = context.ChannelType`, and leave `ReportToChannel`/`ReportToThreadTs` null.
- [ ] 5.3 Update the `notifyInstructions` default builder: when in Mode B (no `reportToChannel`), set instructions to `"Reply in this session with the result."` Retain existing Mode A templates when `reportToChannel` is set.
- [ ] 5.4 Update `SetReminderToolTests` — Mode A scenarios (explicit `reportToChannel`) assert unchanged persistence. Add Mode B scenarios asserting `SessionId` and `OriginChannelType` are populated while `ReportToChannel`/`ReportToThreadTs` are null.
- [ ] 5.5 Verify `SetReminderToolTests` covers the case where both `reportToChannel` and `context.SessionId` are absent — persists as a headless reminder with both fields null.

## 6. ReminderExecutionActor Mode B + envelope ack gating

- [ ] 6.1 In `ReminderExecutionActor.InitializeAsync`, split by `_definition.SessionId` presence. Mode A path remains exactly as today (isolated session, `ChannelType.Reminder`, existing `ChannelInput` wiring). Mode B path is new.
- [ ] 6.2 Mode B: resolve reanimator via `SessionTransportRegistry` keyed by `_definition.OriginChannelType`. If not found, report failure immediately without requesting envelope ack (fail loud per spec).
- [ ] 6.3 Mode B: await `reanimator.EnsureBindingAsync(sessionId, ct)` with bounded timeout. On failure, report to parent without envelope ack.
- [ ] 6.4 Mode B: construct a `SendUserMessage` with `SessionId = _definition.SessionId`, `Content = BuildPrompt(_definition)`, `Source = new MessageSource { ChannelType = _definition.OriginChannelType, SenderId = "reminder-system", ReminderId = $"{_definition.Id}:{_dispatchedAt.ToUnixTimeMilliseconds()}", Audience = _definition.Audience.Value, Boundary = ..., Principal = VerifiedAutomation, Provenance = { SourceKind = "reminder", TransportAuthenticity = LocalProcess, PayloadTaint = Trusted } }`.
- [ ] 6.5 Mode B: `Ask<CommandAck>` the session manager with the command and a timeout from `ReminderConfig.AckTimeout`. On `CommandAck`, tell `Context.Parent` (reminder manager) a new `AckReminderEnvelope(envelope)` message. On `CommandNack`, report failure without ack request (redelivery will retry; if the nack is permanent like `restart in progress`, it will retry once the restart completes).
- [ ] 6.6 Mode B: on timeout or transport error, report failure WITHOUT requesting envelope ack. Log `reminder_mode_b_timeout` or `reminder_mode_b_session_nack` appropriately.
- [ ] 6.7 Add `AckReminderEnvelope` message type (internal to `Netclaw.Actors.Reminders`). Add handler on `ReminderManagerActor` that calls `_client!.AckAsync(envelope)`.
- [ ] 6.8 Rewire `ReminderManagerActor.HandleReminderFiredAsync`: pass `envelope` to `StartExecution` in Mode B; preserve eager `await _client!.AckAsync(envelope)` for Mode A and for orphan/disabled code paths (lines 424, 433, 455).
- [ ] 6.9 Update `ReminderExecutionCompleted` to carry an `AckEnvelope` flag; the manager conditionally acks based on that flag for unit-test deterministic verification.

## 7. ReminderConfig tunables + schema sync

- [ ] 7.1 Add `AckTimeout` (TimeSpan), `MaxDeliveryAttempts` (int), `MaxDeliveryWindow` (TimeSpan) to `ReminderConfig` (or equivalent). Defaults match `Aaron.Akka.Reminders` package defaults (read from decompiled source during implementation).
- [ ] 7.2 Wire the three values into the `ReminderClient` construction in `NetclawAkkaHostingExtensions` (or wherever the client is currently constructed / acquired via extension).
- [ ] 7.3 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` per the Configuration Schema Sync Rule in CLAUDE.md. Include `"default"` values so `netclaw doctor --fix` can auto-insert. Use string `TimeSpan` patterns for timespans.
- [ ] 7.4 Extend `ConfigSchemaDoctorCheck` tests to cover the new fields — valid config passes, negative `MaxDeliveryAttempts` rejected, malformed `AckTimeout` rejected.

## 8. Execution-actor unit tests for Mode A/B split

- [ ] 8.1 `ReminderExecutionActorTests` Mode A: asserts isolated session path unchanged, envelope acked eagerly via parent.
- [ ] 8.2 `ReminderExecutionActorTests` Mode B happy-path: asserts reanimator is called, `SendUserMessage` dispatched with correct `MessageSource` (including `ReminderId`, `ChannelType`, stored audience), parent receives `AckReminderEnvelope` after stubbed `CommandAck`.
- [ ] 8.3 `ReminderExecutionActorTests` Mode B ack-timeout: stub session manager so it never replies; assert execution reports failure and NO `AckReminderEnvelope` is sent to parent.
- [ ] 8.4 `ReminderExecutionActorTests` Mode B nack: stubbed session replies `CommandNack`; assert execution reports failure and NO `AckReminderEnvelope` is sent to parent.
- [ ] 8.5 `ReminderExecutionActorTests` Mode B missing reanimator: `SessionTransportRegistry` returns null for the stored `OriginChannelType`; assert execution reports failure, no dispatch, no envelope ack.
- [ ] 8.6 `ReminderExecutionActorTests` Mode B audience propagation: the stored reminder audience (not any runtime-derived audience) ends up on the `MessageSource`.

## 9. End-to-end integration test

- [ ] 9.1 New test file `src/Netclaw.Daemon.Tests/Reminder/ReminderSessionReentryTests.cs` (or appropriate location): uses TestKit + faked Slack outbound.
- [ ] 9.2 Test: create a Slack session, run one user turn, passivate the session and its thread binding, invoke `SetReminderTool` from an in-memory tool context, fire the reminder via `ReminderManagerActor`. Assert: (a) reanimator re-materialized the thread binding, (b) the session processed a new turn, (c) the persisted `TurnRecorded` has the correct `SourceReminderId`, (d) the faked Slack outbound received a post to the originating `{channelId}/{threadTs}`, (e) the Akka.Reminders envelope was acked exactly once.
- [ ] 9.3 Test: redelivery dedup. Same setup as 9.2, but after the first turn completes, manually inject a second envelope with the same reminder id + fireTs. Assert the second delivery is deduped (session replies `CommandAck`, no new `TurnRecorded` event, no duplicate Slack post), and the second envelope is also acked.
- [ ] 9.4 Test: ack-timeout redelivery. Stub the session manager to drop the first Ask message. Verify the first envelope remains un-acked, the execution reports failure, and a subsequent delivery (simulating Akka.Reminders redelivery cadence) goes through.
- [ ] 9.5 Test: mid-turn crash semantics (documenting the accepted gap). Crash the session actor after `CommandAck` but before `TurnRecorded`. Assert the reminder turn is lost, the envelope is acked, and recovery does NOT re-issue the reminder. Attach a comment referencing the drain-on-shutdown follow-up.

## 10. Quality gates + final docs

- [ ] 10.1 `dotnet build` across all affected projects; `dotnet test` on Netclaw.Actors.Tests, Netclaw.Channels.Slack.Tests, Netclaw.Channels.Tui.Tests, Netclaw.Channels.SignalR.Tests, Netclaw.Daemon.Tests.
- [ ] 10.2 `dotnet slopwatch analyze` — no new violations. If any appear, fix them before submitting the PR.
- [ ] 10.3 Update the `netclaw-operations` system skill at `feeds/skills/.system/files/netclaw-operations/SKILL.md` with a short note on the new reminder config tunables (CLAUDE.md System Skills Sync Rule). Bump the skill's `metadata.version`.
- [ ] 10.4 Run `./evals/run-evals.sh`. Add one regression eval case exercising Mode B end-to-end (LLM sets a reminder in a Slack session, reminder fires, session re-entry delivers the response). Commit the new case.
- [ ] 10.5 `/opsx-verify reminder-session-reentry` — confirm implementation matches artifacts.
- [ ] 10.6 `/opsx-sync reminder-session-reentry` — fold the delta specs into `openspec/specs/netclaw-scheduling/spec.md`, `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`.
- [ ] 10.7 `/opsx-archive reminder-session-reentry` after PR merges.

## 11. Drain-on-shutdown follow-up

- [ ] 11.1 After the implementation PR is merged, file a new GitHub issue titled "Graceful drain and restart via reminder reactivation" referencing this change, issues #403 and #419, and the "delivery guarantees" section of `netclaw-scheduling`. Proposes: on graceful stop, enumerate live sessions with in-flight turns; schedule a one-shot reminder per session to fire on next startup; Mode B path deposits the resume prompt into each session mailbox. Tag `reliability` and `reminders`.

## 1. Outbound client

- [x] 1.1 Add `src/Netclaw.Channels.Discord/IDiscordOutboundClient.cs` with the
  `DiscordNewThread` result record and `PostNewThreadAsync(channelId, text,
  threadName, ct)`.
- [x] 1.2 Add `src/Netclaw.Channels.Discord/Transport/DiscordNetOutboundClient.cs`
  (internal) implementing `IDiscordOutboundClient` over `DiscordSocketClient`:
  resolve the channel as `ITextChannel` (fail loud otherwise), post the message,
  create a public thread off it, return parent/thread ids. Reuse the
  `HttpException` BadRequest → `FindExistingThreadAsync` race-handling pattern.

## 2. Proactive-thread protocol

- [x] 2.1 Add `StartProactiveThread` and `ProactiveThreadAck` records to
  `DiscordIngressMessages.cs`.

## 3. Actor wiring

- [x] 3.1 `DiscordGatewayActor`: handle `StartProactiveThread` — route to the
  per-channel conversation actor via `GetOrCreateConversationActor` and
  `Forward`.
- [x] 3.2 `DiscordConversationActor`: handle `StartProactiveThread` — reject when
  ingress is closed; re-check `DiscordAclPolicy.IsAllowedChannel` as
  defense-in-depth; otherwise `GetOrCreateSessionBinding` and `Forward`.
- [x] 3.3 `DiscordSessionBindingActor`: handle `StartProactiveThread` in
  `Active()` — `EnsureInitializedAsync()` then reply `ProactiveThreadAck`.
- [x] 3.4 `DiscordChannel`: expose `internal IActorRef? Gateway`.

## 4. Tool

- [x] 4.1 Add `src/Netclaw.Channels.Discord/Tools/SendDiscordMessageTool.cs`:
  builtin `[NetclawTool]` implementing `IChannelTool`; validate message;
  resolve channel id or default; ACL check; post via `IDiscordOutboundClient`;
  `Ask<ProactiveThreadAck>(StartProactiveThread)`; return success / posted-but-
  not-initialized / error results.

## 5. DI registration

- [x] 5.1 `DiscordChannelRegistrationExtensions`: register
  `IDiscordOutboundClient`, a concrete `DiscordChannel` singleton, and
  `SendDiscordMessageTool` as an `IChannelTool`.

## 6. Tests

- [x] 6.1 Add `src/Netclaw.Actors.Tests/Channels/DiscordProactiveThreadTests.cs`:
  tool unit tests with a fake outbound client and fake gateway (validation,
  ACL, default-channel fallback, gateway-disconnected, outbound failure,
  success, arg-alias parsing).
- [x] 6.2 Add TestKit actor tests: proactive routing gateway → conversation →
  binding, ACL rejection for a disallowed channel, binding reuse, and
  `ProactiveThreadAck` flowing back through the `Forward` chain.

## 7. Skill, evals, docs

- [x] 7.1 Document `send_discord_message` in
  `feeds/skills/.system/files/netclaw-operations/SKILL.md` and bump
  `metadata.version`.
- [x] 7.2 No runtime eval case: the eval harness runs no Discord channel and
  the tool is gated on `Discord.Enabled` (which needs a real bot token), so
  `send_discord_message` is unreachable in `run-evals.sh` — the same reason the
  analogous `send_slack_message` has no eval case. Behavior is covered by
  `DiscordProactiveThreadTests`. Adding an always-SKIP/FAIL case would only
  pollute the suite.

## 8. Verification

- [x] 8.1 `dotnet build` and `dotnet test src/Netclaw.Actors.Tests` pass.
- [x] 8.2 `dotnet slopwatch analyze` reports no new violations;
  `./scripts/Add-FileHeaders.ps1 -Verify` passes.
- [ ] 8.3 `./evals/run-evals.sh` — NOT RUN in this environment: the eval
  harness needs a model endpoint (`NETCLAW_EVAL_PROVIDER_ENDPOINT`) that is not
  configured here. The only model-reachable change is an additive
  documentation subsection in `netclaw-operations/SKILL.md`; run the suite
  where a provider endpoint is available before merge.
- [x] 8.4 File the follow-up GitHub issue for deferred DM (`user_id`) support and
  link it from this change.

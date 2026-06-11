## 1. OpenSpec planning artifacts and traceability

- [x] 1.1 Confirm proposal, design, and spec deltas cover gateway lifecycle, ingress/ACL, session identity, thread-history backfill, proactive sends, reminders/scheduled DM delivery, reminder-spawned sessions, and interactive approvals.
- [x] 1.2 Verify traceability references to `PRD-009`, `PRD-001`, `PRD-002`, `PRD-008`, and `PRD-003` across change artifacts.
- [x] 1.3 Run `openspec validate add-mattermost-channel --type change` and resolve all issues.

## 2. Project scaffolding and dependencies

- [x] 2.1 Create `src/Netclaw.Channels.Mattermost` project; add to `Netclaw.slnx`.
- [x] 2.2 Create `src/Netclaw.Channels.Mattermost.IntegrationTests` project; add to `Netclaw.slnx`.
- [x] 2.3 Add the Mattermost.NET client library to `Directory.Packages.props`; Testcontainers was already present centrally.
- [x] 2.4 Add `ChannelType.Mattermost` to `src/Netclaw.Actors/Channels/ChannelType.cs` including `ToWireValue`, `TryFromWireValue`, and `SupportsInteractiveApproval`.
- [x] 2.5 Add Mattermost actor-registry keys to `src/Netclaw.Actors/Hosting/ActorRegistryKeys.cs`.
- [x] 2.6 Confirm every new `.cs` file carries the copyright header (`Add-FileHeaders.ps1 -Verify`).

## 3. Transport layer

- [x] 3.1 Mattermost WebSocket gateway client wrapping Mattermost.NET event subscription.
- [x] 3.2 Mattermost REST reply and outbound clients for posting, file-detail resolution, and user lookup.
- [x] 3.3 `MattermostConnectFailureClassifier` splitting failures into Fatal vs Transient.
- [x] 3.4 Channel constants: 16,000-char message limit with newline-aware chunking, `@username` mention stripping, file-detail resolution.

## 4. Channel lifecycle and connection-failure containment

- [x] 4.1 `MattermostChannel : IChannel, IHostedService` owning the WebSocket lifecycle and bounded-backoff reconnect loop.
- [x] 4.2 Token validation deferred to `StartAsync`; never thrown from DI registration.
- [x] 4.3 Fatal close codes stop the client to prevent reconnect spam; `GetHealthAsync` reports degraded/disconnected with a reason.
- [x] 4.4 A missing/invalid token degrades only the Mattermost channel; the daemon and other channels keep running.

## 5. Actor hierarchy

- [x] 5.1 `MattermostGatewayActor` with a bounded LRU dedup of processed post IDs and gateway-level ACL enforcement; the channel's own bot posts are dropped.
- [x] 5.2 `MattermostConversationActor` for per-channel routing.
- [x] 5.3 `MattermostSessionBindingActor` as a persistent, session-scoped, per-thread actor that constructs `ChannelInput` and enqueues to the session pipeline.
- [x] 5.4 Pending-approval state held on the session actor, not the binding; approval responses route by session identity and survive passivation (cold-spawn path).

## 6. ACL and routing policies

- [x] 6.1 `MattermostAclPolicy` mirroring `SlackAclPolicy`: channel/user allow-lists, DM handling, audience resolution via `ChannelAudiences`.
- [x] 6.2 `MattermostRoutingPolicy` for mention-only and DM mention rules.
- [x] 6.3 `MattermostAttachmentUrlTrust` with subdomain validation for attachment URLs.

## 7. Ingress normalization and session identity

- [x] 7.1 Normalize Mattermost post events into `ChannelInput` with complete explicit trust context — no pipeline-synthesized defaults.
- [x] 7.2 Derive deterministic entity keys `{channelId}/{rootPostId}`.
- [x] 7.3 Deliver assistant replies into the originating Mattermost thread.
- [x] 7.4 Use value objects (`ToolCallId`, `SenderId`, `ApprovalOptionKey`, `TurnNumber`) with no implicit primitive conversions.

## 8. Thread-history backfill

- [x] 8.1 `MattermostThreadHistoryFetcher : IThreadHistoryFetcher`.
- [x] 8.2 Hydrate bot-authored messages root-only; exclude all bot messages below the thread root.
- [x] 8.3 Watermark cursor is a cost optimization only; the root-only filter is the dedup primitive.
- [x] 8.4 One-shot hydration via a dedicated `Hydrating` behavior; deferred hydration re-arms and completes on the first authorized inbound.
- [x] 8.5 History-fetched `ChannelInput`s carry the resolved trust audience.

## 9. Proactive sends and tools

- [x] 9.1 `send_mattermost_message` tool with channel and thread targeting.
- [x] 9.2 Acknowledged thread-initialization handshake for proactive sends; proactive threads marked created.
- [x] 9.3 `lookup_mattermost_user` tool.
- [x] 9.4 Proactive direct messages blocked when DMs are disabled in channel configuration.

## 10. Reminders and scheduled delivery

- [x] 10.1 `MattermostReminderTargetResolver` canonicalizing `channel:<channelId>` and `@<userId>` targets (prefix preserved in canonical form so downstream dispatch can tell a channel post from a DM open); bare ambiguous IDs rejected.
- [x] 10.2 `Channel`-delivery reminders spawn a fresh continuable session with the reminder's stored audience.
- [x] 10.3 `CurrentSession`-delivery reminders re-enter the originating session via the gateway trusted-turn handler.
- [x] 10.4 Duplicate reminder execution prevented; no `ReceiveTimeout` that kills long LLM chains.

## 11. Interactive approvals and callback endpoint

- [x] 11.1 `MattermostApprovalPromptBuilder` and interactive-button approval prompts.
- [x] 11.2 Single-use opaque action token (32 bytes RNG, server-stored in `MattermostCallbackActionStore`, channel-bound, 12h TTL) minted per button option and consumed on the first callback.
- [x] 11.3 `/api/mattermost/actions` callback route registered only when the channel is enabled with a `CallbackUrl` configured.
- [x] 11.4 Action-token consumed atomically (fail-closed on unknown/expired/replayed token); payload `channel_id` must match the token's bound channel; ACL run on the resolved sender before mutating approval state; responses route by session identity into existing sessions only.
- [x] 11.5 Deterministic A/B/C/D text-reply approval fallback when interactive approvals are not configured.

## 12. Daemon wiring and configuration

- [x] 12.1 `MattermostChannelOptions` with `Enabled`, bot token (`SensitiveString`), server URL, callback URL, `AllowDirectMessages`, `MentionOnly`, allow-lists, `ChannelAudiences`.
- [x] 12.2 `MattermostChannelRegistrationExtensions` registering the channel, thread-history fetcher, reminder target resolver, reply/outbound clients, tools, and event handlers.
- [x] 12.3 Mattermost section added to `netclaw-config.v1.schema.json` with default values for `MentionOnly`/`MentionRequiredInDm`.
- [x] 12.4 Callback route registration wired into the daemon HTTP host.
- [x] 12.5 `CHANNEL_TYPE_MATTERMOST` added to `netclaw_messages.proto` (channel-type enum parity).

## 13. Conformance contract tests

- [x] 13.1 `MattermostAclContractTests` subclassing `AclPolicyContractTests`.
- [x] 13.2 `MattermostGatewayContractTests` subclassing `GatewayRoutingContractTests`.
- [x] 13.3 `MattermostSessionBindingContractTests` subclassing `SessionBindingContractTests`, including the thread-hydration contract.
- [x] 13.4 `RecordingMattermostReplyClient` and `TestMattermostGatewayDeps` test doubles.

## 14. Unit and integration tests

- [x] 14.1 Unit tests for ACL policy, routing policy, message chunking, attachment URL trust, and reminder target resolution.
- [x] 14.2 Unit tests for action-token mint/consume, replay rejection, mismatched-channel rejection, non-allowlisted-user rejection, gateway-not-yet-registered 503, and approval response routing (including the passivation-survival case).
- [x] 14.3 Offline tests for thread-history backfill (root-only bot dedup, deferred-hydration re-arm) via the contract suite.
- [x] 14.4 Proactive-send and conversation-actor tests covering the gateway hierarchy.
- [x] 14.5 Testcontainers integration tests added in `Netclaw.Channels.Mattermost.IntegrationTests` (compile-verified; gated on `NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1` so the suite self-skips in required CI and only runs on opt-in).

## 15. Skills, evals, and docs

- [x] 15.1 `netclaw-operations` system skill updated for Mattermost channel config and the `send_mattermost_message` tool; `metadata.version` bumped to 2.5.0.
- [x] 15.2 Reviewed the need for a dedicated eval case for `send_mattermost_message`; none added — channel proactive-post tools (`send_slack_message`, `send_discord_message`) have no eval cases, and the tool is reached via the standard `search_tools` discovery path already covered by the eval suite.
- [x] 15.3 Added `docs/integrations/mattermost-channel.md` operator runbook, including the callback action-token lifecycle (in-memory, single-use, invalidated on daemon restart).

## 16. Validation and quality gates

- [x] 16.1 `openspec validate add-mattermost-channel --type change` passes.
- [x] 16.2 `dotnet build` clean; full unit-test suite green (Actors 1805, Daemon 563, Configuration 312, Cli 699, Security 554, Search 46, MemoryRetrievalPoC 5; 131 of the Actors tests are Mattermost).
- [x] 16.3 `dotnet slopwatch analyze` reports 0 issues.
- [x] 16.4 `./scripts/Add-FileHeaders.ps1 -Verify` passes.
- [ ] 16.5 Run `./evals/run-evals.sh` to cover the `netclaw-operations` skill update — release-gate step; requires a live model provider and Docker, so it is left for CI / the release pipeline.
- [x] 16.6 Mattermost config-schema section added with `additionalProperties: false` and defaults; `Netclaw.Configuration.Tests` (schema-doctor coverage) pass.

## 17. Init wizard integration

- [x] 17.1 Add a Mattermost step (`MattermostStepViewModel` + `MattermostStepView`) to the `netclaw init` TUI wizard, mirroring the Discord step plus a self-hosted Server URL sub-step and an optional Callback URL sub-step.
- [x] 17.2 Wire Mattermost into the unified `ChannelPickerStepViewModel` as a third selectable channel; add the `WizardStepIds.Mattermost` constant.
- [x] 17.3 Write a `Mattermost` section to `netclaw.json` and the bot token to `secrets.json` via `WizardConfigBuilder` (`MattermostConfigSection`).
- [x] 17.4 Add `MattermostStepViewModelTests` and channel-picker/config-builder test coverage; `Netclaw.Cli.Tests` pass (724 total).
- [x] 17.5 Validate via the native TUI smoke harness. The `init-wizard` tape exercises the channel picker with Mattermost present and advances past it cleanly. The tape leaves the External Skills step skipped (its `IsApplicable` returns false on a smoke host with no Claude Code etc.), matching pre-PR behavior; an earlier accidental tape edit that assumed the step was always shown has been reverted.

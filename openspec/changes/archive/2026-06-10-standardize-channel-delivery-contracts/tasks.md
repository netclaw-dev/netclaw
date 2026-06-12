## 1. OpenSpec planning artifacts

- [x] 1.1 Confirm proposal, design, and spec delta define channels as output-capable delivery surfaces that may also produce input.
- [x] 1.2 Confirm reminders and webhooks are represented as trigger consumers of channel delivery targets, not channel registry participants.
- [x] 1.3 Confirm Mattermost actorization is represented as an adapter-specific lifecycle task, not the top-level change.
- [x] 1.4 Confirm invariants, capability matrix, and multi-window implementation guardrails remain consistent after review edits.
- [x] 1.5 Run `openspec validate standardize-channel-delivery-contracts --type change` and resolve all issues.

## 2. Channel descriptor and snapshot contracts

- [x] 2.1 Add a standard channel descriptor model for output-capable remote chat and local interactive channels.
- [x] 2.2 Add capability flags for receive, send, DM, threaded conversations, interactive approval, file ingress, file egress, proactive send, user lookup, destination lookup, runtime health, and supported output effects.
- [x] 2.3 Add a standard channel runtime snapshot model with enabled, health, connected, ready, principal identity, and activity metadata.
- [x] 2.4 Add a channel registry service that enumerates descriptor and snapshot providers for output-capable channels only.

## 3. Delivery target contracts

- [x] 3.1 Add `ChannelDeliveryTarget` with channel key, resolved destination, and optional thread/root target.
- [x] 3.2 Preserve channel-originated default delivery targets for Slack, Discord, Mattermost, and TUI input turns.
- [x] 3.3 Require trigger-originated turns to carry an explicit delivery target when external output is requested.
- [x] 3.4 Fail loudly when a trigger-originated turn attempts external output without a delivery target.

## 4. Existing channel coverage

- [x] 4.1 Register channel descriptors for Slack, Discord, Mattermost, and TUI or explicitly mark unsupported/not-configured output channels.
- [x] 4.2 Adapt Slack runtime health to the standard snapshot shape without changing Slack behavior.
- [x] 4.3 Adapt Discord runtime health to the standard snapshot shape without changing Discord behavior.
- [x] 4.4 Adapt Mattermost runtime health to the standard snapshot shape without actorizing it yet.
- [x] 4.5 Represent TUI as a local interactive channel and SignalR as daemon infrastructure, not as the same channel record.
- [x] 4.6 Keep first-slice descriptors limited to implemented delivery behavior, not aspirational roadmap capabilities.
- [x] 4.7 Implement Discord proactive DM output before advertising `DirectMessages` or `DirectMessage` address support on the Discord descriptor.
- [x] 4.8 Implement Discord `FileOutput` upload before advertising `FileEgress` or `FileAttachment` support on the Discord descriptor.
- [x] 4.9 Implement Mattermost `FileOutput` upload before advertising `FileEgress` or `FileAttachment` support on the Mattermost descriptor.

## 5. Trigger-source consumers

- [x] 5.1 Update reminder definitions to store or resolve explicit channel delivery targets when output is requested.
- [x] 5.2 Update webhook route definitions to store or resolve explicit channel delivery targets when output is requested.
- [x] 5.3 Ensure reminders and webhooks do not register channel descriptors or channel snapshot providers.

## 6. Descriptor-driven observability

- [x] 6.1 Change daemon runtime status to enumerate the channel registry instead of hard-coding individual channel adapters.
- [x] 6.2 Change daemon stats channel activity to enumerate descriptor-backed output channels.
- [x] 6.3 Keep trigger-source status separate from channel status when reminder or webhook operational state is reported.
- [x] 6.4 Preserve current status/stats output fields or provide explicit compatibility mapping.

## 7. Address resolution

- [x] 7.1 Add a standard channel address resolver contract for users and destinations.
- [x] 7.2 Support exact stable ID resolution before name search.
- [x] 7.3 Fail loudly with candidates for ambiguous display-name matches.
- [x] 7.4 Route resolution requests to the resolver registered for the selected channel descriptor.
- [x] 7.5 Wire Slack lookup to its channel-scoped resolver.
- [x] 7.6 Wire Discord lookup to its channel-scoped resolver where supported.
- [x] 7.7 Wire Mattermost lookup to its channel-scoped resolver.

## 8. LLM-facing channel tool standardization

- [x] 8.1 Define standard generic tool schemas and final tool names: `send_channel_message`, `lookup_channel_user`, and `lookup_channel_destination`, each with required first `channel_key` enum-constrained from enabled descriptors.
- [x] 8.2 Rename/map existing Slack tools to the standard tool names and intent schema.
- [x] 8.3 Rename/map existing Discord tools to the standard tool names and intent schema.
- [x] 8.4 Rename/map existing Mattermost tools to the standard tool names and intent schema.
- [x] 8.5 Update system skills, CLI/help text, and eval cases for renamed LLM-facing channel tools.
- [x] 8.6 Define the standardized DM send workflow as user lookup -> direct-message delivery target -> send-channel-message, gated by each channel descriptor's implemented DM output capability.
- [x] 8.7 Skipped: smaller-model channel selection evals require fake channel descriptors, fake address resolvers, and no-op send sinks that the current eval harness does not provide; deterministic daemon/unit coverage remains the feasible validation path for this slice.

## 9. Channel output effects follow-up

- [x] 9.1 Add a channel output renderer contract for semantic `SessionOutput` effects.
- [x] 9.2 Add contract tests for supported optional output effects, unsupported optional output effects, and unsupported required output effects.
- [x] 9.3 Wire processing-indicator output through channel capabilities so Discord typing indicators and future Slack/Mattermost/TUI equivalents share the same semantic output path.

## 10. Stateful channel lifecycle follow-up

- [x] 10.1 Add contract tests for not-ready ingress gating, runtime disconnect health, clean reconnect signaling, and handler de-duplication for stateful remote chat channels.
- [x] 10.2 Implement Mattermost lifecycle actorization only after the standard snapshot and lifecycle contract tests exist.
- [x] 10.3 Verify Slack and Discord satisfy the same lifecycle requirements or document explicit capability differences.

## 11. Validation and quality gates

- [x] 11.1 `dotnet test src/Netclaw.Actors.Tests/ --filter Channel`
- [x] 11.2 `dotnet test src/Netclaw.Daemon.Tests/`
- [x] 11.3 `dotnet slopwatch analyze`
- [x] 11.4 `./scripts/Add-FileHeaders.ps1 -Verify`

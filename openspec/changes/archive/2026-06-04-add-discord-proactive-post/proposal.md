## Why

The Discord channel can only *respond* to inbound messages — it has no tool to
*start* a conversation. Slack has `send_slack_message` for proactive posting,
which the agent uses for scheduled-reminder channel delivery
(`DeliveryKind.Channel`) and cross-channel outreach. `ReminderDelivery`
already maps `Transport = "discord"` to a `send_discord_message` tool name, so a
Discord-targeted channel reminder currently tells the LLM to call a tool that
does not exist. This closes that gap and brings Discord to parity with Slack for
agent-initiated posting (PRD-008 scheduling outcomes, PRD-009 unified-input
principle).

## What Changes

- New `send_discord_message` LLM tool (`[NetclawTool]`, `Grant = "builtin"`):
  posts a message to an allowed Discord channel and creates a thread off that
  message, so the bot's message becomes the thread root.
- New proactive-thread wiring: a `StartProactiveThread` / `ProactiveThreadAck`
  protocol routed gateway → conversation → session-binding actor, so user
  replies in the created thread route back to a live session.
- Conformance with `thread-history-backfill`: the proactively-created thread's
  bot-authored root is adopted as context on the first authorized reply via the
  binding actor's existing deferred re-armed hydration — no transcript seeding.
- **Scope: channel targets only.** DM (`user_id`) proactive posting is
  deliberately deferred — see Impact and design.md. Not a breaking change.

## Capabilities

### New Capabilities
<!-- None. This extends an existing channel capability. -->

### Modified Capabilities
- `netclaw-discord-socket`: adds a requirement for an agent-initiated
  proactive-post tool and proactive-thread actor wiring, including conformance
  with the `thread-history-backfill` deferred-hydration contract and an explicit
  channel-only scope boundary.

## Impact

- **New code**: `src/Netclaw.Channels.Discord/Tools/SendDiscordMessageTool.cs`,
  `IDiscordOutboundClient.cs`, `Transport/DiscordNetOutboundClient.cs`.
- **Modified code**: `DiscordIngressMessages.cs` (protocol records),
  `DiscordGatewayActor.cs`, `DiscordConversationActor.cs`,
  `DiscordSessionBindingActor.cs`, `DiscordChannel.cs` (expose gateway),
  `DiscordChannelRegistrationExtensions.cs` (DI).
- **Security**: the tool enforces `DiscordAclPolicy.IsAllowedChannel`; the
  conversation actor re-checks the channel ACL as defense-in-depth before
  wiring the session. No new bypass of ACL/ingress gating.
- **Operational**: agent guidance updated in the `netclaw-operations` system
  skill; a new tool-discovery eval case is added.
- **Known behavioral gap (tracked)**: the agent cannot proactively DM a Discord
  user. Discord DMs are flat (no thread root), so the `thread-history-backfill`
  amnesia fix cannot apply to DMs without a new mechanism. This is deferred and
  tracked by netclaw-dev/netclaw#1025; design.md records the rationale.

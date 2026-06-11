# Discord Channel Integration

This runbook describes how to configure and operate the Netclaw Discord channel.

## Overview

The Discord channel is designed for Slack-parity behavior with default-deny ACL
controls.

- Session identity: `{channelId}/{threadOrMessageId}`
- Inbound handling: ACL-gated before session dispatch
- Approval fallback: deterministic text fallback is supported
- Reminder support: `current_session` and Discord channel target resolution

## Current implementation status

Discord runtime policy, reminder routing, and init wizard support are in place,
but the default transport registrations are still placeholders.

- `UnconfiguredDiscordGatewayClient` fails loud on connect
- `UnconfiguredDiscordReplyClient` fails loud on outbound post

If `Discord.Enabled = true` and you do not register concrete transport clients,
daemon startup fails by design.

## Prerequisites

- A Discord bot token
- Discord snowflake IDs for channels and/or users you plan to allow
- Concrete implementations of `IDiscordGatewayClient` and
  `IDiscordReplyClient` registered in daemon DI

## Configuration

Put non-secret behavior in `~/.netclaw/config/netclaw.json`:

```json
{
  "Discord": {
    "Enabled": true,
    "DefaultChannelId": "129847561203948576",
    "AllowDirectMessages": false,
    "AllowedChannelIds": ["129847561203948576", "130111223344556677"],
    "AllowedUserIds": ["120001112223334445"],
    "ChannelAudiences": {
      "dm": "team",
      "129847561203948576": "team",
      "130111223344556677": "public"
    }
  }
}
```

Put secrets in `~/.netclaw/config/secrets.json`:

```json
{
  "Discord": {
    "BotToken": "your-discord-bot-token"
  }
}
```

Supported Discord settings:

- `Enabled` - toggles Discord channel startup
- `BotToken` - bot token used by Discord gateway/reply clients
- `DefaultChannelId` - optional primary channel allow entry
- `AllowDirectMessages` - defaults to `false` (secure by default)
- `AllowedChannelIds` - allow-list for non-DM channel traffic
- `AllowedUserIds` - optional user allow-list (empty means no user filter)
- `MentionOnly` - when `true` (default), the bot only responds to messages that
  mention it in non-DM channels; when `false`, the bot responds to all messages
  in allowed channels
- `MentionRequiredInDm` - when `true`, the bot requires a mention even in direct
  messages; defaults to `false` (DMs are always treated as addressed to the bot)
- `ChannelAudiences` - optional audience override map; keys are channel IDs or
  `dm`, values are `personal`, `team`, or `public`

## Init wizard behavior

`netclaw init` includes a Discord step that can:

- enable/disable Discord integration,
- collect bot token,
- collect allowed channel IDs,
- toggle direct messages,
- collect allowed user IDs.

When enabled, wizard output writes:

- a `Discord` section into `netclaw.json`, and
- `Discord.BotToken` into `secrets.json`.

## ACL policy model

Discord ACL evaluation follows fail-closed rules.

- Missing sender ID is denied.
- DMs are denied unless `AllowDirectMessages = true`.
- Non-DM traffic is denied unless channel is in `AllowedChannelIds` or matches
  `DefaultChannelId`.
- If `AllowedUserIds` is non-empty, sender must be listed.
- Audience is resolved in this order:
  1. explicit channel override from `ChannelAudiences[channelId]`
  2. DM override from `ChannelAudiences["dm"]`
  3. fallback (`team` for DM or explicit allow-list paths, otherwise `public`)

## Reminder targeting

`set_reminder` supports Discord in two delivery patterns:

- `delivery_kind = "current_session"` for session-thread replies
- `delivery_kind = "channel"` with `delivery_transport = "discord"`

Discord channel target resolution accepts canonical channel forms:

- `<#123...>` (channel mention)
- `channel:123...` (explicit channel ID)

Reminder channel delivery maps to the generic `send_channel_message` tool with
`channel_key = "discord"` and a resolved destination object. Discord proactive
DM output is not supported yet.

## Runtime behavior and troubleshooting

- If Discord is disabled, daemon starts normally and Discord remains inactive.
- If Discord is enabled with missing `BotToken`, startup fails fast.
- If Discord is enabled with placeholder clients, startup fails fast.
- Health reports `Disconnected` when gateway is not connected.
- Discord gateway lifecycle is actor-owned. Inbound messages and interactions
  are gated until the socket reaches READY, runtime disconnects surface through
  channel health, and stale/resumed sessions request a clean reconnect.

Common failure patterns:

- `Discord is enabled but Discord:BotToken is not configured.`
  - Add `Discord.BotToken` to `secrets.json`.
- `no Discord gateway client is configured`
  - Register a concrete `IDiscordGatewayClient`.
- `no Discord reply client is configured`
  - Register a concrete `IDiscordReplyClient`.

## Security note

Treat Discord bot tokens as secrets. If a token appears in logs, chat, shell
history, or commits, rotate it immediately and update `secrets.json`.

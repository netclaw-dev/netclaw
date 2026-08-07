# Slack Socket Mode Integration

This document describes how to configure and run the Netclaw Slack channel.

## Overview

The Slack channel runs inside `netclawd` using Slack Socket Mode.

- Inbound events: `app_mention`, `message`
- Session identity: `{channelId}/{threadTs}`
- Reply behavior: assistant replies are posted back to the same thread
- Processing indicator: native Slack thread status via `assistant.threads.setStatus`
- Thread behavior: mention starts thread, thread replies continue without mention

No public webhook endpoint is required.

## Prerequisites

Create a Slack app with:

- Socket Mode enabled
- Bot token (`xoxb-...`)
- App-level token (`xapp-...`)
- Bot scopes at minimum:
  - `app_mentions:read`
  - `channels:history`
  - `chat:write` for thread replies and native loading status
  - `groups:history` (if private channels are used)
  - `channels:read` and `groups:read` (if resolving by channel name)

Install the app to your workspace.

## Configuration

Put non-secret behavior in `~/.netclaw/config/netclaw.json`:

```json
{
  "Slack": {
    "Enabled": true,
    "SocketMode": true,
    "MentionOnly": true,
    "DefaultChannelName": "openclaw"
  }
}
```

Put tokens in `~/.netclaw/config/secrets.json`:

```json
{
  "Slack": {
    "BotToken": "xoxb-your-bot-token",
    "AppToken": "xapp-your-app-token"
  }
}
```

Supported Slack settings:

- `Enabled` - toggles Slack channel startup
- `SocketMode` - currently must be `true`
- `BotToken` - Slack bot token for Web API and auth
- `AppToken` - Slack app-level token for Socket Mode
- `DefaultChannelId` - optional hard filter to one channel ID
- `DefaultChannelName` - optional channel name resolved at startup
- `MentionOnly` - if `true`, plain `message` events require bot mention
- `MentionRequiredInThread` - if `true`, thread replies require a bot mention even when the thread already has an active session; defaults to `false`
- `AllowDirectMessages` - defaults to `false` (secure by default)
- `AllowedChannelIds` - defaults to empty array (`[]`), so no channels are allowed until explicitly configured
- `AllowedUserIds` - defaults to empty array (`[]`), meaning no user filter is applied beyond channel/DM policy

## Runtime behavior

- When Slack is disabled, daemon starts normally and Slack channel stays inactive.
- If Slack is enabled but required tokens are missing, daemon startup fails fast.
- While a session is processing, Netclaw sets the Slack thread status to
  `is thinking...` and clears it when the session returns idle.
- Socket Mode disconnects are handled by SlackNet reconnecting client behavior.
- Slack lifecycle is hosted-service owned rather than actor-owned. Ingress only
  forwards after the Slack gateway actor exists, while clean reconnect decisions
  remain delegated to SlackNet Socket Mode instead of a Netclaw lifecycle actor.

## Security note

Treat Slack tokens as secrets. If a token is ever posted in chat, shell history, logs,
or commits, rotate it immediately in Slack and update `secrets.json`.

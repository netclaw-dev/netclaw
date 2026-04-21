# Discord Channel

Discord support uses the same default-deny posture as Slack: inbound events are
ACL-checked before dispatch, and direct messages stay blocked unless explicitly
enabled.

## Init Wizard

`netclaw init` includes a Discord step that can:

- enable/disable Discord integration,
- collect the bot token,
- capture allowed channel IDs,
- toggle direct messages,
- capture allowed user IDs.

When Discord is enabled, generated config includes a `Discord` section and the
bot token is written to `secrets.json`.

## Reminder Targeting

`set_reminder` supports `delivery_transport = "discord"` for channel-kind
delivery. Target resolution accepts canonical Discord identifiers:

- `<@123...>` or `<@!123...>` user mentions,
- `@123...` user shorthand,
- raw snowflake user IDs,
- `dm:<channelId>` for explicit DM-channel IDs.

`delivery_kind = "current_session"` is also supported for Discord sessions and
routes through the Discord gateway path.

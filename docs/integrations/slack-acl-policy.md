# Slack ACL Policy

Slack ACL policy controls where Netclaw is allowed to engage and who can invoke it.

## Defaults

- Channel conversations require `@mention` to start a thread.
- Once a thread is started, follow-up thread replies are accepted without mention.
- Direct messages are allowed by default.

## Policy settings

Configure in `~/.netclaw/config/netclaw.json`:

```json
{
  "Slack": {
    "MentionOnly": true,
    "AllowDirectMessages": true,
    "AllowedChannelIds": ["C0AGM484P0Q"],
    "AllowedUserIds": ["U12345678", "U87654321"]
  }
}
```

- `MentionOnly`: requires mention to start non-DM sessions.
- `AllowDirectMessages`: enables DM conversations without mention.
- `AllowedChannelIds`: optional allow-list of channels.
- `AllowedUserIds`: optional allow-list of Slack users.

If `AllowedChannelIds` or `AllowedUserIds` is omitted, Netclaw does not enforce
that specific allow-list.

## Actor enforcement model

Slack policy is enforced by Slack channel actors:

- `SlackGatewayActor`: global event ingress and de-duplication
- `SlackConversationActor`: room/DM ACL and mention/thread policy
- `SlackThreadBindingActor`: thread-to-session binding (`{channelId}/{threadTs}`)

This keeps Slack-specific policy state inside actor boundaries rather than
in process-local adapter dictionaries.

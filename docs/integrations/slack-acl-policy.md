# Slack ACL Policy

Slack ACL policy controls where Netclaw is allowed to engage and who can invoke it.

## Defaults

- Channel conversations require `@mention` to start a thread.
- Once a thread is started, follow-up thread replies are accepted without mention
  (unless `MentionRequiredInThread` is `true`).
- Direct messages are denied by default.
- No channels are allowed by default (`AllowedChannelIds` is empty).

## Policy settings

Configure in `~/.netclaw/config/netclaw.json`:

```json
{
  "Slack": {
    "MentionOnly": true,
    "AllowDirectMessages": false,
    "AllowedChannelIds": ["C0AGM484P0Q"],
    "AllowedUserIds": ["U12345678", "U87654321"]
  }
}
```

- `MentionOnly`: requires mention to start non-DM sessions.
- `MentionRequiredInThread`: requires mention for thread replies even when the
  thread already has an active session. Defaults to `false`.
- `AllowDirectMessages`: enables DM conversations without mention.
- `AllowedChannelIds`: required allow-list of channels for non-DM traffic.
- `AllowedUserIds`: optional allow-list of Slack users.

If `AllowedChannelIds` is omitted, it defaults to `[]` and channel traffic is denied.

## Actor enforcement model

Slack policy is enforced by Slack channel actors:

- `SlackGatewayActor`: global event ingress and de-duplication
- `SlackConversationActor`: room/DM ACL and mention/thread policy
- `SlackThreadBindingActor`: thread-to-session binding (`{channelId}/{threadTs}`)

This keeps Slack-specific policy state inside actor boundaries rather than
in process-local adapter dictionaries.

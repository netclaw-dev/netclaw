## Why

When a bot is mentioned in a multi-party thread, the LLM cannot distinguish who said what — hydrated history uses raw platform IDs without role context, and live messages carry no speaker identity at all. Additionally, when `AllowedUserIds` restricts bot access, non-allowed users' messages in active threads are hard-blocked instead of being included as contextual input the authorized user intended the bot to see. This blocks practical use in shared channels (e.g., a Discord community where an operator tags the bot on a bug discussion thread and expects it to read everyone's messages). Related: [#738](https://github.com/Aaronontheweb/netclaw/issues/738).

## What Changes

- Add a third ACL outcome — **Observe** — alongside Allow and Deny. When `AllowedUserIds` is populated and the sender isn't on the list but a thread session already exists, the ACL returns Observe instead of Deny.
- Introduce an `IsObserver` flag on `ChannelInput`, `SlackThreadInbound`, and `DiscordThreadInbound` to propagate the observer classification from the ACL through the pipeline.
- **BREAKING**: Change the hydrated history speaker tag format from `<user: {SenderId}, {ReceivedAt}>` to `<speaker: {SenderId}, role={authorized|observer}, {ReceivedAt}>`. The merger gains an `allowedUserIds` parameter to determine roles.
- Add speaker attribution tags on live messages: `ChannelPipeline.MapToCommand` prefixes `<speaker: {SenderId}, role={role}>` to `SendUserMessage.Content`.
- Add a multi-speaker system prompt overlay (via `SessionPipelineOptions.PromptOverlay`) that explains authorized vs. observer roles when `AllowedUserIds` is non-empty.

## Capabilities

### New Capabilities

_None — this change modifies three existing capabilities._

### Modified Capabilities

- `thread-history-backfill`: Speaker tag format changes from `<user:>` to `<speaker:>` with role attribution. The merger accepts an allow-list to classify historical speakers as authorized or observer.
- `netclaw-acl`: Third ACL outcome (Observe) for non-allowed users in active thread sessions. Conversation actors forward observer messages instead of dropping them.
- `netclaw-input-adapters`: Live messages get speaker attribution tags in `SendUserMessage.Content`. `ChannelInput` gains `IsObserver`. System prompt overlay for multi-speaker instruction authority.

## Impact

- **ACL layer**: `IAclDecision`, `SlackAclDecision`, `DiscordAclDecision` gain `AclOutcome` enum. `EvaluateInbound()` signature adds `bool threadExists` parameter.
- **Conversation actors**: `SlackConversationActor`, `DiscordConversationActor` replace `!IsAllowed` guard with Deny/Observe branching.
- **Transport contracts**: `SlackThreadInbound`, `DiscordThreadInbound` gain `IsObserver` field.
- **Channel pipeline**: `ChannelInput` gains `IsObserver`. `MapToCommand` prefixes speaker tags on all messages.
- **Thread history**: `ThreadHistoryContentMerger` changes tag format and gains role classification.
- **Session prompt**: Binding actors set `PromptOverlay` when `AllowedUserIds` is non-empty.
- **No persistence schema changes** — `MessageSource` is `[ProtoIgnore]` and `ChannelInput` is ephemeral.
- **No configuration schema changes** — uses existing `AllowedUserIds` arrays.

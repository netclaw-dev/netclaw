## Why

When Netclaw is @-mentioned in an existing Slack thread (or future channels like
Teams/Discord), the session starts with no awareness of prior messages — including
images, files, and the full conversational context that prompted the mention. Users
expect the bot to understand what was already discussed. Without backfill, the bot's
first response lacks critical context, making the interaction feel broken.

This applies to any channel where a bot can be summoned mid-conversation: Slack
threads, Teams reply chains, Discord threads. The design must be channel-agnostic at
the session layer.

References: GitHub issue #575, PRD-001 (session identity), PRD-009 (input adapters).

## What Changes

- Channel adapters gain a **thread history fetch** responsibility: on first mention in
  an existing thread, the adapter fetches all prior messages (text + images + files)
  before session initialization completes.
- Backfilled messages flow through the **existing multimodal inbound pipeline**
  (`ChannelInput` → `SendUserMessage`) — same file download, content scanning, and
  media storage path used for live messages. No new content handling code.
- Backfilled history is injected as **read-only context** into the session before the
  first LLM turn. The LLM sees what was said but does not believe it participated.
- **No artificial caps** on backfill size. If the thread is long enough to exceed the
  context window, the existing compaction pipeline handles overflow.
- A new **channel-agnostic interface** (`IThreadHistoryProvider` or similar) defines the
  contract so the session layer never knows which channel fetched the history.
- Slack adapter implements the interface using `conversations.replies` API.

## Capabilities

### New Capabilities

- `thread-history-backfill`: Channel-agnostic thread history fetch, normalization, and
  session context injection. Covers the interface contract, backfill-as-context
  injection mechanism, and integration with existing compaction.

### Modified Capabilities

- `netclaw-input-adapters`: Input adapters gain an optional pre-session history fetch
  phase. The `ChannelInput` contract is unchanged, but adapter lifecycle expands to
  include backfill before first message delivery.
- `netclaw-slack-socket`: Slack adapter implements thread history fetch via
  `conversations.replies`, including multimodal content (images, files) processed
  through the existing download/scan pipeline.
- `netclaw-session`: Session initialization accepts optional pre-context history block.
  Compaction must handle backfilled content the same as live conversation history.

## Impact

- **Slack API**: New `conversations.replies` calls on session init. Subject to Slack
  rate limits (Tier 3, ~50 req/min). One call per new session, paginated for long
  threads.
- **Latency**: First response in a backfilled session will be slower — fetching and
  downloading thread history (especially images) adds startup time. Acceptable tradeoff
  since the alternative is a context-blind response.
- **Storage**: Backfilled media files written to session media directory, same as live
  messages. No new storage path.
- **Security**: Backfilled content goes through the same content scanning pipeline as
  live messages. ACL decisions apply per the existing channel security posture.
- **Code areas**: `SlackThreadBindingActor`, `ChannelPipeline`, `LlmSessionActor`
  context injection, `SessionPipelineOptions`.
- **Future channels**: Teams and Discord adapters will implement the same interface
  when added. Design must not assume Slack-specific behavior at the session layer.

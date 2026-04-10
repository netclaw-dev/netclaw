## Why

When Netclaw is @-mentioned in an existing Slack thread, the session starts with
no awareness of prior messages — including images, files, and the conversational
context that prompted the mention. Users expect the bot to understand what was
already discussed; without hydration, the first response is context-blind.

The same problem appears on **daemon restart**: Slack events that arrived while
Netclaw was down are replayed by Slack's socket reconnect logic, but the running
actor has no way to tell which events it already processed versus which are
genuinely new, and no way to backfill the gap without duplicating work.

The solution must be channel-agnostic at the abstraction layer so future
adapters (Teams, Discord) can implement the same behavior without touching the
session layer.

References: GitHub issue #575, PRD-001 (session identity), PRD-009 (input adapters).

## What Changes

- A new channel-agnostic contract, `IThreadHistoryFetcher`, returns
  `IReadOnlyList<ChannelInput>` for a given `SessionId`. Each threaded adapter
  implements it. The session layer never references it.
- `SlackThreadBindingActor` becomes a **persistent actor** that stores a
  per-thread **cursor** (the Slack `ts` of the most recently processed event).
  The cursor survives actor passivation and daemon restart.
- On every inbound Slack event the binding actor:
  1. **Drops stale events** whose `ts` is at or before the cursor, recording
     `stale_event` telemetry. This filters Slack's replay duplicates and any
     out-of-order delivery.
  2. On the first inbound per runtime (hydration), calls
     `IThreadHistoryFetcher`, then computes the **gap** of messages strictly
     between the cursor and the current event. The gap is merged **inline into
     the triggering `ChannelInput`** as a `[thread history — messages exchanged
     before this inbound event]` text block plus inline image `DataContent`.
  3. Runs each gap message through `IPromptInjectionDetector`. High-risk
     messages are dropped; if the detector itself fails, the message is dropped
     and the user is warned in-thread.
  4. Advances the cursor by persisting a `CursorAdvanced` event after a
     successful enqueue.
- Image attachments in gap messages are downloaded via `url_private_download`
  with bot-token auth and content-scanned through `IContentScanner`, reusing
  the live-message download path.
- No artificial cap on hydration size — long threads rely on the existing
  `SessionCompactionPipeline` for overflow handling.
- The session layer has **no special handling** for thread history. From its
  perspective the merged message is just a normal `SendUserMessage`.

## Capabilities

### New Capabilities

- `thread-history-backfill`: Channel-agnostic thread hydration contract. Defines
  the fetcher interface and the semantics of merging prior messages into the
  triggering inbound event.

### Modified Capabilities

- `netclaw-input-adapters`: Introduces the `IThreadHistoryFetcher` contract as
  an optional capability for threaded adapters. The `ChannelInput` contract is
  unchanged — no backfill flag is exposed.
- `netclaw-slack-socket`: `SlackThreadBindingActor` becomes a persistent actor
  with a per-thread cursor, implements stale-event filtering, and merges
  hydrated gap content into the triggering message.

## Impact

- **Slack API**: One paginated `conversations.replies` call per session per
  runtime. Tier 3 endpoint (~50 req/min) — not a bottleneck.
- **Persistence**: New persistent actor `SlackThreadBindingActor` with
  `PersistenceId = slack-thread-cursor-{sessionId}`. Journal entries are
  snapshotted/truncated every 10 persisted events to bound growth.
- **Latency**: First hydrated turn pays a one-time cost for fetch + per-message
  scanning + image downloads. Acceptable since the alternative is a
  context-blind response.
- **Security**: Gap messages flow through the existing content scanner and an
  additional prompt-injection detector gate. No new trust decisions at the
  session layer.
- **Code areas**: `SlackThreadBindingActor`, `SlackThreadHistoryFetcher`,
  `IThreadHistoryFetcher`, `SlackGatewayDependencies`,
  `SlackChannelRegistrationExtensions`.
- **Future channels**: Teams and Discord adapters implement
  `IThreadHistoryFetcher` and a cursor-persisting binding actor when added.

## Why

Agent-initiated channel posts (today: `send_slack_message`; in flight: a
Discord equivalent per [issue #953][i953]) create a brand-new channel
session whose transcript is born empty. When the user replies — even
hours later in the same thread — the loaded LLM context contains none
of the message that opened the conversation, so the agent answers
the reply with no record of what it said. Concrete production repro
in Slack session `D0AC6CKBK5K/1778533824.662179`: a `DeliveryKind.None`
reminder fired, the agent DM'd the user about a (hallucinated) PR
status, the user replied two hours later, and the agent's reply was
off-topic and confabulated.

A simpler diagnosis emerged during implementation. The
`thread-history-backfill` capability already hydrates prior thread
messages into adopted context when an authorized inbound creates an
executable turn — that machinery is in production and correct. The
gap is that each channel's history fetcher *also* filters out
bot-authored messages from the server-side history API. For Slack:
`SlackThreadHistoryFetcher.cs:154` drops any message with a
`bot_id`. That filter exists for a reason — it prevents
double-counting bot messages that the session itself produced and
already has in transcript — but it has one corner case where it
removes load-bearing content: when the bot's message is the
*thread root* and no in-session turn ever recorded it.

The cursor watermark already protects against the double-count
scenario: caught-up sessions don't refetch entries below their
watermark. The filter is therefore stricter than necessary. Removing
it surfaces exactly one new entry — the bot's posted message that
opened the proactive thread — exactly when the session needs it.

[i953]: https://github.com/netclaw-dev/netclaw/issues/953

## What Changes

- Modify `thread-history-backfill` to add a cross-channel requirement
  that bot-authored messages SHALL be included in history hydration.
  The cursor watermark continues to handle dedup; the inbound
  bot-message filter (loop prevention) is unchanged.
- Slack adapter: stop filtering bot messages in
  `SlackThreadHistoryFetcher`; derive a sender id from the user id
  when present, falling back to bot id; drop entries with neither.
- Discord adapter: stop filtering bot messages in
  `DiscordThreadHistoryFetcher` at all three filter sites (page
  iteration, thread root resolution, raw message fetch). Discord's
  message author exposes a single id; sender id derivation is the
  author id directly.
- Any future channel that ships a server-side history fetcher
  alongside a proactive-post tool (e.g., when a future tool joins
  Discord's existing `send_discord_message` family — issue #953)
  must apply the same rule.

Not a breaking change. Slack channel ACL gating already runs ahead of
the fetcher; the entries newly returned are subject to the same
adopted-context security model that handles third-party speakers
today.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `thread-history-backfill`: adds a new requirement that bot-authored
  messages are hydrated from server-side history. Existing requirements
  in this capability are unchanged.

## Impact

### Code

- `src/Netclaw.Channels.Slack/SlackThreadHistoryFetcher.cs` — remove
  the unconditional bot-message skip; derive sender id from user or
  bot identifier; drop entries with neither.
- `src/Netclaw.Actors.Tests/Channels/SlackThreadHistoryFetcherTests.cs` —
  replace `Filters_out_bot_messages` with assertions for the new
  behavior (bot entries included; sender id derivation; user-id
  preference when both fields present).
- `src/Netclaw.Channels.Discord/Transport/DiscordThreadHistoryFetcher.cs` —
  remove the three `IsBot` filter sites (page iteration, thread root
  resolution, raw message fetch); message author id continues to be
  the sender id.
- `src/Netclaw.Actors.Tests/Channels/DiscordThreadHistoryFetcherTests.cs` —
  add `Includes_bot_authored_messages_in_history_result` asserting the
  new inclusion behavior.

### APIs

- Internal only. No public surface change.

### Cross-channel implications

- Slack and Discord history fetchers are both updated in this change.
  The spec delta is in `thread-history-backfill` precisely so the
  requirement is discoverable by any future channel implementer.
- TUI / SignalR / webhook-side channels currently have no notion of
  server-side thread history backfill (they aren't threaded
  external-platform channels in the same sense), so this requirement is
  inert for them today.

### PRD lineage

- **PRD-008** (Scheduling and Periodic Tasks) — outcomes (3) "task
  results are posted to the originating or configured Slack channel"
  and the execution model that "creates a fresh session actor" assume
  that a user replying to a posted task result reaches a coherent
  session. This change closes the conformance gap that prevented that.
- **PRD-009** (Input Adapters and Unified Input) — the unified-input
  premise that "everything is just a message arriving at a session
  actor" is upheld by ensuring agent-initiated history entries hydrate
  the same way human-authored ones do.

### Security and operational impact

- **No new attack surface.** The newly-hydrated entries are bot-authored
  messages from the same channel and thread the session already has
  permission to read. Channel-level ACL gating runs ahead of the
  fetcher and is unchanged.
- **No privilege change.** Bot messages enter adopted context as quoted
  prior speakers' messages, the same path third-party human messages
  take. They are quoted, non-executable; only the current authorized
  message is executable.
- **No new persistence shape.** The fix uses existing adopted-context
  hydration; no new event types, no SessionState changes, no proto
  changes. Sessions persisted before this change recover identically.
- **Operational signal:** the existing structured log on history fetch
  (`Fetched {Count} thread history messages for {ChannelId}/{ThreadTs}`)
  already covers the new entries; no new telemetry required.

### MVP scope statement

**In scope for MVP:**
- `thread-history-backfill` spec delta requiring bot-message inclusion.
- Slack-side implementation: filter removed, sender id derivation,
  updated unit test.
- Discord-side implementation: three filter sites removed; new unit
  test asserting bot-authored entries flow through.

**Out of scope for MVP:**
- `send_discord_message` (a proactive-post tool for Discord, parallel
  to Slack's `send_slack_message`) — tracked under issue #953. The
  history-fetcher conformance for Discord is implemented in this
  change so the tool will land on a coherent contract when it ships.
- TUI / SignalR / webhook-side proactive-post tooling.
- **Agent reasoning lineage / explainability** ("why did the agent say
  this?"). The fix makes the new session see *what* it posted, not
  *why*. Reasoning lineage from the originating ephemeral session is a
  separable concern.
- Changing the role taxonomy in adopted-context rendering to mark the
  bot's own prior content as "assistant" explicitly. The existing
  rendering presents it with the bot's sender id; identity grounding
  in the system prompt is sufficient for the LLM to recognize its own
  content. Can be revisited if evals show the LLM behaves badly on the
  sender-id-only framing.

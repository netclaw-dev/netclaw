## Context

When Netclaw is @-mentioned in an existing Slack thread,
`SlackThreadBindingActor` creates a session pipeline and delivers only the
mention message. Without hydration the bot has zero awareness of prior
thread messages — text, images, files, or decisions that led to the mention.

A second, originally unrelated problem turns out to share the same solution:
on daemon restart, Slack's Socket Mode replay can deliver events the bot
already processed, and any actual gap in processing goes undetected. The
running actor has no durable marker for "the last event I handled."

The existing inbound pipeline
(`SlackThreadBindingActor.HandleInboundAsync` → `ChannelInput` →
`ChannelPipeline` → `SendUserMessage`) already handles multimodal content:
image download via Slack's file API, content scanning, and
`DataContent` assembly. This pipeline is the normalization boundary —
everything downstream of `ChannelInput` is channel-agnostic.

Session context overflow is handled by `SessionCompactionPipeline`
(tool-result clearing → extractive reduction → observation generation).
No new compaction logic is needed.

## Goals / Non-Goals

**Goals:**

- Hydrate full thread history (text + images) into the first inbound event a
  session sees, so the LLM has prior context before its first turn.
- Reuse the existing multimodal inbound pipeline — no new content-handling code.
- Durably track the last-processed event per thread so the bot can compute a
  gap after restart and filter Slack replay duplicates.
- Define a channel-agnostic contract (`IThreadHistoryFetcher`) so Teams/Discord
  adapters can plug in without touching the session layer.
- Gate backfilled text through prompt-injection detection so prior-thread
  content cannot smuggle instructions into the session.
- Handle arbitrarily long threads — no artificial caps; compaction handles
  overflow.

**Non-Goals:**

- Session-layer awareness of "backfill". The session treats the merged message
  as a normal `SendUserMessage`.
- A separate "context block" message type or a `IsBackfill` flag on
  `ChannelInput`. Hydration is a Slack-adapter concern only.
- Backfilling reactions, edits, or deleted messages.
- Non-image file types beyond what the live inbound pipeline already handles.
- Streaming/incremental hydration — fetch is synchronous before the triggering
  message is enqueued.

## Decisions

### 1. `SlackThreadBindingActor` becomes a persistent actor with a per-thread cursor

**Decision:** The binding actor extends `ReceivePersistentActor` with
`PersistenceId = "slack-thread-cursor-{sessionId}"` and stores a single piece
of state — `_cursorTs`, the Slack `ts` of the most recently successfully
processed inbound event. Updates are written via `PersistAsync(new
CursorAdvanced(ts))`.

**Why:** The cursor has to survive actor passivation (1-hour idle timeout) and
daemon restart, and it must be monotonic per thread. Putting it in the binding
actor rather than a central store keeps the ownership local and avoids a new
cross-cutting storage dependency. The earlier draft introduced an
`ISlackThreadCursorStore` abstraction (commit `6bd3a9c`) and was reverted
(commit `3a824e0`) once the persistent-actor approach was clearly simpler.

**Journal management:** After each persisted event, if `LastSequenceNr > 1` and
`LastSequenceNr % 10 == 0`, the actor calls `DeleteMessages(LastSequenceNr - 1)`
to keep the journal bounded. Only the latest event matters for recovery.

### 2. Hydration happens inline on the first inbound event per runtime

**Decision:** When `_threadHistoryHydrated` is `false` and an inbound arrives,
`BuildInputForInboundAsync` calls
`IThreadHistoryFetcher.FetchThreadHistoryAsync`, computes the gap
(messages strictly after the cursor and strictly before the current event),
runs each gap message through prompt-injection detection, and merges the
surviving messages into the triggering `ChannelInput` as an inline
`[thread history …]` text block plus any surviving image `DataContent`.

**Why:** Hydrating at session-init time would require synthesising a fake
trigger. Hydrating on the first real inbound means there's always a concrete
`ChannelInput` to merge into, and the resulting `SendUserMessage` looks
identical to any other first-turn message.

**Format of the merged text:**

```
[thread history — messages exchanged before this inbound event]

<user: U0123, 2026-04-09 10:15 UTC>
Has anyone looked at the dashboard latency spike?
[image attachments: 1]

<user: U0456, 2026-04-09 10:17 UTC>
I think it's the new query. See attached.

[end thread history]

<live text of the triggering mention>
```

Image bytes from the gap are appended as `DataContent` items on the merged
`ChannelInput` so the LLM receives them as vision content alongside the text.

### 3. No session-layer involvement

**Decision:** The session actor has no special handling for thread history.
`ChannelInput` carries no `IsBackfill` flag, `SendUserMessage` carries no
backfill flag, and `LlmSessionActor` has no backfill branch. An earlier draft
added these (commit `6bd3a9c`) and was reverted (commit `3a824e0`).

**Why:** Merging upstream of the pipeline reuses the pipeline's existing
`ChannelInput → SendUserMessage` transformation verbatim. The session cannot
distinguish a hydrated turn from a normal one, which means compaction, media
handling, and turn accounting all work without change.

### 4. Stale-event drop based on cursor

**Decision:** Before hydration or enqueue, the actor compares the inbound
event's `ts` against `_cursorTs`. If the inbound `ts` is at or before the
cursor, the event is dropped with telemetry tag `stale_event`.

**Why:** Slack Socket Mode replays events on reconnect and rarely delivers out
of order. Without a durable cursor the actor cannot distinguish a replay from
a genuine new event. The cursor lets us apply a simple rule: anything at or
before the high-water mark is already represented in the session's conversation
history and must not be re-delivered.

### 5. Prompt-injection gate on gap messages

**Decision:** Each gap message is scanned by `IPromptInjectionDetector`. If
`Risk == High`, the message is dropped with a structured warning log. If the
detector itself throws, the message is dropped and the actor posts
`BackfillDetectorWarning` to the thread so the user knows some prior context
was excluded.

**Why:** Prior thread content is untrusted user input that will be concatenated
into a prompt the LLM processes. Skipping injection detection on hydration
would create a bypass route for the same attack the live path already blocks.
Detector failures are treated as "unsafe by default" — we refuse to load the
message rather than pass it through unchecked.

### 6. `IThreadHistoryFetcher` is the only channel-agnostic contract

**Decision:** The interface lives in `Netclaw.Actors.Channels`:

```csharp
public interface IThreadHistoryFetcher
{
    Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}
```

`SlackThreadHistoryFetcher` implements it using
`ISlackApiClient.Conversations.Replies`, paginates through results, filters bot
messages, downloads images, and returns chronologically ordered `ChannelInput`
items. The binding actor owns all the merge, cursor, and injection logic —
the fetcher is pure retrieval.

**Why:** Keeping the fetcher side-effect-free makes it trivially testable and
keeps Slack-specific merge semantics out of the shared `Netclaw.Actors`
assembly.

### 7. No artificial caps on hydration size

**Decision:** Fetch the entire thread. If the merged `ChannelInput` exceeds
the session's context window, `SessionCompactionPipeline` handles it.

**Why:** Adding a cap would require choosing an arbitrary limit and building
cap-specific logic. Compaction already handles overflow for any content type.

## Risks / Trade-offs

**[First-response latency]** → Long threads with many images slow the first
hydrated turn. Acceptable — the alternative is a context-blind response.

**[Slack API rate limits]** → One paginated `conversations.replies` call per
runtime per thread. Tier 3 endpoint (~50 req/min). SlackNet handles 429 retry.

**[Cursor journal growth]** → Bounded by `DeleteMessages` every 10 persisted
events. Only the latest `CursorAdvanced` matters for recovery.

**[Bot message filtering]** → Must exclude the bot's own messages and other
bots from hydration. `SlackThreadHistoryFetcher` filters by `bot_id`.

**[Partial fetch failure]** → Per-message errors (download failures, scan
rejections) are logged and skipped without failing the whole hydration.
API-level errors return an empty list so the session still proceeds with
the triggering message alone. This is not a silent fallback — warnings are
logged and, for detector failures, surfaced to the user in-thread.

**[Stale-drop false positives]** → If Slack ever delivers a genuinely new
event with a `ts` equal to or before the cursor, it would be dropped. In
practice Slack `ts` is monotonic per thread, so this has not been observed.
The `stale_event` telemetry tag makes the condition visible if it ever fires.

## Open Questions

- **Operator visibility**: Should the operator console expose the per-thread
  cursor? Useful for debugging replay/hydration issues. **Leaning:** log-level
  indicator only for now.
- **Cross-channel hydration**: When Teams/Discord adapters land they will need
  their own `IThreadHistoryFetcher` implementation and a cursor-persisting
  binding actor. The contract is ready; the persistence pattern will be
  copy-adapted per adapter.

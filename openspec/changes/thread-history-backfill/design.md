## Context

When Netclaw is @-mentioned in an existing Slack thread, `SlackThreadBindingActor`
creates a session pipeline and delivers only the mention message. The bot has zero
awareness of prior thread messages — text, images, files, or decisions that led to the
mention.

The existing inbound pipeline (`SlackThreadBindingActor.HandleInboundAsync` → `ChannelInput`
→ `ChannelPipeline.MapToCommand` → `SendUserMessage`) already handles multimodal content:
image download via Slack's file API, content scanning, media storage to the session
directory, and `SerializableMediaReference` creation. This pipeline is the normalization
boundary — everything downstream of `ChannelInput` is channel-agnostic.

The Slack adapter uses `SlackNet`'s `ISlackApiClient`, which exposes
`Conversations.Replies()` for fetching thread history. The bot token already has the
required `channels:history` / `groups:history` scopes for Socket Mode.

Session context overflow is handled by `SessionCompactionPipeline` (tool result clearing →
extractive reduction → observation generation). No new compaction logic is needed.

## Goals / Non-Goals

**Goals:**

- Fetch full thread history (text + images + files) when the bot is first mentioned in
  an existing thread, before the first LLM turn.
- Reuse the existing multimodal inbound pipeline — no new content handling code.
- Inject backfilled messages as read-only context so the LLM understands the conversation
  but does not believe it participated.
- Define a channel-agnostic contract so Teams/Discord adapters can implement the same
  behavior without session-layer changes.
- Handle arbitrarily long threads — no artificial caps; compaction handles overflow.

**Non-Goals:**

- Backfilling messages that arrived while the daemon was down (gap recovery). SQLite
  persistence covers the bot's own turns; Slack-side gap fill is a separate concern.
- Streaming/incremental backfill — fetch is synchronous before first LLM turn.
- Backfilling reactions, edits, or deleted messages.
- Non-image file types beyond what the existing pipeline already handles (images only
  for now, matching current `HandleInboundAsync` behavior).

## Decisions

### 1. Backfill happens in `SlackThreadBindingActor.EnsureInitializedAsync`

**Decision:** Fetch thread history after pipeline creation but before unstashing the
first inbound message.

**Why:** This is the natural initialization boundary. The actor already stashes messages
while initializing. Adding a history fetch here means the backfill completes before any
live message is processed, and the existing stash/unstash flow handles ordering.

**Alternative considered:** Fetch in `SlackConversationActor` before creating the thread
actor. Rejected — the conversation actor is a routing layer and shouldn't own I/O-heavy
work. The thread binding actor already has the `HttpClient`, bot token, and content
scanner references needed for file downloads.

### 2. Backfilled messages flow through the existing `ChannelInput` pipeline

**Decision:** Convert each historical Slack message into a `ChannelInput` and write it
to the input queue, exactly as live messages are processed. The `ChannelPipeline` handles
`ChannelInput` → `SendUserMessage` transformation, media storage, and delivery to the
session actor.

**Why:** This reuses all existing multimodal handling — image download, content scanning,
MIME type mapping, media directory storage, `SerializableMediaReference` creation. Zero
new content handling code.

**Alternative considered:** Inject as a text-only `PromptOverlay` via
`SessionPipelineOptions.PromptOverlay`. Rejected — this is a single string, not
multimodal content. It cannot carry images, and it would require re-implementing media
handling outside the pipeline.

**Alternative considered:** Inject as `SessionState.AddSystemNudge()`. Rejected — nudges
are text-only system messages. Same multimodal limitation.

### 3. Backfilled messages are marked as context, not conversation turns

**Decision:** `ChannelInput` gains an optional `IsBackfill` flag (default `false`).
`ChannelPipeline.MapToCommand` propagates this to `SendUserMessage`. The session actor
uses this flag to inject backfilled messages as a read-only context block rather than
as conversation turns the LLM would try to "continue."

**Why:** If backfilled messages appear as normal user turns, the LLM will try to respond
to each one individually or believe it already participated. The context block framing
("The following messages were exchanged before you were mentioned") sets the right
expectation.

**Format in session history:** Backfilled messages are assembled into a single system-role
context block with structure:

```
[thread history — messages exchanged before you were mentioned]

<user: @alice, 2026-04-09 10:15 UTC>
Has anyone looked at the dashboard latency spike?
[image: screenshot.png]

<user: @bob, 2026-04-09 10:17 UTC>
I think it's the new query. See attached.
[image: grafana-panel.png]

[end thread history]
```

Images are included as inline `DataContent` blocks in the same message, so the LLM
receives them as vision content alongside the text.

### 4. Channel-agnostic contract via `IThreadHistoryFetcher`

**Decision:** Define an interface in `Netclaw.Actors.Channels`:

```csharp
public interface IThreadHistoryFetcher
{
    Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}
```

Each channel adapter implements this. The `SlackThreadBindingActor` uses a Slack-specific
implementation that calls `conversations.replies` and processes each message through the
existing file download/scan pipeline.

**Why:** The session layer never references Slack types. When Teams/Discord adapters are
added, they implement the same interface. The contract is `IReadOnlyList<ChannelInput>` —
the same type the pipeline already consumes.

**Alternative considered:** Put the interface on the session layer and have the session
actor call back to the channel. Rejected — this inverts the dependency (session → channel)
and complicates the actor hierarchy. The channel adapter already has the transport
credentials and HTTP client needed for history fetch.

### 5. Slack implementation uses `ISlackApiClient.Conversations.Replies`

**Decision:** Use `SlackNet`'s `Conversations.Replies()` method with the thread's
`channelId` and `threadTs`. Paginate through all results (Slack returns max 1000 per
page). Filter out bot's own messages (by bot ID) and messages with no usable content.

**Why:** `SlackNet` is already a dependency. `conversations.replies` returns all messages
in a thread, including file metadata with `url_private_download` — the same field used
for live message file downloads.

**Rate limits:** Tier 3 endpoint (~50 req/min). One paginated call per new session. For
threads with >1000 messages, multiple pages are needed, but this is rare and the rate
limit is generous for session-init-time calls.

### 6. No artificial caps on backfill size

**Decision:** Fetch the entire thread history. If it exceeds the context window, the
existing `SessionCompactionPipeline` compresses it.

**Why:** Adding a separate cap would require choosing an arbitrary limit and building
cap-specific logic. The compaction pipeline already handles overflow for any content type.
Simpler to let it do its job.

**Trade-off:** Very long threads (hundreds of messages with images) will increase first-
response latency due to download time. This is acceptable — the alternative is a
context-blind response.

### 7. Detect "first mention" via session recovery state

**Decision:** Backfill only runs when the session is newly created (no prior persisted
state). If the session actor recovers from SQLite with existing history, skip backfill —
the bot already has its own context.

**Why:** On daemon restart, the session recovers from persistence. Backfilling would
duplicate content the bot already processed. The check is simple: if
`EnsureInitializedAsync` is creating a new session (not recovering), fetch history.

**Implementation:** `SessionPipeline.CreateAsync` already returns a `MaterializedSession`.
The session manager can signal whether this is a new or recovered session. Alternatively,
the thread binding actor can track whether it has ever initialized (persisted flag or
first-message detection).

## Risks / Trade-offs

**[First-response latency]** → Long threads with many images will slow the first
response. Mitigation: fetch text messages first, start image downloads concurrently.
The LLM call can proceed once text context is available; images can be streamed in if
needed in a future optimization. For MVP, sequential fetch is acceptable.

**[Slack API rate limits]** → `conversations.replies` is Tier 3 (~50 req/min). With one
call per new session, this is unlikely to be a bottleneck. Mitigation: standard retry
with exponential backoff on 429 responses. SlackNet handles this automatically.

**[Large media storage]** → Backfilled images are stored in the session media directory.
A thread with many images could consume significant disk space. Mitigation: same as live
messages — session cleanup/TTL policies apply uniformly. No new storage concern.

**[Bot message filtering]** → Must exclude the bot's own messages from backfill to avoid
circular context. Mitigation: filter by bot ID during fetch. Also exclude other bot
messages (integrations, webhooks) to reduce noise.

**[Partial fetch failure]** → If image download fails for one message, the rest of the
backfill should still proceed. Mitigation: per-message error handling (skip failed
downloads, log warning), same pattern as `HandleInboundAsync` today.

**[Thread not found / permission denied]** → The bot may lack access to the channel
history. Mitigation: if `conversations.replies` returns an error, log a warning and
proceed without backfill. The session still works — just without prior context. This is
not a silent fallback; the warning is logged and visible in operator diagnostics.

## Open Questions

- **Message ordering in context block:** Should backfilled messages appear in
  chronological order (oldest first) or reverse chronological (most recent first)?
  Chronological is more natural for reading, but most-recent-first may be better for
  compaction (recent context is preserved, older context is summarized).
  **Leaning:** Chronological — matches how humans read threads.

- **Backfill indicator in operator UI:** Should the operator console show when a session
  was initialized with backfilled context? Useful for debugging but not critical for MVP.
  **Leaning:** Log-level indicator only for now.

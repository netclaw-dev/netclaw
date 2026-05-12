## Context

A reminder fires inside an ephemeral session (e.g.,
`reminder/llama-cpp-mtp-pr-watch/<ts>`). The session's LLM calls
`send_slack_message`, which posts a new top-level Slack thread via
`PostNewThreadAsync` and asks the gateway to spawn a
`SlackThreadBindingActor` for the new `{channelId}/{threadTs}` session
id. The binding actor calls `EnsureInitializedAsync` and acks; the
originating tool returns success; the ephemeral reminder session
terminates. The new Slack thread session is alive but has no
transcript content of its own — the posted message was written only
into Slack, never into any in-session output pipeline.

When the user replies in that thread, the binding actor processes the
inbound. The existing `thread-history-backfill` machinery runs — it
fetches prior thread messages from Slack via `conversations.replies`
and merges them into adopted context — but `SlackThreadHistoryFetcher`
at line 154 unconditionally drops any message with a `bot_id`. So the
fetched-history result is empty, the adopted-context window is empty,
and the LLM responds to the user with only the user's message in
context plus whatever memory adoption surfaces. The result is the
amnesia and confabulation observed in the repro.

The exploration considered several mechanisms that would have
introduced new protocol fields, new transcript-seeding events, and
new actor states. All were rejected because the existing
adopted-context machinery already does the job — the only thing
missing is for the history fetcher to actually return the bot's
posted message rather than filter it out.

## Goals / Non-Goals

**Goals:**

- Smallest possible code change that closes the amnesia bug for the
  observed repro and analogous scenarios.
- Specify the rule cross-channel so Discord (issue #953) and any
  future channel applying server-side history fetching land on the
  same behavior.
- Preserve every other invariant in the existing adopted-context and
  watermark machinery.

**Non-Goals:**

- Recovery of the producing ephemeral session's reasoning. The fix
  makes the new session see *what* it posted, not *why*.
- A new bootstrap protocol field on `StartProactiveThread` (considered
  during exploration; superseded by this simpler approach).
- A new persisted event type for bootstrap seeding (considered;
  superseded).
- Output pipeline dispatch suppression for bootstrap seeds (no longer
  needed; the fix never enters the output pipeline).
- Marking the bot's adopted-context entry with an explicit
  "assistant" role tag distinct from third-party speakers. The
  adopted-context renderer presents the entry with the bot's sender
  id; system-prompt identity grounding lets the LLM recognize its own
  content. Can be revisited later.

## Decisions

### Decision 1: Fix point is the history fetcher's bot-message filter

The single point of correction is the unconditional bot-message skip
in each channel's history fetcher. Removing it surfaces the bot's
prior posted message through the existing hydration path, and from
there the existing adopted-context merge layer treats it as a quoted
prior speaker.

**Alternatives considered:**

| Option | Why rejected |
|---|---|
| Add `Message` payload to `StartProactiveThread`; seed transcript at bootstrap time | Requires new protocol field, new persisted event, new SessionState handling, new subscriber dispatch suppression. ~200+ LOC vs. ~15 LOC for this approach. |
| Capture the bot-message echo via Slack Events API | Echoes are filtered as loop-prevention; flipping the filter breaks the loop guard. Even with selective filtering, dedup against the output pipeline is fiddly and pure latency loss. |
| Pre-allocate sessionId, seed first, post after | Requires a local-id ↔ remote-id mapping layer for every channel. Significant new infra. |
| Restructure `send_slack_message` as session-spawning runtime primitive | Architectural rewrite of proactive posting. Heaviest variant considered. |

**Rationale:** the bug is structurally "history fetch filters out the
one bot message that's load-bearing." Fix it where the filter is.

### Decision 2: Cursor watermark remains the dedup primitive

Removing the bot-message filter could theoretically reintroduce
double-counting: a session that already produced bot output and
recorded it in transcript could refetch the same bot output from
Slack's server-side history.

The existing `thread-history-backfill` capability already has the
answer: the watermark. From the existing spec —

> For a new authorized inbound with ordering key Y, the adapter SHALL
> hydrate messages whose ordering key is strictly greater than the
> watermark and strictly less than Y.

A bot message the session itself produced is below the watermark by
the time the next inbound runs (the watermark advanced when the
session's own turn completed). The fetcher would never include it.

The only bot messages above the watermark are those the session
itself never produced — which, in practice, means the opening
message of a proactively-posted thread, because that's the one case
where a bot message exists in Slack history without any in-session
turn having recorded it.

So the watermark + relaxed fetcher together do the right thing
without any new dedup logic.

### Decision 3: Sender id derivation prefers user id, falls back to bot id

Slack bot posts can carry both a `user` (the bot's user identity in
the workspace) and a `bot_id` (the bot integration id). The fetcher
derives a sender id from these:

1. If `user` is present and non-empty → use it (matches the agent's
   own workspace user id when the message is from our bot).
2. Else if `bot_id` is present → use that.
3. Else → drop the entry (no sender; can't enter adopted-context).

The user-id-first preference matters because the agent's
system-prompt identity grounding refers to its workspace user id. The
LLM recognizes "I (this workspace user) said X earlier" when the
adopted-context entry's author attribute matches. If only the bot id
were used, the LLM would see an unfamiliar identifier and might not
recognize it as itself.

### Decision 4: Inbound bot-message filter is untouched

`SlackConversationActor.cs:50` (`IsBotMessage → drop`) is unchanged.
That filter operates on live inbound events from the Events API and
exists for loop prevention: without it, the bot would receive its own
`chat.postMessage` echoes as inbound events and start a new turn on
them. This change does not require any modification to that path; the
two paths (live inbound and server-side history fetch) are
independent.

The spec delta makes this explicit so future readers don't conflate
the two filters.

### Decision 5: Cross-channel conformance lives in `thread-history-backfill`

The new requirement is added to the existing `thread-history-backfill`
capability rather than creating a new spec. Reasons:

- The new requirement is structurally a refinement of how
  `thread-history-backfill` hydrates — same machinery, different
  filtering rule.
- Discoverable in the same place as the watermark requirement that
  governs dedup, which is the load-bearing argument for why the
  filter can be safely removed.
- Avoids a new top-level spec whose surface area would be a single
  ADDED requirement.

Discord and any future channel that ships a server-side history
fetcher must satisfy the same requirement. There is no per-channel
conformance test base today; we add one if/when a second channel
implements proactive posts.

## Risks / Trade-offs

| Risk | Mitigation |
|---|---|
| Bot messages from *other* bots in the same channel now leak into adopted context | Acceptable — they're channel-level context the agent has permission to see, presented as third-party speakers in the existing adopted-context model. If a deployment wants to exclude specific bots, an ACL filter can be layered separately. |
| LLM doesn't recognize its own bot sender id as itself | Sender id is the workspace user id (via the user-id-first derivation). System-prompt identity grounding already covers "you are user U…". If evals show otherwise, we can add explicit "assistant-role" tagging in the adopted-context renderer for the agent's own sender id. |
| Cursor watermark not always set correctly at first inbound | Existing `thread-history-backfill` capability already covers crash-recovery semantics; no new code path. |
| Discord ships without applying the requirement | Issue #953 references this spec; reviewer must confirm Discord's history fetcher honors it. Conformance test base can be added when N=2 channels exist. |
| Sender-id-only framing isn't enough for the LLM to disambiguate its own content from other speakers' | Eval suite case (see Migration Plan) catches this. Escalation path: explicit role marking in the renderer. |

## Actor boundaries and persistence implications

- **No actor topology changes.** The fix lives entirely inside
  `SlackThreadHistoryFetcher`, which is invoked by the existing
  hydration path.
- **No new persisted events.** The bot's posted message enters the
  session via adopted-context, which is already persisted as
  `AdoptedContextRecorded` per the existing
  `thread-history-backfill` spec.
- **No watermark changes.** The watermark is the dedup primitive and
  is unchanged.
- **No subscriber dispatch changes.** The fix never enters the
  session output pipeline.

## Failure modes and recovery behavior

| Failure | Visible effect | Recovery |
|---|---|---|
| Slack `conversations.replies` returns no entries | Fetcher returns empty list (current behavior); no hydration | Existing fallback path; LLM responds with memory adoption only. Same as today, no regression. |
| Slack `conversations.replies` rate-limited or errors | Fetcher catches `SlackException`, returns empty list | Existing behavior; logged warning. No regression. |
| Bot's message exists in Slack history but the agent's workspace user id has changed | Adopted-context entry has the *old* sender id; LLM may not recognize it as itself | Mitigation: identity grounding in the system prompt mentions the current and recent prior user ids; or evals catch the case and we add explicit role tagging. |
| Watermark mis-set leading to bot output being refetched | Adopted context includes duplicate of content the session already has in transcript | Existing watermark machinery enforces correctness; this change does not introduce new watermark logic. |

## Migration Plan

This is a forward-only change. No data migration. No new persistence
shape. Sessions persisted before this change recover identically.

**Rollback strategy:** revert the two-line change in
`SlackThreadHistoryFetcher.cs` (restore the unconditional bot-message
skip; drop the senderId parameter on `ConvertMessageAsync`). The
updated unit test stays as a regression fixture even if reverted.

**Order of merge:**

1. Spec delta + proposal/design/tasks (this change).
2. Slack history fetcher implementation (already in this branch).
3. Optional: eval regression case for the proactive-post amnesia
   scenario.
4. Discord history fetcher conformance (deferred to issue #953).

## Open Questions

1. **Should the renderer mark the agent's own adopted-context entries
   with an explicit "self" / "assistant" attribute?** Today the
   renderer formats every adopted entry with `author=<senderId>` and
   relies on identity grounding for self-recognition. If evals show
   the LLM behaves badly on the sender-id-only framing — e.g., treats
   its own prior content as a third party's claim it must defend or
   refute — we'd add a per-entry tag. Defer pending eval signal.

2. **Conformance test base when N=2 channels.** Today only Slack has a
   server-side history fetcher with a bot-message filter to remove.
   When Discord ships (issue #953), it'll need an analogous change in
   its own fetcher. At that point a small shared test fixture (e.g.,
   `IThreadHistoryFetcherConformance`) makes sense. Premature with
   N=1.

3. **Other bots in shared channels.** This change incidentally
   surfaces messages from *other* bots in the same channel as adopted
   context. For most deployments this is desirable (channel context
   the agent has permission to see). If a deployment wants to exclude
   specific bots, a layered ACL filter can be added separately; out of
   scope for this change.

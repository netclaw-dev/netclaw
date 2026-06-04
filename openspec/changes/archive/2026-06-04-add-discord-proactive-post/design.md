## Context

Slack supports agent-initiated proactive posting via `send_slack_message`
(`src/Netclaw.Channels.Slack/Tools/SendSlackMessageTool.cs`). It posts a message,
then sends `StartProactiveThread` to the gateway actor so the new thread is wired
into the actor hierarchy and user replies route to a live session. Discord has
no equivalent: no `Tools/` directory, no `[NetclawTool]`.

The conformance work for proactive threads already landed for both channels.
`DiscordSessionBindingActor` and `DiscordThreadHistoryFetcher` were updated by
the 2026-05-16 `restore-proactive-thread-hydration-conformance` change so the
deferred re-armed hydration path works on Discord — but it could not be
exercised because no Discord tool creates proactive threads. This change adds
that tool.

Constraints:
- Discord session identity is `{channelId}/{threadOrMessageId}`. A flat
  guild-channel message has the message id as `threadOrMessageId`, so each
  message is its own session and replies (separate messages) never route back.
  Only a **thread** gives a stable session key shared by all replies.
- Discord threads created from a message have `thread.Id == message.Id`.
  `DiscordThreadHistoryFetcher` already detects the thread root via
  `MessageId == threadChannelId` and includes bot-authored roots.
- The gateway actor ref only exists after `DiscordChannel.StartAsync`, so the
  tool must resolve it lazily.

## Goals / Non-Goals

**Goals:**
- A `send_discord_message` builtin tool that posts to an allowed channel and
  creates a thread, wired so replies route to a live session.
- Parity with `send_slack_message` structure (protocol records, actor routing,
  DI registration, ACL enforcement) so the two channels stay symmetric.
- Conformance with `thread-history-backfill`: the bot root is recovered through
  the existing deferred-hydration path, not by seeding the transcript.

**Non-Goals:**
- DM (`user_id`) proactive posting — deliberately deferred (see Decisions).
- A `lookup_discord_user` tool (Slack's `LookupSlackUserTool` analogue).
- Any change to the inbound message path or live reply path.
- Any change to `DiscordChannelOptions` or the config schema.

## Decisions

### Decision: Create a thread off the posted message, not a flat message

For replies to route back to one session, the proactive post needs a stable
session key. A flat guild-channel message yields `{channelId}/{messageId}` and
every reply is a distinct message → distinct session. Creating a public thread
off the posted message makes the posted message the thread root; replies in the
thread resolve to `{parentChannelId}/{threadId}` (a stable key). This also
satisfies `thread-history-backfill`: the bot root is detectable
(`MessageId == threadChannelId`) and hydratable.

*Alternative considered:* post a flat message and rely on Discord reply
references. Rejected — reply references do not produce a stable session key and
the routing policy (`DiscordConversationActor`) keys on `threadOrMessageId`.

### Decision: Mirror Slack's deferred hydration; do not seed the transcript

The bot's posted message is recovered on the first authorized reply by the
binding actor's existing `_hydrationPending` → `ApplyDeferredHydrationAsync`
path, which fetches the thread root from Discord server-side history and adopts
it into the authorized turn's context window. The tool only posts + wires; it
never writes to the session transcript.

*Alternative considered:* seed the new session's transcript with the posted
message as a first assistant turn (as issue #953's text suggests). Rejected —
this is a new mechanism absent from Slack, would touch the session pipeline, and
duplicates the already-landed `thread-history-backfill` contract. Mirroring
Slack keeps the two channels symmetric and the conformance surface single.

### Decision: New `IDiscordOutboundClient`, separate from `IDiscordReplyClient`

`IDiscordReplyClient` serves the inbound reply path and creates threads only off
*pre-existing* anchor messages. Proactive posting is post-then-thread on a
freshly created message. A dedicated `IDiscordOutboundClient` (mirroring Slack's
`ISlackOutboundClient` vs `ISlackReplyClient` split) keeps proactive concerns
isolated and testable with a fake.

### Decision: Channel-only scope — DM proactive posting deferred

The tool exposes a channel target only; no `user_id`, no DM channel opening.
Discord DMs are a flat conversation with no distinct thread root. The
`thread-history-backfill` capability explicitly documents this: scenarios
"Discord DM has no thread root, so no bot content hydrates from history" and
"Discord DM never defers and never re-arms". A DM proactive post would route
replies correctly (DM session key is the stable DM channel id) but the bot's
posted message could not be recovered as context — the proactive-post amnesia
bug would remain open on DMs.

Rather than ship a tool whose DM path silently has weaker context guarantees
than its channel path, DM support is deferred. This is a deliberate behavioral
gap, recorded here and tracked by a dedicated follow-up GitHub issue
(netclaw-dev/netclaw#1025, "Add DM (`user_id`) proactive-post support to
`send_discord_message`"). Closing it will require either a new context-recovery
mechanism for flat conversations or an accepted, explicitly-documented weaker
guarantee for DMs.

*Alternative considered:* ship DM support now with the documented amnesia
limitation. Rejected for this change to keep the tool's guarantees uniform; the
follow-up issue carries the decision.

## Risks / Trade-offs

- **[Thread creation race]** Two near-simultaneous posts could both try to
  create a thread on the same anchor → Discord returns `400`. Mitigation: reuse
  `DiscordNetReplyClient`'s existing `HttpException BadRequest` →
  `FindExistingThreadAsync` fallback in the new outbound client.
- **[Post succeeds, wiring fails]** The message is posted before
  `StartProactiveThread` is acknowledged; if wiring times out the post is
  already public. Mitigation: the tool reports "posted but session pipeline did
  not initialize" with the thread id, mirroring Slack — never an unqualified
  success and never a false failure.
- **[Non-text channel target]** `CreateThreadAsync` requires an `ITextChannel`.
  Mitigation: the outbound client fails loud with a clear message; the tool
  surfaces it as an error result.
- **[DM gap]** The deferred behavioral gap is the main trade-off; mitigated by
  explicit documentation here, in the spec delta, and a tracking issue.

## Migration Plan

Additive only. No schema or persistence change. No rollback complexity — the
tool is inert unless the Discord channel is enabled and the LLM invokes it. The
`netclaw-operations` system skill and an eval case are updated in the same
change per the repository skill-sync and eval rules.

## Open Questions

None. Scope, conformance approach, and the DM deferral were confirmed during
planning.

## Context

A reminder fires inside an ephemeral session (e.g.,
`reminder/llama-cpp-mtp-pr-watch/<ts>`). The session's LLM calls
`send_slack_message`, which posts a new top-level Slack thread via
`PostNewThreadAsync` and asks the gateway to spawn a
`SlackThreadBindingActor` for the new `{channelId}/{threadTs}`
session id. Today, the binding actor calls `EnsureInitializedAsync`
and acks; the originating tool returns success. The ephemeral
reminder session terminates. The new Slack thread session is alive,
but its transcript is empty — the posted message was never written
into it.

When a user replies in that thread (sometimes hours later), the
binding actor loads context for the new turn. The seed is missing,
so the LLM context contains only adopted memories and the user's
reply. The agent confabulates a response from fuzzy memory recall.
Concrete repro: session `D0AC6CKBK5K/1778533824.662179`.

The fix is to seed the new session's transcript with the posted
payload at bootstrap time. The design question is how to do that
without (a) treating the seed as an LLM-produced turn (which would
fire subscriber events, double-post to Slack, etc.), (b) violating
existing invariants in adjacent code paths (notably the `BotId`
filter in `SlackThreadHistoryFetcher`), or (c) introducing a new
bug class where the bot reacts to its own seeded message.

## Goals / Non-Goals

**Goals:**

- Channel-agnostic contract: the same rule applies to Slack today,
  Discord (issue #953), and any future channel adapter that ships a
  proactive-post tool.
- Smallest implementation surface that satisfies the contract.
- Reuse existing actor infrastructure for ordering guarantees rather
  than building new buffering or synchronization machinery.
- Conformance must be testable in isolation with `Akka.TestKit` and
  a fake outbound client (no real Slack network required for CI).
- Eval-suite coverage for the LLM behavioral aspect (since the change
  touches context assembly per `CLAUDE.md`'s eval-trigger list).

**Non-Goals:**

- Recovery of the ephemeral producing session's *reasoning* (web_fetch
  results, decision chain, etc.). The seed makes the new session see
  *what* it posted, not *why*. Reasoning lineage / explainability is
  a separable concern.
- Backfilling history from Slack on inbound. The existing
  `thread-history-backfill` mechanism with its `BotId` filter is
  correct for all non-proactive cases and remains untouched.
- Pre-allocating session ids before posting. We accept that the
  platform mints the conversation id; the protocol carries the
  payload after the post returns it.
- Buffering inbound user messages awaiting bootstrap. The actor
  infrastructure's natural FIFO ordering at the channel boundary
  removes the need (see Decision 5).
- Restructuring proactive posting as a session-spawning runtime
  primitive (the heaviest variant considered during exploration).
  Tool-based remains the right abstraction.

## Decisions

### Decision 1: Mechanism — push the payload through the bootstrap protocol message

The bootstrap protocol message that today carries
`{channelId, threadTs, sessionId}` gains a new field for the **full
posted payload** (text, attachments, blocks, anything that posted
under the platform-assigned message id) and the **platform message
id** itself.

**Alternatives considered:**

| Option | Why rejected |
|---|---|
| Capture the bot-message echo via Slack Events API | Echoes are filtered as loop-prevention; flipping the filter breaks the loop guard. Even with selective filtering, dedup against the output pipeline is fiddly and pure latency loss. |
| Stop filtering bot messages in `SlackThreadHistoryFetcher` | Filter is correct for non-proactive cases (bot messages in history come from sessions that already have them). Removing it causes adopted-context double-counting across every threaded session. |
| Pre-allocate sessionId, seed first, post after | Requires a local-id ↔ remote-id mapping layer for every channel. Significant new infra, no real win. |
| Restructure `send_slack_message` into a session-spawning verb (M3 from exploration) | Architectural rewrite of proactive posting. Heaviest variant considered; the ingredients to fix this without that change all exist. |

**Rationale:** the calling tool has the payload in hand when it
posts; the platform returns the message id synchronously; the bootstrap
protocol is the natural carrier.

### Decision 2: Seed role and provenance — assistant-role with a `ProactivePostSeed` flag

The seeded entry is written into the new session's transcript with
role `assistant` (because the agent did, in fact, post it) plus a
`ProactivePostSeed` provenance flag that distinguishes it from a
real LLM-produced turn.

**Alternatives considered:**

- **`system`-role seed.** More semantically pure ("the system informs
  the LLM that a prior agent session posted X"), but requires a new
  role concept in the context assembler. The cost of revisiting later
  is one line at the seeding site, so we start with assistant-role.
- **`user`-role synthetic message.** Causes the LLM to treat it as a
  user turn and respond to it. Defeats the purpose.

The provenance flag is load-bearing: it suppresses
`TurnCompleted` / `TurnRecorded` dispatch (Decision 3). If a future
eval shows the LLM confabulates more on an `assistant`-role seed
than a `system`-role seed, the role can be lifted to `system` at the
seeding site without touching the rest of the design.

### Decision 3: Seed write does not emit normal turn lifecycle events

The seed write SHALL NOT fire the events that an LLM-produced
assistant turn would fire — no `TurnCompleted`, no `TurnRecorded`, no
subscriber dispatch (per `netclaw-session`'s subscriber model). The
seed is a passive transcript write that only affects the next
context assembly, not the active turn-completion pipeline.

**Implementation hook:** the `ProactivePostSeed` provenance flag (from
Decision 2) is checked at the dispatch points in the session output
pipeline; entries with the flag short-circuit the dispatch path while
still being persisted to the transcript.

**Why this matters:** without this suppression, the subscriber model
would fire `TurnCompleted` for a turn that no LLM produced — which is
a lie to subscribers, would cause Slack to re-post the message
(double-post), would inflate telemetry, and could trigger
output-driven side effects (e.g., search indexing) on content that
didn't go through the normal output path.

### Decision 4: Idempotency keyed by platform message id

The seeded transcript entry is keyed by the platform-assigned
message id. A retried `StartProactiveThread` for the same message
id — possible under Akka.Reminders redelivery, crash recovery, or any
restart between post and ack — SHALL NOT double-seed. The existing
entry is detected and the bootstrap acks normally.

**Why message id and not sessionId:** sessionId is keyed to the
thread/conversation as a whole; a future "reply-with-more-content"
proactive-post variant could legitimately want to add a *second*
seeded entry to the same session. Message-id keying admits that
without ambiguity.

### Decision 5: Ordering is a property of the actor infrastructure, not the protocol

The race-window concern — that a user reply could arrive at the
binding actor before the bootstrap seed — is not a real concern in
production because:

```
                  channel-boundary actor
                  (e.g., SlackConversationActor)
                  ─────────────────────────────
                                                FIFO mailbox.
                                                ONE message processed at
                                                a time.

  Tool (in-process) ──▶ StartProactiveThread ──┐
                                               │
  Slack Events API ──▶ SlackInboundMessage  ──┴──▶ first to arrive wins.

  Latency comparison:
  - In-process Akka Ask: microseconds.
  - Events API webhook for user reply: many round-trips + user
    physically reading and typing. Many orders of magnitude slower.

  So in production, bootstrap always wins; the binding actor is born
  with the seed. No buffering machinery needed.
```

The conformance contract therefore does not specify ordering
behavior. Implementations that route bootstrap through their channel
boundary actor (which all channel adapters in this codebase do, by
architectural convention) inherit ordering for free.

**Alternative considered:** specify "no inbound message in the new
session is processed before seeding" as a hard requirement,
forcing implementations to buffer inbound or use an atomic init
dance. Rejected because the architectural property already
guarantees this; specifying it would create test obligations against
a scenario that physics already prevents.

### Decision 6: Scope is bootstrap-only

Three categories of bot-authored content:

| Category | How it lands in transcript | Spec covers? |
|---|---|---|
| Reentrant reminder (`DeliveryKind.CurrentSession`) | Normal `DeliverTrustedSessionTurn` → input queue → output pipeline → recorded | No (covered by `netclaw-session`) |
| Normal LLM reply in existing session | Output pipeline records on the way out | No (covered by `netclaw-session`) |
| Proactive post creating a new session | **Currently lost at handoff** | **Yes — this spec** |

The first two are already coherent via the normal turn lifecycle.
The new spec covers only the third.

This is captured as an explicit out-of-scope clarification scenario
in the spec so future readers don't try to extend the contract to
cover the already-coherent cases.

## Risks / Trade-offs

| Risk | Mitigation |
|---|---|
| LLM behaves badly on assistant-role seed (confabulates reasoning when asked) | Eval suite regression case (per CLAUDE.md). If observed, lift role to `system` at the seeding site (one-line change). |
| Subscriber dispatch suppression accidentally skips a legitimate `TurnCompleted` somewhere | Probe-based unit test: subscribe a fake subscriber, write a seed, assert no events; then write a real assistant turn, assert events fire. Catches accidental over-suppression. |
| Provenance flag forgotten on transcript entry (regression risk) | Conformance test base asserts the provenance flag is present on entries written by `StartProactiveThread` (or its per-channel equivalent). |
| Cross-channel drift (Discord ships with subtly different semantics) | Shared conformance test base; Discord's test project subclasses it and must pass. Failures are loud and channel-specific. |
| Idempotency check missed in a retry path | Conformance test: send the same bootstrap twice; assert single entry. Crash-replay test: kill the binding actor between seed and ack, recreate, redeliver, assert single entry. |
| Rich-payload bloat in session context | Out-of-scope to limit payload size in this spec; rely on existing context-window management. Operational signal (payload size logged) catches regressions empirically. |
| Race-window edge case in non-actor channel adapters | The contract is silent on ordering. Any non-actor adapter that wants to claim conformance must achieve ordering by some other means. Not a concern for any current or proposed channel. |

## Actor boundaries and persistence implications

- **Channel boundary actor** (e.g., `SlackConversationActor`) remains
  the single entry point for all session creation in the channel.
  Both `StartProactiveThread` and inbound user messages route through
  it. The boundary's FIFO mailbox is the ordering primitive.
- **Channel binding actor** (e.g., `SlackThreadBindingActor`) owns
  the session's transcript. The seed write is a method on that actor;
  the bootstrap protocol handler invokes it before sending the ack.
- **Persistence implications:** the seed is written to the same
  transcript store as normal turns (whatever the session pipeline
  uses), with the provenance flag on the persisted record. On
  recovery, loading the session transcript naturally includes the
  seed; no special recovery code path.
- **Akka.Reminders interaction:** redelivery is the standard
  failure-mode trigger. Idempotency keying (Decision 4) handles it.
- **Output pipeline isolation:** the seed write does not enter the
  output pipeline at all. Subscribers (`netclaw-session`) see the
  session's normal turns; they never see synthetic events for the
  seed. Persistence still happens because the transcript store is
  upstream of the dispatch path.

## Failure modes and recovery behavior

| Failure | Visible effect | Recovery |
|---|---|---|
| `PostNewThreadAsync` fails (network/Slack rate-limited) | Tool returns error to calling session; no bootstrap fires; no session created | LLM in calling session sees error, can retry or surface to user |
| Bootstrap message lost in flight between tool and gateway (in-process Akka — effectively impossible) | Tool's `Ask` times out; tool returns error; **Slack message is posted with no actor backing it** | Tool error message includes thread coordinates; user replies would land on a session that doesn't exist yet, which causes the standard "lazy-create on first inbound" path (no seed) |
| Seed write fails after post succeeded | Bootstrap actor logs failure, does not ack | Originating tool sees `Ask` failure; **same orphan-thread state** as above. See "Open question 1" below. |
| Bootstrap ack arrives at tool after timeout | Tool already errored; bootstrap actor is fine | Slightly stale state; user replies still work because seed completed. Acceptable. |
| Crash between post and bootstrap (e.g., daemon restart) | Slack has the message; no actor exists yet | First inbound lazy-creates the binding actor with no seed (current behavior, falls back to memory-adoption). On *manual* operator action, the seed cannot be reconstructed. See "Open question 2". |
| Crash between seed write and ack | Seed is persisted; ack lost | Akka.Reminders redelivers; idempotency check (Decision 4) recognizes the existing seed; redelivery acks cleanly |
| Subscriber probe receives spurious event from a seed write (bug) | Subscribers may double-handle | Caught by conformance unit test. Fix the suppression path before merge. |

## Migration Plan

This is a forward-only change. No data migration is required: the
seed mechanism only applies to *new* proactive-post sessions; existing
amnesiac sessions stay amnesiac (their threads can be replied to but
the agent's first reply on those threads will continue to be
context-poor until the next proactive post on a new thread).

**Rollback strategy:** the change is gated behind code paths in the
Slack channel; reverting the `StartProactiveThread` field addition
and the seed-write call restores prior behavior. Eval regression
case stays in the suite as a documenting fixture even if reverted.

**Order of merge:**

1. Spec + design + tasks (this change).
2. Slack implementation (transcript seed + provenance flag handling +
   conformance test).
3. Eval regression case.
4. Sandbox smoke test (manual).
5. Cross-reference from `netclaw-slack-socket` (optional polish).

## Open Questions

1. **Seed-failed-after-post recovery.** If the network post succeeds
   but the in-process seed write throws (out-of-memory, persistence
   transient), we currently surface an error to the calling session
   while the Slack post stays. Should the channel adapter attempt
   compensating actions — e.g., `chat.delete` the just-posted
   message? Lean: no, because the user has likely already seen the
   message; deleting it is more confusing than the orphan state. But
   worth a deliberate decision in implementation.

2. **Crash-between-post-and-bootstrap recovery.** If the daemon
   crashes after `PostNewThreadAsync` returns but before
   `StartProactiveThread` is sent, the Slack message exists but the
   bootstrap never runs. On daemon restart, the actor system has no
   knowledge of the orphaned thread. Lean: accept the orphan; the
   first user reply lazy-creates the binding actor with no seed
   (current behavior). The operational signal
   `proactive_session_seeded` failures should make these visible.
   Recovery via Slack history backfill would require the change to
   the backfill filter that Decision 1's alternatives table rejected.

3. **Eval-suite assertion shape.** The eval case asserts "no
   off-topic confabulation when the user replies." How strict should
   the assertion be? Lean: assert that the response mentions or
   references the seeded payload content (positive signal) AND does
   not mention any of the known-hallucinated topics from the repro
   ("DGX Spark", "NADDOD", "MI350P") that came from memory recall.

These don't block the change; they're deliberation items for
implementation.

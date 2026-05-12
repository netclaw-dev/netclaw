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
off-topic and confabulated because it had no transcript anchor.

The bug exists because the existing "bot messages in channel history
were produced by a session that recorded them on the way out" invariant
silently breaks across the proactive-post boundary: the producing
session (ephemeral reminder execution) terminates without writing the
posted text into the new channel session's transcript, and the new
channel session was never the producer.

The fix is small and well-bounded, but the contract is cross-cutting
(Slack today, Discord next, every future channel after). It deserves
to be specified once, in one place, so every channel that ships a
proactive-post tool lands on the same correctness contract from day
one rather than discovering the bug independently.

[i953]: https://github.com/netclaw-dev/netclaw/issues/953

## What Changes

- Introduce a new **`proactive-channel-sessions`** capability spec defining
  the cross-channel conformance contract for agent-initiated channel
  session bootstrap. The contract is channel-agnostic — Slack is the
  inaugural implementer; Discord and any future channel must conform
  when they ship their proactive-post tool.
- Slack adapter: extend `StartProactiveThread` to carry the posted
  payload and platform message id, and have `SlackThreadBindingActor`
  seed the new session's transcript before acking the originating
  tool call. Seeding uses an `assistant`-role transcript entry with a
  `ProactivePostSeed` provenance flag that suppresses
  `TurnCompleted` / `TurnRecorded` / subscriber dispatch (per
  `netclaw-session`).
- Cross-reference the new spec from `netclaw-slack-socket` so Slack's
  spec documents its conformance authority.
- Add a shared conformance test base class so any channel implementing
  proactive posts inherits the same correctness assertions (seeds
  before ack, idempotent per message id, no subscriber dispatch on
  seed write, seed survives restart).
- Add an eval suite regression case for the proactive-session amnesia
  scenario, per the eval-suite trigger in
  `CLAUDE.md` ("changes to context assembly → eval coverage").

Not a breaking change. The new field on `StartProactiveThread` is
optional at the protocol layer until callers populate it; once Slack
is updated, the field is always present in production. Idempotency
keying means replay-safe across crash recovery.

## Capabilities

### New Capabilities

- `proactive-channel-sessions`: cross-channel contract for sessions
  bootstrapped by an agent-initiated proactive post. Defines the
  transcript-seeding invariant, idempotency requirements, and the
  isolation rules that prevent the seed write from being treated as
  an LLM-produced turn.

### Modified Capabilities

(none — the new spec adds a contract that adjacent specs reference,
but does not change requirements in any existing spec)

- `netclaw-slack-socket` is touched only by a non-normative cross-reference
  pointing to the new spec; no requirement deltas.
- `thread-history-backfill` is **explicitly preserved as-is**. The
  `BotId`-skipping filter in `SlackThreadHistoryFetcher` enforces a
  correct invariant (bot messages in history were produced by a session
  that already has them in transcript). The new spec closes the
  proactive-bootstrap hole by extending the producing-session-records
  invariant, not by removing the backfill filter.

## Impact

### Code

- `src/Netclaw.Channels.Slack/SlackIngressMessages.cs` — new field on
  `StartProactiveThread`.
- `src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs` — seed
  transcript in `HandleProactiveThreadAsync` before ack.
- `src/Netclaw.Channels.Slack/Tools/SendSlackMessageTool.cs` — plumb
  `args.Message` and the returned platform message id into
  `StartProactiveThread`.
- Session output pipeline / subscriber dispatch path — recognize the
  `ProactivePostSeed` provenance flag and suppress
  `TurnCompleted` / `TurnRecorded` events for seed writes only.
- New shared conformance test base under `Netclaw.Channels.*` test
  utilities.
- Slack-side conformance test using the new base.
- New eval case under `evals/` for the proactive-session amnesia
  regression.

### APIs

- Internal Akka protocol only. No public surface change.

### Cross-channel implications

- Discord's `send_discord_message` (issue #953) must implement to this
  contract from day one; the issue references the spec explicitly.
- Future TUI / SignalR / webhook-side proactive-post mechanisms must
  also conform when they ship.

### PRD lineage

- **PRD-008** (Scheduling and Periodic Tasks) — outcomes (3) "task
  results are posted to the originating or configured Slack channel"
  and the execution model that "creates a fresh session actor" are
  the direct upstream rationale for the bootstrap path. The bug
  described here is a correctness gap in honoring those outcomes.
- **PRD-009** (Input Adapters and Unified Input) — the core insight
  "everything is just a message arriving at a session actor" implies
  parity between user-initiated and agent-initiated session bootstrap.
  This spec closes the parity gap.

### Security and operational impact

- **No new attack surface.** The seeded entry is written by the
  channel adapter's own out-of-band invocation; it does not introduce
  any new pathway for external input. The pre-existing
  `IsBotMessage → drop` inbound filter remains untouched and continues
  to do its loop-prevention work.
- **No privilege change.** Seed content is identical to what the
  agent just posted, which already passed ACL and audience checks at
  the tool boundary. The seed merely records what was sent.
- **Operational signals to add:** counter
  `proactive_session_seeded{channel, outcome}` for telemetry on
  bootstrap success/failure; structured log on seed write keyed by
  `messageId`, `sessionId`, payload size. Useful for incident triage
  if seeding regresses post-deploy.
- **Idempotency for crash recovery:** Akka.Reminders may redeliver
  envelopes after a crash; the message-id-keyed idempotency on the
  seed write keeps recovery safe.
- **Memory adoption interaction:** the seed lands in the session's
  loaded LLM context before the existing memory-recall coordinator
  runs, so the agent now has a stronger anchor than fuzzy memory
  adoption. Reduces (does not eliminate) the off-topic hallucination
  pattern observed in the repro.

### MVP scope statement

**In scope for MVP:**
- The full conformance contract as a new capability spec.
- Slack adapter implementation (`SendSlackMessageTool` is the only
  proactive-post tool currently in production).
- Shared conformance test base + Slack-side test.
- Eval regression case.
- Cross-reference into `netclaw-slack-socket` (non-normative).

**Out of scope for MVP:**
- `send_discord_message` implementation — tracked under issue #953,
  must honor this spec when it ships.
- TUI / SignalR / webhook-side proactive-post tooling — not yet
  implemented; will be added when those channels need it.
- **Agent reasoning lineage / explainability** ("why did the agent
  say this?"). The seed makes the new session see *what* it posted,
  not *why*. Reasoning lineage from the originating ephemeral session
  is a separable concern and is not addressed by this spec.
- Removing or relaxing the `BotId`-skipping filter in
  `SlackThreadHistoryFetcher` — that filter remains correct.

## Context

Threaded channel adapters hydrate prior thread messages into an adopted-context
window so an authorized inbound turn carries the conversation that preceded it.
The `thread-history-backfill` capability specifies that a bot-authored thread
**root** is hydrated as adopted context for the first authorized reply — the
scenario that covers a proactively-created thread (a reminder posting via
`send_slack_message`).

PR #958 implemented this correctly. PR #990 then changed hydration to run
**once per actor lifetime**, driven by the `RecoveryCompleted` self-tell: the
binding actor transitions `Initializing → Hydrating → Active`, runs
`PerformOneShotHydrationAsync` once in `Hydrating`, and thereafter the `Active`
inbound handler is fetch-free. PR #990 fixed a real bug (in-flight images
re-downloaded on every inbound) but did not update the spec.

For a **proactively-created** thread the binding actor is created at the moment
the agent posts the root (`SendSlackMessageTool` → `StartProactiveThread`). Its
single hydration therefore runs when the thread contains only the bot root.
`PerformOneShotHydrationAsync` requires an **authorized** gap message to act as
the turn trigger; the bot root classifies as `Pending` (the bot is not an
allowed *user*), so `trigger == null`, hydration logs "deferring backfill to
next live inbound" and returns. The "next live inbound" — the human reply —
takes the fetch-free `Active` path and never re-hydrates. The bot root is
silently dropped and the agent answers with no anchor.

## Goals / Non-Goals

**Goals:**

- Restore `thread-history-backfill` conformance: a proactively-created thread
  hydrates its bot root into the adopted-context window of the first authorized
  inbound, including the quick-reply case (reply arrives within the same actor
  lifetime, before passivation).
- Keep the fix channel-agnostic — `SlackThreadBindingActor` and
  `DiscordSessionBindingActor` received the identical PR #990 change.
- Preserve PR #990's guarantee: no thread-history fetch on ordinary subsequent
  inbounds.

**Non-Goals:**

- Surfacing reminder identity/task (vs. the posted message text) into the
  thread — separate follow-up, requires its own spec delta.
- Closing the Discord-DM amnesia case — a documented permanent limitation
  (flat conversation, no thread root).
- Adding a proactive-posting tool for Discord (issue #953).
- Any change to ACL, authorized-trigger classification, the history fetchers,
  or the cursor/watermark persistence model.

## Decisions

### Decision 1: Re-arm hydration when it defers for lack of an authorized trigger

`PerformOneShotHydrationAsync` distinguishes two non-enqueueing outcomes today,
but treats them identically (hydration consumed):

1. **Complete** — empty thread, or cursor already at head, or an authorized
   turn was enqueued. Nothing further is owed.
2. **Deferred** — a non-empty gap was fetched but contained no authorized
   message to anchor a turn. A turn is still owed once an authorized inbound
   arrives.

The binding actor will track a `_hydrationPending` flag set only on outcome (2).
While set, the first **authorized** live inbound performs the deferred
hydration instead of taking the fetch-free path; the flag is then cleared and
the actor reverts to normal fetch-free behavior.

This is exactly what the spec already requires — hydration "only when an
authorized inbound message is about to create an executable turn." The
once-per-lifetime optimization remains valid; it is simply not counted as spent
until a hydration has *completed*.

### Decision 2: The live inbound is the authorized trigger; the fetched gap is its adopted context

When `_hydrationPending` and an authorized inbound arrives, the actor fetches
thread history, computes the gap above the cursor, classifies it, and merges
the gap as adopted context onto the **live** inbound via the existing
`AdoptedContextContentBuilder.MergeWithCurrentMessage` — the same merge the
hydration path already performs. The live inbound keeps its own (live-pipeline)
representation and remains the executable message; the normal fetch-free
enqueue for that inbound is skipped so the turn is enqueued exactly once.

**Alternatives considered:**

- *Re-run `PerformOneShotHydrationAsync` standalone and let it synthesize the
  trigger from history.* Rejected: it would build the trigger from the
  history-fetched copy of the human reply, diverging from the live inbound's
  representation, and risks a double enqueue (hydration + live handler).
- *Carry the deferred gap in actor memory and merge it later without
  re-fetching.* Rejected: the gap can go stale (more messages may arrive before
  the reply), and restart-safety still requires a re-fetch — so an in-memory
  copy adds a divergence risk for no real saving. Re-fetching once yields
  current truth.

### Decision 3: No new persistent state

`_hydrationPending` is in-memory only. The durable cursor (`_cursorTs` /
`CursorAdvanced`) advances solely on `TurnCompleted`, so until the proactive
thread's first authorized turn completes, the bot root remains above the
cursor. On actor crash/restart, recovery re-queues `PerformHydration`; the
re-run recomputes the gap from `conversations.replies` and the recovered
cursor. Restart-safety is therefore already provided by the existing
watermark + recovery-driven hydration; `_hydrationPending` only optimizes the
within-lifetime quick-reply path.

### Actor boundaries

The change is contained entirely within the binding actors
(`SlackThreadBindingActor`, `DiscordSessionBindingActor`). The gateway,
conversation actor, session pipeline, and history fetchers are untouched. The
binding actor remains the owner of thread-gap fetch and watermark bookkeeping,
per the `thread-history-backfill` requirement "Authorized sync watermark and
gap computation."

## Risks / Trade-offs

- **[Reintroducing the PR #990 image storm]** → The re-armed fetch fires at
  most once more per actor lifetime, only after a deferral, and never on
  ordinary inbounds. A proactive thread at deferral time has the cursor at
  origin and no in-flight turns, so the duplicate-in-flight-media condition
  PR #990 fixed cannot arise. A regression test asserts no second fetch on a
  normal second inbound.
- **[Double enqueue / duplicate adoption]** → The re-armed path enqueues
  exactly one authorized turn (live inbound as trigger, fetched gap as adopted
  context) and skips the normal fetch-free enqueue for that inbound.
- **[Re-armed hydration fetch fails]** → Non-fatal, consistent with today's
  "hydration threw; continuing without backfill": the turn proceeds without the
  adopted root. The cursor does not advance, so a later turn can still adopt
  the gap. `_hydrationPending` stays set so a subsequent authorized inbound
  retries.
- **[Non-authorized message arrives while pending]** → No turn is created and
  `_hydrationPending` stays set, matching the existing "unauthorized inbound
  does not trigger hydration" rule.
- **[Discord]** → `DiscordSessionBindingActor` gets the symmetric change.
  Discord DMs have no thread root, so hydration returns an empty gap, never
  defers, and never sets `_hydrationPending` — the documented limitation is
  preserved unchanged.

## Migration Plan

Pure behavior fix. No data, persistence-schema, or API migration. Rollback is a
straight revert of the change; behavior returns to the once-per-lifetime
strategy with no residual state to clean up.

## Open Questions

None.

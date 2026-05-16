## Why

A proactively-created channel thread (e.g. a reminder posting via
`send_slack_message`) loses the message that opened the conversation: when the
user replies, the agent answers with no record of what it posted and
confabulates an unrelated topic. This is a **regression** — the
`thread-history-backfill` capability already specifies the correct behavior
("Bot's own posted message at thread root is hydrated as adopted context"), and
PR #958 implemented it correctly. PR #990 ("hydrate thread history once per
actor lifetime") then silently regressed it without touching the spec.

## Source PRDs

- `PRD-009`: Input adapter contract and transport-agnostic session handoff.
- `PRD-008`: Scheduling and periodic tasks (reminders are the primary producer
  of proactive threads).
- `PRD-002`: Gateway security envelope — authorized-trigger classification of
  hydrated gap messages.

## What Changes

- A one-shot thread-history hydration that **defers for lack of an authorized
  trigger** SHALL NOT be counted as consumed. The binding actor SHALL re-arm so
  the next authorized inbound performs the deferred hydration, adopting the gap
  (including a bot-authored thread root) into that inbound's adopted-context
  window.
- This restores conformance for proactively-created threads, where the binding
  actor's lifetime begins at post time — before any authorized human inbound
  exists — so the existing "once per actor lifetime at startup" strategy never
  hydrates the root for a quick reply.
- PR #990's guarantee is preserved: hydration still does not run on *ordinary*
  subsequent inbounds; re-arming only applies when the prior hydration produced
  no authorized turn.
- Applied symmetrically to the Slack and Discord binding actors (PR #990
  changed both). Discord cannot exercise the proactive path yet (no proactive
  posting tool — issue #953), but the fix lands so Discord is correct when that
  ships. Discord DMs remain a documented out-of-scope limitation.
- No breaking changes; no API or persistence-schema changes.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `thread-history-backfill`: Add a requirement that a hydration deferred for
  lack of an authorized trigger is re-armed (not consumed), and a regression
  scenario covering a proactively-created thread whose authorized reply arrives
  within the same actor lifetime (before passivation). Clarifies that the
  "once per actor lifetime" optimization is valid only once a hydration has
  produced an authorized turn or confirmed an empty thread.

## Impact

- **Code**: `src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs`
  (`PerformOneShotHydrationAsync`, `Hydrating`/`Active` behaviors);
  `src/Netclaw.Channels.Discord/DiscordSessionBindingActor.cs` (symmetric
  change). No change expected in the history fetchers.
- **Tests**: `SlackThreadBackfillIntegrationTests`, `SessionBindingContractTests`,
  `SlackProactiveThreadTests`.
- **Behavior**: one additional thread-history fetch on the first authorized
  inbound of a proactively-created thread; bounded — never repeats on ordinary
  inbounds.
- **Security/operational**: no change to ACL or authorized-trigger
  classification; hydrated gap messages keep their existing
  authority-at-inclusion semantics. No new failure modes; hydration failure
  remains non-fatal (logged, turn proceeds).

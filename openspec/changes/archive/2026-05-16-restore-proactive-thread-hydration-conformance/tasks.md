## 1. Slack binding actor

- [x] 1.1 In `SlackThreadBindingActor`, add an in-memory `_hydrationPending`
      flag (default false). Make `PerformOneShotHydrationAsync` set it when it
      returns via the deferral path — a non-empty gap with no authorized
      trigger (`SlackThreadBindingActor.cs:867-871`) — and leave it false on
      every completion path (empty thread, cursor at head, fetch failure with
      no gap, authorized turn enqueued).
- [x] 1.2 In the `Active` behavior's authorized-inbound handling, when
      `_hydrationPending` is set: fetch the current thread gap, classify it,
      and merge it as the adopted-context window onto the live inbound
      (executable message) using `AdoptedContextContentBuilder.MergeWithCurrentMessage`
      — reusing the merge already performed by `PerformOneShotHydrationAsync`.
      Enqueue exactly one authorized turn and skip the normal fetch-free
      enqueue for that inbound.
- [x] 1.3 Clear `_hydrationPending` once a re-armed pass completes (gap merged,
      or empty). On a re-armed fetch failure, keep it set so a later authorized
      inbound retries; ensure the inbound still executes (non-fatal).
- [x] 1.4 Confirm an unauthorized inbound arriving while `_hydrationPending` is
      set does not perform hydration, does not dispatch a turn, and leaves the
      flag set.
- [x] 1.5 Verify no behavior change for a normal thread: hydration that
      completes never sets `_hydrationPending`, so subsequent inbounds stay on
      the fetch-free path (PR #990 guarantee preserved).

## 2. Discord binding actor

- [x] 2.1 Inspect `DiscordSessionBindingActor` and confirm it has the same
      `Hydrating`/`Active` structure and a `PerformOneShotHydrationAsync`
      equivalent with the same deferral path (PR #990 changed both actors).
- [x] 2.2 Apply the symmetric `_hydrationPending` re-arm change to
      `DiscordSessionBindingActor`. Confirm Discord DMs (no thread root) yield
      an empty gap, never defer, and never set the flag.

## 3. Tests

- [x] 3.1 Add a `SlackThreadBackfillIntegrationTests` case: proactively create
      a thread (bot root only), then deliver an authorized human reply within
      the same actor lifetime; assert the first authorized turn's
      adopted-context window contains the bot root and the reply is the
      executable message. Synchronize with `Ask`/acks — no `Task.Delay`.
- [x] 3.2 Add a `SessionBindingContractTests` case asserting no thread-history
      re-fetch on an ordinary second inbound after a completed hydration.
- [x] 3.3 Add a `SlackProactiveThreadTests` case for the unauthorized-inbound-
      while-pending scenario (flag stays set, no turn dispatched).
- [x] 3.4 Add a re-armed fetch-failure case: the authorized inbound still
      executes without an adopted window and the flag remains set.
- [x] 3.5 Add the symmetric Discord binding-contract test (proactive path
      unreachable today, but assert the deferral/re-arm wiring and the
      Discord-DM empty-gap path).

## 4. Verification and docs

- [x] 4.1 Run the affected test projects (`Netclaw.Actors.Tests`,
      `Netclaw.Channels.Slack` / Discord test projects); all green.
- [x] 4.2 Run `dotnet slopwatch analyze` (no new violations) and
      `./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 4.3 Update operational/runbook docs only if behavior described there
      changes — confirm no `docs/spec` drift; the OpenSpec delta is the spec of
      record.
- [x] 4.4 Run `/opsx-verify` for this change; resolve any conformance gaps.

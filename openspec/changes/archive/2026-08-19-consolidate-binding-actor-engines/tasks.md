# Tasks: consolidate-binding-actor-engines

Working branch: `refactor/binding-engine`, stacked on `refactor/delivery-failure-drift` (PR #2004), worktree `/home/aaronontheweb/repositories/netclaw-dev/netclaw-refactor-cleanup`. Every numbered group ends with the full `Netclaw.Actors.Tests` suite green, `dotnet slopwatch analyze` clean, and header verification clean, and is one commit.

## 1. Discord cursor stringization (prerequisite)

- [x] 1.1 Add `SnowflakeCursorComparer` (length-then-ordinal) to `Netclaw.Channels` with a unit test proving equivalence to `ulong` ordering: cross-digit-length pairs, adjacent powers of ten, `ulong.MaxValue`, and short synthetic test IDs
- [x] 1.2 Replace Discord's internal `ulong` cursor (`_cursorSnowflake`, `AdvanceCursor(ulong)`, `TryParseSnowflake`) with `string` state compared via the comparator; persisted `CursorAdvanced` handling is already string and stays untouched
- [x] 1.3 Run Discord contract + cursor tests; verify no persisted-format change with the serialization test suite

## 2. Gap-hydration engine

- [x] 2.1 Extract the shared engine (fetch → cursor-filter → classify → merge adopted context → enqueue) into `Netclaw.Channels`, transplanting the Mattermost copy as the reference implementation; constructor takes required classifier, authorization callback, history fetcher, and cursor comparator
- [x] 2.2 Delegate Mattermost, then Discord, then Slack hydration to the engine; diff each removed region against the transplant to confirm mechanical equivalence; STOP and surface any real semantic difference instead of normalizing it
- [x] 2.3 Hydration contract tests green for all three channels (fetch-once, stash-during-hydration, restart re-runs, adopted-context backfill, deferred hydration)

## 3. Approval-response flow

- [x] 3.1 Extract `ApprovalResponseFlow` (text approval parsing, cold-spawn forwarding, prompt resolution via `PendingApprovalLookup`) with required render hook and optional Mattermost synchronous-reply hook
- [x] 3.2 Delegate Discord and Mattermost; verify Slack's lookup shape — if semantically identical, delegate Slack too, otherwise Slack shares the outer flow only and the difference is documented in the parity spec
- [x] 3.3 Approval contract tests green per channel, including wrong-requester rejection, cold text approval, pruned option order, and post-turn approval forwarding

## 4. Output template and safe transport calls

- [x] 4.1 Extract turn-completion bookkeeping engine; engine returns events to persist, actor keeps `Persist`/`PersistAll`; channel-specific outputs (`SessionTitleOutput`, `ProcessingStateOutput`) go through the channel hook
- [x] 4.2 Extract the safe transport-call skeleton (timing → call → telemetry → notify) preserving per-channel telemetry categories and the PR #2004 fail-loud contract — Discord and Mattermost delegate; Slack keeps its own by the stop rule (three exception classes, one with `RecordReplyRejected` telemetry no other channel has, plus deferred failure reporting)
- [x] 4.3 Contract tests green: delivery-failed feedback, empty-turn fallback, reminder settlement, `Feedback_send_failure_faults_the_actor`

## 5. Finish

- [x] 5.1 Full solution build with zero warnings; full `Netclaw.Actors.Tests`, `Netclaw.Daemon.Tests` channel suites; slopwatch and header gates
- [x] 5.2 Line-count accounting for the PR description (target ~1,200-1,500 lines removed) and parity-spec sync via `/opsx-sync`
- [x] 5.3 Submit `refactor/binding-engine` as PR 4 via `gh stack submit`; PR body documents any differences surfaced by the stop rule

# Proposal: consolidate-binding-actor-engines

## Why

`SlackThreadBindingActor`, `DiscordSessionBindingActor`, and `MattermostSessionBindingActor` copy the same orchestration logic. Discord and Mattermost are ~78% line-identical. This duplication already produced one confirmed drift bug: Discord and Mattermost swallowed feedback-pipe failures that Slack propagated (fixed in PR #2004). Every future fix to hydration, approval flow, or delivery handling must land three times by hand. Each hand copy is a chance to miss a security-relevant path.

Source PRDs: PRD-001 (Netclaw MVP, channel bindings), PRD-002 (gateway security envelope, approval checks), PRD-009 (input adapters and unified input).

## What Changes

- Extract a shared **gap-hydration engine** into `Netclaw.Channels`. It owns the fetch → cursor-filter → injection-classify → adopted-context-merge → turn-enqueue algorithm. The three actors delegate to it. (~600-700 duplicated lines removed.)
- Extract a shared **approval-response flow**. It owns text approval parsing, cold-spawn approval forwarding, and prompt resolution. Mattermost keeps a synchronous HTTP-reply hook; Discord and Slack do not use it. (~450-550 lines removed.)
- Extract a shared **output-handling template** for `TurnCompleted`/approval-prompt/reminder-observer bookkeeping, with a channel-specific hook for outputs only some channels support (Discord thread rename, processing indicators). (~70-90 lines removed.)
- Extract a shared **safe transport-call skeleton** (timing → call → telemetry → failure notify). (~60-80 lines removed.)
- **Prerequisite**: Discord's internal cursor changes from `ulong` snowflake to `string`, which the persisted `CursorAdvanced` event already stores for every channel. A unit test SHALL prove ordinal string comparison orders real Discord snowflake ranges the same as numeric comparison before the numeric path is removed.
- Zero behavior change. No persisted-type change. Builds on the `PendingApproval*` helpers from PR #2002.

In scope: the four engine extractions above, the Discord cursor change, and parity tests.
Out of scope: a shared binding-actor base class that owns the actor FSM; Mattermost processing-indicator support (flagged as a possible feature gap, needs a product decision); the generic approval API rework (issue #1944); SignalR channel extraction (issue #691).

## Capabilities

### New Capabilities

- `channel-binding-parity`: cross-channel guarantee that gap hydration, approval response handling, output-completion bookkeeping, and transport-failure escalation run through single shared implementations, with per-channel hooks limited to genuine transport differences.

### Modified Capabilities

<!-- none: thread-history-backfill and tool-approval-gates requirements are unchanged; this change consolidates their implementations without behavior change -->

## Impact

- Code: `src/Netclaw.Channels` (new engine types), `src/Netclaw.Channels.Slack`, `src/Netclaw.Channels.Discord`, `src/Netclaw.Channels.Mattermost` (delegation), `src/Netclaw.Actors.Tests` (parity contract tests).
- Security impact: the approval requester check and the prompt-injection gap classification move from three copies to one. A fix in one place reaches all channels. No ACL or policy semantics change. The engines take required (non-nullable) security dependencies per the constitution.
- Operational impact: none at runtime. Log messages keep their per-channel adapter fields. No config change, no migration, no persisted-format change.
- Rollout: lands as PR 4 of the refactor stack, stacked on #2004. Revert is a single PR revert; no data migration to unwind.

## Why

When a tool call needs human approval, the session parks the turn and posts a
Slack/Discord approval prompt. If the session is then idle-passivated (or its
turn fails, or the actor restarts), it loses all in-memory knowledge of the
pending approval — the prompt stays clickable but clicking it does nothing,
and the user must send a fresh message to wake the agent. This breaks the
recovery guarantee of FR-003 (Persistent Recovery) for the FR-011 (Tool Access)
approval path: a pending approval is durable session state that is currently
not persisted, and the session state machine only accepts an approval response
while actively `Processing`.

## What Changes

- Persist the set of pending tool interactions (the in-memory
  `_pendingToolInteractions`) into the session snapshot, so a recovered session
  knows which tool-approval prompts are still outstanding.
- Restore pending interactions on recovery from a snapshot; clear them when a
  later journal event (turn recorded / compaction) supersedes the snapshot.
- Accept a tool-interaction response while the session is `Ready`,
  `Passivating`, or `Compacting` — not only `Processing`:
  - `Ready` (incl. cold-recovered): re-drive the parked tool batch from the
    last assistant message and continue the turn.
  - `Passivating`: abort passivation, return to `Ready`, then re-drive
    (symmetric with the existing abort-on-user-message behavior).
  - `Compacting`: buffer the response and replay it after compaction completes.
- Fail loud when an approval response arrives for a call that is genuinely
  unknown (expired prompt): post a user-visible "approval prompt expired"
  message instead of silently dropping it.

In scope (MVP): snapshot persistence of pending interactions, response handling
across session phases, whole-batch re-drive, and the expired-prompt message.

Out of scope: proactively editing/expiring stale Slack/Discord prompt messages
when a turn fails (tracked as a follow-up — it touches the channel output
protocol and every adapter); persisting partial results of sibling tool calls
that completed before the approval pause (re-drive re-runs the whole batch).

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `session-state-machine`: the `Ready`, `Passivating`, and `Compacting` phases
  gain defined behavior for an incoming tool-interaction response (re-drive,
  abort-and-re-drive, buffer-and-replay respectively).
- `tool-approval-gates`: the mid-turn approval pause is no longer purely
  in-memory — a pending approval is durable across passivation and cold
  recovery, and an approval response for an expired call fails loud.
- `session-resume`: session recovery additionally restores outstanding tool
  approval prompts so a post-passivation approval click resumes the turn.

## Impact

- `src/Netclaw.Actors/Protocol/SessionSnapshot.cs` — new persisted records.
- `src/Netclaw.Actors/Serialization/Protos/netclaw_messages.proto` and
  `NetclawProtoMapper.cs` — append-only proto field (backward compatible; old
  snapshots recover with an empty pending set).
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — snapshot build/restore,
  per-phase response handlers, tool-batch re-drive.
- Security/operational impact: no change to the ACL/approval decision itself —
  the same `CanApprove` requester check and grant-persistence rules apply on
  the re-drive path. The change removes a silent-drop path (constitution: no
  silent fallbacks) and makes a parked approval durable. Re-drive replays the
  whole tool batch, so non-idempotent sibling tools in the same batch may
  re-execute; this is documented and accepted for MVP. No new external surface,
  no migration step — backward compatible with existing snapshots.

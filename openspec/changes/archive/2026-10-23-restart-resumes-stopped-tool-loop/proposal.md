## Why

A daemon stop on 2026-10-23 drained two sessions in deep tool loops (#2632). Each session logged that no restart reminder resumes its turn, and both turns were lost. A tool batch consumes the turn input, so the drain found no pending input and built no reminder. This breaks PRD-001 FR-016 (drain, restart, and relaunch the work) and PRD-001 goal 2 (session context survives a restart).

## What Changes

- When a restart drain stops a running turn after a tool batch, and each tool call of the turn has a recorded result, the restart reminder comes from the recorded authority of that turn. Person turns and automation turns use the same rule.
- After the restart, the restart reminder resumes that turn with the next model call over the stored history. The reminder text is not an input.
- The stopped turn gets the notice only when no reminder can resume it: text reached the person, an input stays pending, the stored authority is invalid, or the turn has no channel type.
- A tool that still runs at the coordinator deadline still blocks the reminder (unchanged).
- The final passivation snapshot carries the authority of the stopped turn. The drain acknowledges after the snapshot store confirms the write.

In scope: the restart drain, the restart reminder, and the restart resume of one session. Out of scope: a new tool, a new reminder kind, and a resume of a turn whose tool ran at the deadline.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: the drain and resume rules for a turn that stopped after its first tool batch.

## Impact

- Code: `SessionExecutionLifetime`, `LlmSessionActor` (drain stop, final snapshot, restart resume), `SessionState`, `SessionSnapshot`, and the snapshot protobuf mapping.
- Durable data: `SessionSnapshotProto` gets field 22 `restart_stopped_turn`. An old binary ignores it, so a rollback loses only the resume of that turn and posts no notice for it.
- Security: the resumed turn runs under its own recorded authority. The reminder never replaces that authority, and it never mixes two authorities.
- Operations: the drain can wait for one snapshot write before it acknowledges. The coordinator deadline stays 20 seconds.

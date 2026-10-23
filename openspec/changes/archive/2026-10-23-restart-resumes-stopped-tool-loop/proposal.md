## Why

A daemon stop on 2026-10-23 drained two sessions in deep tool loops (#2632). Each session logged that no restart reminder resumes its turn, and both turns were lost. A tool batch consumes the turn input, so the drain found no pending input and built no reminder. This breaks PRD-001 FR-016 (drain, restart, and relaunch the work) and PRD-001 goal 2 (session context survives a restart).

## What Changes

- The drain journals one continuation `InputAdmitted` for an eligible turn that stopped after its tool batch. The input has no text and carries the recorded authority of the turn. Person turns and automation turns use the same rule.
- The existing pending-input path builds the restart reminder and resumes the turn with the next model call over the stored history. The resumed turn consumes the continuation input like any input. The reminder text is not an input.
- The turn is not eligible when text reached the person, another input waits, an overflow replay waits, a background job lives, a tool call has no recorded result, or the turn has no channel type. The person then gets the stop notice.
- A tool that still runs at the coordinator deadline still blocks the reminder, and the session posts no notice (unchanged).

In scope: the restart drain and the restart resume of one session. Out of scope: a new event type, a snapshot field, and a resume of a turn whose tool ran at the deadline.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: the continuation input of a stopped turn, its eligibility, and its resume.

## Impact

- Code: `SessionExecutionLifetime.ContinuesStoppedTurn`, the drain stop paths in `LlmSessionActor`, and the history filter for an input without text.
- Durable data: no shape change. The journal gets one more `InputAdmitted` record with empty content.
- Rollback: an older binary sees a pending input with empty content and resumes it as an ordinary input under the same recorded authority.
- Security: the continuation input carries only the recorded authority of the stopped turn. It never joins the turn of another requester.
- Mutation gate: `stopped-turn-continuation` (10 mutants) in the `session-reliability` group.

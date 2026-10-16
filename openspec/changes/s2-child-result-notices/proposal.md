## Why

A ready child result reached the parent through a second scheduler. The runtime
chose a result at a model boundary, built a synthetic `child_run_result` tool
pair, and ran the turn under the authority of the child. That path broke the
one-authority rule: a turn could change its authority in the middle of its run.
It also hid the result from ordinary input order, and a restart could lose it.

## What Changes

- A child result becomes one admitted notice. The runtime writes the result to
  `result.md` in the run folder, then admits a notice input. The notice names
  the agent, the run, the outcome, and the path. It never holds the child output.
- The model reads `result.md` with a real `file_read` call. The runtime builds no
  synthetic tool pair.
- The notice follows the ordinary input rules: admission order, one requester for
  each turn, and a join only for the same requester. A notice never abandons an
  open approval. The kind of the input decides, not the principal.
- The spawner writes `task.md` (agent, requester, brief) before the child starts.
  It removes the run folder when the owner rejects the start. It refuses a start
  when the requester cannot use `file_read`.
- Recovery ends a lost child, writes `result.md` again for an ended run that has
  no notice, and admits one notice. Passivation waits for such a run. A restart
  with only a child run gets a restart reminder. A restart resume runs the
  leading inputs of one requester and keeps the other inputs waiting.
- **BREAKING (internal):** the states `Prepared`, `Consumed`, and `Closed`, the
  child precedence, the delivery slot, the four wake paths, and the live writes of
  `ChildContinuation` and `ResultPrepared` are deleted. A reported run keeps a
  small summary. Deployed rows stay readable (read-only).

Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017. Decisions S2-1 to S2-7 of the
operator. Out of scope: a write block on `task.md` and `result.md` (issue #2510),
`send_message` (S3), and the approval barrier (S4).

## Capabilities

### New Capabilities

### Modified Capabilities
- `background-subagent-runs`: results reach the parent as notices; the run folder
  holds `task.md` and `result.md`; deployed v2 rows stay readable.
- `tool-authorization`: a child notice is automated input by kind.
- `netclaw-session`: a notice is a runtime-produced input; restart resume runs one requester.
- `session-resume`: a session with only a child run gets a restart reminder.
- `session-state-machine`: passivation waits for an ended run that has no notice.

## Impact

- Code: `LlmSessionActor`, `SessionState`, `SessionInputFlow`, `SessionToolWorkflow`,
  `SessionExecutionLifetime`, `SubAgentSpawner`, `LegacyCoordinationReplay`, the proto mapper.
- Journal: `InputAdmitted` gains field 14. `ss-v2` case 19 gains `notice_input_id`.
  Older binaries cannot read the new rows (back up `netclaw.db` before rollout).
- Docs: `docs/architecture/tool-authorization.md`, the subagent runbook, PRD FR-017, the
  `agent-coordination` skill (version 1.2.0; evals are due).

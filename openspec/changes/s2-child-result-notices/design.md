## Context

The child owner is the session actor. Before this change it kept a prepared result in
the child state, chose it at a model boundary, and delivered a synthetic tool pair under
the authority of the child. See `simplify-agent-coordination` for the earlier design; this
change supersedes its line "Child results use the tool-result path. They do not become
ordinary user inputs."

## Decisions

- **One notice for each ended run.** `SessionState.Apply(InputAdmitted)` marks the run
  `Reported` in the same step. The run state is the guard, not the recent source keys.
- **The notice carries the recorded context of the run owner with a new turn ID.** The
  reducer checks that it has the same authority as the run. The notice grants nothing.
- **Kind decides automation.** `InputAdmitted.ChildNotice` (proto field 14) is durable, so a
  rebuilt waiting notice keeps it. `PlanNewInputAbandonment` treats it as automation.
- **`result.md` is a copy of the terminal event.** The owner writes it off the actor thread
  (temporary file, then rename). The notice states the outcome from the journal. A failed
  write still yields a notice that says the file is missing.
- **`task.md` is written by the wrapper of the spawn delegate**, which holds the requester.
  The spawner removes the run folder only after an explicit rejection
  (`ChildStartRejectedException`) or when the owner knew the start as another run. An unclear
  outcome keeps the folder, because the child may be live.
- **Working context.** A read of a child run file does not enter `recent_files`.
- **Compatibility.** `ChildContinuation` and `ResultPrepared` stay as read-only types. A
  writer that sets them fails at serialization. Snapshot case 17 reads as an ended run with
  the carried pair; case 18 reads as reported; case 19 holds a delivery call or a notice input.

## Risks

- A notice that abandons an approval (mitigated: kind check, Stryker target, specs).
- Double or lost delivery on replay of deployed rows (mitigated: recorded corpus, specs).
- Rollback to the earlier build double-delivers (mitigated: database backup, forward-only rollout).
- A crash between the notice admission and its turn leaves a pending notice that no reminder resumes (same limit as any input).

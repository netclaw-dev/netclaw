The test author writes groups 1 to 3 first, on `feature/durable-child-pr2`, against stub types. Each test compiles
and fails on an assertion. A different agent implements groups 4 to 6. No test uses `Thread.Sleep` or `Task.Delay`.
Each test class that owns a temp folder deletes it. This list replaces tasks 6 to 9 of `durable-child-consent-wait`.

## 1. Stubs and shared test helpers

- [x] 1.1 Add the stub types: `ChildRunEvent.Parked`, `ChildRunEvent.Resumed`, `ChildRunLifecycle.Parked`, `ChildParkRequest`, `BackgroundChildRun.PromptsIssued`, `ChildRunPark.MaxBytes`, the two proto messages and fields 8, 9, 21, and 22 (no mapper), the reason `park_damaged`, and the new `ChildParkRules` functions (`RecoveryAction`, `ParkFits`, `LoopGuardDamage`, `CountsAsUnreported`, `RejectedEventEnds`, `IgnoredBySnapshot`), each with a default result. Verify the solution builds and the PR 1 tests still pass.
- [x] 1.2 In `ChildParkTestBase`, make `RequestsOf` read the requests of the committed `Parked` events, and add one invariant to `AssertRunInvariantsAsync`: each probe run comes after one committed `Resumed`, and the count of `Resumed` is at most the count of `Parked`. Verify the S1 to S4 rows fail on the new invariant and on no compile error.

## 2. Session tests (all rows through `AssertRunInvariantsAsync`)

- [x] 2.1 S1 park and resume: keep the 7 PR 1 rows. Verify they fail only on the new invariant.
- [x] 2.2 S2 refusals: add rows `toolarge` (a park above 4 MiB ends `park_too_large`, no prompt) and `resume-rejected` (the journal rejects `Resumed`: `tool_execution_failed`, "The parked calls did not run.", no actor). Add the row `late-rejection` (a duplicate answer from the stash resumes the run before the rejection of the first answer arrives: the run ends once with the child result). Verify they fail.
- [x] 2.3 S3 cancel: add row `restart` (cancel after a restart; a late click gets the "cancelled" text). Verify it fails.
- [x] 2.4 S4 restart: rename the Theory to `A_restart_keeps_a_durable_park`. Flip `parked` (the idle step passivates; after the restart Alice approves and the call runs once) and `drain` (an answer during the drain; recovery resumes; the call runs once). Add `passivating` (the restart nack; the prompt stays open; the answer after the restart runs once), `compaction` (p1 answered, compaction snapshot and delete, restart, p2 answered: both run once), `crash-answered` (recovery resumes), `crash-resumed` (lost, no second run), `wrong-user` (Bob after the restart: nack, nothing persists), `damaged` (a planted `EqualRounds = 7`: recovery completes; the answer ends `park_damaged`), `counter` (a second park after a restart reuses no prompt call ID), and `late-click` (a click after a resume and a snapshot gets the closed-call reply; nothing runs). Verify each fails.
- [x] 2.5 S5 pause across a restart (new Theory, one helper: each fire runs at most once; no automated turn starts while a request is open): rows `held-fire`, `stashed-fire`, `reminder-held`, `reminder-person`, `notice` (#2617: one new notice lists the open parked requests), and `automation-continuation` (#2651: the restart reminder starts no turn for a paused automation continuation; it resumes once after the last answer). Verify each fails.
- [x] 2.6 Recovery log: add `Recovery_logs_the_count_and_bytes_of_the_parked_runs` and `Recovery_with_no_parks_writes_no_park_line`. Verify they fail.
- [x] 2.7 `RestartDrainSpecs` (#2651 contract): add rows `ConsentNoticeBeforeReminder` (an approval notice closes the continuation in its own write; one restart notice; the later reminder replies `RestartResumeNotNeeded`), `ReplacedFireBeforeReminder` (a fire that replaces a waiting fire closes the waiting fire and the continuation in one write; one restart notice), and `LostChildNoticeBeforeReminder` (the `owner_restarted` notice of a live child closes the continuation; the stopped turn does not run). Verify the first two fail. Record the result of the third: the lost-child notice goes through the ordinary admission path, so it can pass today and pin existing behavior.

## 3. Pure, serialization, and end-to-end tests

- [x] 3.1 `ChildParkRulesTests` tables: `RecoveryAction` (each lifecycle by open count 0 and 1), `ParkFits` (4 MiB accepted; 4 MiB + 1 refused), `LoopGuardDamage` (each bad field, a round below the last checkpoint, one valid row), `CountsAsUnreported` (parked open, parked answered, accepted, terminal, reported), `RejectedEventEnds` (each event kind by each lifecycle), and `IgnoredBySnapshot` (a child entry, a parent entry). Verify they fail.
- [x] 3.2 `ChildRunParkSerializationTests`: `Parked` and `Resumed` round-trip through the Akka serializer with the `cre-v2` manifest; a parked run record round-trips through `SessionSnapshotV2Proto`, with a reflection walk that asserts every park field and list element is non-default before the trip and a structural compare after it; a beta.2 record (no fields 21 and 22) reads as before. Verify they fail.
- [x] 3.3 `ChildParkSqliteRecoveryTests.A_parked_run_survives_a_SQLite_journal_and_snapshot_restart`: the real serializer, journal, and snapshot store; one restart from the journal and one from a snapshot. Verify it fails.

## 4. Durable park (implementation)

- [x] 4.1 Add the proto messages and fields, the mapper entries (the mapper only maps), `ChildRunEvent.Parked` and `Resumed`, `ChildRunLifecycle.Parked`, and `BackgroundChildRun.PromptsIssued`; move `Requests` out of `ChildRunPark` into `ChildParkReached`. Verify task 3.2 passes.
- [x] 4.2 Add the reducer rules of design D3, and apply each answer of a parked run to its park, live and in replay. Verify the S4 `compaction` row passes.
- [x] 4.3 In `AcceptChildPark`, persist one `Parked` with the cap (`ParkFits`, `park_too_large`, the Error and Info size logs) in place of the actor-local park and the `PersistAll` of requests; remove `_parks` and `_promptsIssued`. Verify S1, S2 `toolarge`, and S4 `counter` pass.
- [x] 4.4 In `TryResumeParkedRun`, persist `Resumed` before the spawn; add `LoopGuardDamage` to `PrepareResume` (`park_damaged`); end a rejected `Resumed` with `tool_execution_failed`; replace the PR 1 race guard with `RejectedEventEnds`. Verify S2 `resume-rejected` and `late-rejection` and S4 `damaged` pass.
- [x] 4.5 In `ControlChild`, cancel a run whose lifecycle is `Parked`. Verify S3 passes.

## 5. Recovery, passivation, and the pause (implementation)

- [x] 5.1 In `RecoveryCompleted`, open the entries of each parked run (`OpenParkEntries`) for the journal and the snapshot alike, withdraw only the child entries of runs that are not parked, queue held inputs before stashed commands when the pause holds, and write `child_parks_recovered` when n > 0. Narrow `ExpireRecoveredChildApprovals` to runs that are not parked. Verify S4, S5, and task 2.6 pass.
- [x] 5.2 Make `RecoverChildren` call `RecoveryAction`. Verify S4 `crash-answered` and `crash-resumed` pass.
- [x] 5.3 Make `AllowsSnapshot` call `IgnoredBySnapshot`; drop `_parks.Count > 0` from `HasUnreportedChildren`; filter `UnreportedRuns()` with `CountsAsUnreported`; check that the idle abandon rule ignores child entries. Verify S4 `parked` and `drain` pass.
- [x] 5.4 N1: while `IsChildConsentPaused`, `TryResumeInterruptedInput` leaves out each input that the pause holds and acknowledges with no turn when no other input remains. Verify S5 `reminder-held`, `reminder-person`, and `automation-continuation` pass.
- [x] 5.5 #2651 contract: make the approval-notice path (`AdmitConsentNoticeThen`) and `ReplaceWaitingFire` close a pending continuation that no turn selected, in the same write, through `PersistReplacingInput`, and post the restart notice once. Verify the three task 2.7 rows pass.
- [x] 5.6 Run task 3.3. Verify it passes.

## 6. Docs and gates

- [ ] 6.1 Extend the `// <child-resume-gate>` span in `ChildRunPark.cs` over the new `ChildParkRules` functions, and update the expected counts of `child-resume-gate` and `child-approval-close` in their scripts and in `TOOLING.md`. Verify every mutant is killed and the counts are exact.
- [x] 6.2 Update `docs/architecture/tool-authorization.md` 3.6 and 4 (a parked prompt survives a restart, a snapshot, and passivation; recovery withdraws only prompts of runs that are not parked; no replay) and `docs/spec/GLOSSARY.md` ("parked child run" is durable in the run record). Verify the doc links resolve.
- [ ] 6.3 Update the `netclaw-operations` skill (`references/child-runs.md`: a parked run survives a restart, `child_parks_recovered`, `park_too_large`, `park_damaged`, the downgrade rule) and bump `metadata.version` to dev plus one. Verify `./evals/run-evals.sh` passes.
- [ ] 6.4 Add the release-note text of design "Migration Plan" to the release notes draft, and the `PARK-EVENT` and `PARK-SNAPSHOT` kinds to the swap rule. Verify the text names the backup restore.
- [x] 6.5 Run slopwatch, the header check, and `openspec validate --strict`. Verify all pass.
- [ ] 6.6 After the merge: sync and archive this change and `durable-child-consent-wait`. Verify `openspec validate --strict` passes on the main specs.

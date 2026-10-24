The test author writes groups 1 to 3 first, against stub types. Each test compiles and fails on an assertion. A
different agent implements groups 4 to 6. No test uses `Thread.Sleep` or `Task.Delay`. Each test class that owns a
temp folder deletes it. The tasks use the decisions A1 to A4 of `design.md` as chosen there.

## 1. Stubs and the shared helper

- [x] 1.1 Add the stubs: the constant `SessionExecutionLifetime.ChildWait` (`DaemonConfig.BoundedDrainTimeout - 2 s`; no change to `PrepareForDaemonRestart`), the messages `ParkForDrain` and `RestartChildWaitExpired`, `ChildParkRules.RequesterCanAnswer` (stub: returns `supportsInteractiveApproval`), and `SessionExecutionLifetime.WaitsForChildren` (stub: returns false). Verify the solution builds and the PR 2 tests pass.
- [x] 1.2 In `ChildParkTestBase`, make `ChildActorExistsAsync` check each incarnation name `{name}-{n}` for n from 0 to the prompt counter of the run, so a drain-park resume named `-0` counts. Verify the PR 2 S1 to S5 rows still pass.
- [x] 1.3 Add the shared helper `AssertDrainParkAsync(journal, id, run, expectedRequests)` to `ChildParkTestBase`. Pre-condition after the drain acknowledgement: the acknowledgement arrived before the drain deadline; it carries a restart reminder only when no request of the run is open (PR 2 `CountsAsUnreported`), so a consent park gives no reminder and the answer of the person wakes the session; the journal holds one `Parked` record of the run with `expectedRequests` requests and no `Resumed` record; no child actor of the run is alive. Post-condition after the run ends: `AssertRunInvariantsAsync`; each tool call ID of the run ran at most once over all incarnations; each tool context of the run has the audience, boundary, and delivery targets of `run.OriginalContext`; the first model request of the resumed child equals the transcript of the park.

## 2. Orchestration test (one Theory, every row through the helper)

- [x] 2.1 Add `ChildDrainParkSpecs.A_running_child_parks_at_a_graceful_drain_and_resumes_after_the_restart(string row)` with these rows, and verify each row fails:
  - `batch-end`: the drain starts while a held sibling batch runs; the batch ends; a drain park with 0 requests; the restart reminder wakes the session; the child resumes at round N and completes; the sibling ran once.
  - `model-call`: the drain starts while the child waits for its first model reply; the child parks at once with the task message only; the held reply does not enter the transcript; after the restart the child resumes with a model call and completes.
  - `consent`: the drain starts during a batch with a sibling call and a consent call; one consent park with 1 request and no second park; after the restart Alice approves; the consent call runs once.
  - `automation`: a reminder turn with no interactive approval starts the child; the drain park records, the run does not end, and no prompt shows; the child resumes after the restart and completes.
  - `missed-wait`: the sibling stays held past the child wait (the spec advances the `TestScheduler` by `ChildWait`); one Warning names 1 live child (its text ends with `child_wait_ended live_children=1`); the acknowledgement carries the reminder before the drain deadline; after the restart the run is lost with `owner_restarted`; the sibling ran once. The pre-condition of this row expects no `Parked` record.
  - `other-requester`: Bob writes after the restart, and his turn is open when the resumed child runs its next call. The order of Bob's message and the resume is not fixed, so the row checks this: the resumed child's first model call, and each tool call after it, run under Alice's recorded audience, boundary, and delivery targets.
  - `crash-resumed`: the session stops after the `Resumed` record commits and before the child ends; the next recovery records the run as lost and starts no child.
- [x] 2.2 Move three older drain tests to the drain park contract, because a drain now parks a child that waits at its model call:
  - `ChildNoticeRecoverySpecs.Child_only_session_gets_a_restart_resume_and_reports_the_child` (renamed from `..._reports_the_lost_child`): the resumed child reports its own output, and the result has no `owner_restarted`.
  - `ChildNoticeRestartResumeSpecs.Restart_after_streamed_text_still_wakes_a_session_that_owes_a_child_result`: the same change.
  - `LostChildRestartReasonSpecs.A_child_lost_to_a_restart_names_owner_restarted_in_its_result_file` (renamed from `A_child_lost_to_a_restart_drain_...`): the session stops with no drain, because only a stop with no drain or a missed child wait loses a child.

## 3. Pure decision tables (named rows)

- [x] 3.1 `ChildParkRulesTests.A_park_needs_a_requester_who_can_answer_only_when_it_asks` (`RequesterCanAnswer`): rows `drain-park-no-approval` (0, false → true), `drain-park-approval` (0, true → true), `consent-park-approval` (1, true → true), `consent-park-no-approval` (1, false → false). Verify the first row fails.
- [x] 3.2 `SessionExecutionLifetimeTests.The_drain_acknowledgement_waits_only_for_live_children_inside_the_wait` (`WaitsForChildren`): rows `no-drain` (false, true, false → false), `live-child` (true, true, false → true), `no-live-child` (true, false, false → false), `wait-ended` (true, true, true → false). Verify the `live-child` row fails.
- [x] 3.3 New `ChildRunReducerTests.A_park_and_its_resume_follow_the_request_count` (`SessionState.Apply`): rows `drain-park` (Accepted + Parked with 0 requests → Parked; the prompt counter does not change), `drain-resume` (Parked with 0 requests + Resumed → Accepted at round N with "The child resumed after the restart."), `consent-resume` (Parked with 1 answered request + Resumed → "The child resumed after consent."), `consent-open` (Parked with 1 open request + Resumed → throws), `foreign-request` (Parked with a request of another run → throws). Verify `drain-park` and `drain-resume` fail.
- [x] 3.4 `ChildRunParkSerializationTests`: add the row `zero-requests` (a `Parked` event and a parked run record with no request round-trip through the `cre-v2` manifest and the snapshot). Verify it passes or fails, and record the result: the mapper can already hold it. Result: both `zero-requests` rows pass with no mapper change.
- [x] 3.5 Keep these PR 2 rows unchanged, because they already decide the drain park: `ResumeGate` `(true, 0, true) → Wait` and `(true, 0, false) → Resume`; `CountsAsUnreported` "parked answered"; `RecoveryAction` `Parked → Resume`. Verify they pass.

## 4. Session side (implementation)

- [x] 4.1 Make the default of `DaemonRestartCoordinator.RestartDrainTimeout` read `DaemonConfig.BoundedDrainTimeout`, so both drains share the value that `SessionExecutionLifetime.ChildWait` uses. Verify the daemon drain tests pass.
- [x] 4.2 `SessionExecutionLifetime.RequestRestart` sends `ParkForDrain` to each live child and starts `RestartChildWaitExpired` when a live child exists; implement `WaitsForChildren`. Verify task 3.2 passes.
- [x] 4.3 `FinalizePassivation` starts with the `WaitsForChildren` gate. Add `ContinueDrainAfterChild()` to the journal callbacks of `Parked` and `TerminalRecorded`. Handle `RestartChildWaitExpired` in every phase: set the flag, log one Warning with the live child count (text: `Restart drain child_wait_ended live_children={count}`), and finalize when `Passivating`. Verify rows `batch-end` (pre-condition) and `missed-wait` pass.
- [x] 4.4 Implement `RequesterCanAnswer` inside the `child-resume-gate` span and call it in `AcceptChildPark`; a damaged authority still ends the run. Verify task 3.1 and row `automation` (pre-condition) pass.
- [x] 4.5 Drop the `Requests.Count > 0` guard of the `Parked` reducer rule, and pick the `Resumed` summary text by the request count. Verify task 3.3 passes.

## 5. Child side (implementation)

- [x] 5.1 `SubAgentActor`: handle `ParkForDrain` in `Processing` (set `_parkForDrain`; park at once with `Park([], current directories)` when no tool batch runs); add the "tool batch runs" bool; make `FireLlmCall` park first when `_parkForDrain` is set. Verify rows `batch-end`, `model-call`, and `consent` pass their pre-conditions.
- [x] 5.2 `ResumeFromPark`: when `Answers` is empty, call `FireLlmCall()` before the parked-call lookup. Verify every row of task 2.1 passes.

## 6. Docs and gates

- [x] 6.1 Update the expected mutant count of `child-resume-gate` in its script and in `TOOLING.md`. Verify every mutant of `RequesterCanAnswer` is killed and the count is exact.
- [x] 6.2 Update `docs/architecture/tool-authorization.md` 3.6 (a drain park asks nobody and needs no requester who can answer; a consent park still needs one) and `docs/spec/GLOSSARY.md` "Parked child run" (a parked run can hold zero consent requests after a graceful drain). Verify the doc links resolve.
- [ ] 6.3 Update the `netclaw-operations` skill (`references/child-runs.md`: a running child survives a graceful restart; the child wait and its Warning; a missed wait gives `owner_restarted`) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [x] 6.4 Run slopwatch, the header check, and `openspec validate running-child-drain-park --strict`. Verify all pass.
- [ ] 6.5 After the merge: sync and archive this change after `durable-child-park-recovery`. Verify `openspec validate --strict` passes on the main specs.

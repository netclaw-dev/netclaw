## 1. Contract and reference proof

- [x] 1.1 Obtain root review of the exact contract; verify every policy choice has an explicit disposition before production edits.
- [x] 1.2 Reconcile the prior active draft through OpenSpec; verify completed repairs remain preserved and unfinished proof gates remain mandatory.
- [ ] 1.3 Extend the disposable pure detector laboratory; verify all fifteen prior cases and the new nonadjacent controls pass.
- [x] 1.4 Run at least 10,000 fixed-seed checks and one million deterministic fuzz sequences; verify reproducible outcomes and sensitivity controls.
- [ ] 1.5 Produce private replay and shadow evidence without tool execution; verify every proposed block receives independent review.
- [ ] 1.6 Assess the 3,000-turn independent productive holdout; verify zero confirmed hard false blocks and record absent data explicitly.

## 2. Shared recurrence decisions

- [x] 2.1 Extend prepared call signatures with grouped outcomes; verify duplicate count changes cannot clear recurrence and distinct outcomes remain visible.
- [x] 2.2 Add per-call recurrence to the existing tracker; verify correction after two equal completed rounds and terminal stop after corrective feedback.
- [x] 2.3 Preserve the adjacent guard and define precedence; verify parent and child decisions agree on the same deterministic corpus.
- [x] 2.4 Add the cold-key LRU and pinned-state rules; verify cold eviction limits detection and never clears established suspicion.
- [x] 2.5 Repair canonical receipt producers and remove success substitution; verify a missing-receipt defect preserves results and settles without another model request.

## 3. Explicit repeat facts

- [x] 3.1 Carry accessible Pending or Running job facts through the existing receipt path; verify terminal and inaccessible responses supply no exception.
- [x] 3.2 Bind the exception to a valid noncancel prepared identity; verify other identities, invalid metadata, denials, and cancellation cannot inherit it.
- [x] 3.3 Apply the exception before both guards; verify authorized rechecks proceed without elapsed-text normalization or a new poll interval.

## 4. Parent persistence and recovery

- [x] 4.1 Add framework-safe admission, result, and checkpoint metadata; verify round-trip serialization and private digest exclusion from diagnostics.
- [x] 4.2 Preserve retained state in session events and snapshots; verify snapshot recovery and journal replay produce equal decisions.
- [x] 4.3 Add durable task adoption from canonical admitted context; verify old attempts retain authority and fresh consumption resets before continuation.
- [ ] 4.4 Reconstruct the suffix after the last checkpoint; verify every admission, result, and checkpoint crash cut point preserves completed effects once.
- [ ] 4.5 Inject checkpoint and snapshot failures; verify an explicit error and no silent detector reset or premature dispatch.
- [x] 4.6 Load captured pre-change records; verify conversation recovery and an explicit detector baseline gap without fabricated receipt evidence.
- [ ] 4.7 Test the rollback reader or document database restoration; verify new event variants cannot silently corrupt an older runtime.

- [x] 4.8 Persist canonical job origin and checkpoint on existing job records; verify the commit, task-switch, delivery, and removal order.
- [x] 4.9 Verify fast completion, launch/receipt crash, duplicate delivery, two jobs, invalid origin, and explicit legacy evidence gaps.
- [x] 4.10 Commit the canonical legacy approval baseline before redrive; verify replay, snapshot, duplicate boundaries, and completed sibling preservation.

## 5. Actor integration and terminal settlement

- [x] 5.1 Prepare all group decisions before dispatch; verify a terminal decision prevents every call from that response.
- [x] 5.2 Preserve call-result pairs for mixed batches; verify refused members do not execute and unrelated eligible members still execute.
- [x] 5.3 Preserve approval redrive correlation; verify one feedback observation and no duplicate execution or prompt authority.
- [x] 5.4 Replace final model requests with framework settlement; verify one factual terminal parent result and one Partial child result.
- [x] 5.5 Retain confirmed file activity and bounded partial evidence; verify a model claim cannot make a refused operation successful.
- [ ] 5.6 Ignore late model or tool replies after settlement; verify no renewed dispatch, grant, or duplicated terminal outcome.

## 6. Limit removal and operational contracts

- [x] 6.1 Remove the parent configuration key and schema entry; verify legacy validation fails and doctor removes only that key.
- [x] 6.2 Remove the child ceiling and constructor parameter; verify every caller uses the new mandatory contract.
- [x] 6.3 Retain telemetry and operation guards; verify no count-based stop and preserved timeout, cancellation, and empty-response behavior.
- [x] 6.4 Drive useful parent and child tasks past the former ceilings; verify independent artifact contents rather than model success claims.
- [x] 6.5 Update configuration docs and the operations skill version; verify guidance explains exact recurrence, limits, and pending-status exceptions.

## 7. Adversarial proof and targeted evals

- [x] 7.1 Integrate the independent test agent's assertions; verify each actual defect receives its failing test before the production fix.
- [x] 7.2 Run named fault variants for reset, duplicate count, forged receipt, missing evidence, terminal model dependence, and scope; verify behavioral rejection.
- [x] 7.3 Run focused Stryker gates for changed dispatch boundaries; verify expected mutants run and fail within the documented CI cost budget.
- [ ] 7.4 Run only necessary tool-cycle and relevant subagent model cases at the approved target; verify strict independent assertions and retained raw trial evidence.
- [ ] 7.5 Review every model failure; verify parser, prompt, capability, and infrastructure causes receive separate dispositions.
- [ ] 7.6 Reconcile laboratory, replay, shadow, holdout, actor, and model evidence; verify every activation gate passes before merge approval.

## 8. Quality and documentation

- [x] 8.1 Run relevant .NET projects sequentially; verify restore, test discovery, and pass counts without shared output locks.
- [x] 8.2 Run Slopwatch and header checks; verify no new violation or missing copyright header.
- [ ] 8.3 Inspect changed complex methods with focused coverage analysis; verify no critical untested branch remains.
- [x] 8.4 Validate the OpenSpec change strictly and inspect the full diff; verify no private replay payload or operational target enters tracked metadata.
- [ ] 8.5 Use OpenSpec verification and sync skills; verify implementation, canonical specifications, and completed tasks agree before closure.

## Local proof scope

- `ToolRecurrenceContractTests` proves the million-sequence corpus, 10,000 checkpoint sequences, outcome parity, and retention rules.
- `ToolRecurrenceAdversarialTests` proves parent/child parity, exact effects, paired results, useful long tasks, and missing-receipt settlement.
- `ToolLoopReplayAdversarialTests` proves result-cut reconstruction, canonical adoption, receipt defects, and duplicate reset rejection.
- `ToolTaskRecoveryAdversarialTests` proves actor recovery, job lineage, fast results, invalid deliveries, and legacy approval redrive.
- `BackgroundJobLineageTests` proves two-job checkpoint retention, origin rejection, durable reports, and explicit legacy gaps.
- `PreChangePersistenceCompatibilityTests` proves actual legacy journal bytes and registered-serializer snapshot bytes through actor recovery.
- Task 4.10 includes duplicate redrive and the specific post-baseline serialized snapshot case with seeded actor recovery.
- That snapshot case does not claim a live compaction save. The separate compaction actor cases prove live compaction behavior.
- `ToolCompactionReplayAdversarialTests` proves original requester, exact input IDs, ordered effects, paired results, and result-before-adoption journal order.
- `ToolCycleUserInputTests` proves fresh-task reset at the existing batch boundary and preserved evidence without fresh input.
- The scoped mutation gate selected 12 tests and killed its four expected targets. The CI gate for the current head remains separate.
- The full non-native actor run passed 5,796 cases and found one fixture failure. Its 40 existing skips remain explicit.
- The repaired input class passed all seven cases. The independent current-candidate run remains separate.
- Configuration tests passed six cases. Doctor tests passed 37 cases. This repair changes no configuration or CLI code.
- Open tasks retain the laboratory, private replay, shadow, productive holdout, exhaustive crash, injected persistence failure, and late-reply gates.
- Open tasks also retain executed rollback, model trials, coverage analysis, hosted CI, and final contract reconciliation.

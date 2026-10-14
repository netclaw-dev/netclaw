Use the [canonical plan](../../../.systematize/plans/coordination-replacement/plan.html) for slice boundaries and the next-slice recipes. Do not duplicate those recipes here.

## 1. PR 1: Durable child runs and parent results

- [x] 1.1 Add a compatibility reader or converter for `tta-v1`, `cra-v1`, `cre-v1`, extended records, snapshots, and the invalid-adoption case; verify fixed deployed bytes, safe dispositions, replay, and restore.
- [x] 1.2 Implement validated durable child acceptance, idempotent start, cancellation, approval correlation, status, and recovery through the current session owners; verify actor-level positive, failure, and restart cases.
- [x] 1.3 Deliver prepared results at the next safe boundary without child-finish ordering; verify ordinary input order, model-call and tool-batch safety, result consumption, and no child user-input admission.

## 2. PR 2: Recurrence rules

- [x] 2.1 Apply exact per-call recurrence, adjacent-cycle checks, and the trusted pending-job exception through the existing recurrence owner; verify generated operation sequences and productive changes.
- [x] 2.2 Remove obsolete limits only when recurrence proof covers their guarantees; verify terminal settlement, correction results, and no extra model request.

## 3. PR 3: Skills and behavioral proof

- [x] 3.1 Retain the coordination skill and task-worker profile; update logical-resource guidance, operator docs, and system skill versions; verify safe install preserves existing operator files.
- [x] 3.2 Add the focused spill hint only for retained output that the child can read; verify available, missing, and denied references.
- [ ] 3.3 Add three coordination eval cases and objective negative controls; verify each new case, existing skill eval gates, and the repository's full required eval command.
- [ ] 3.4 Run a real-provider parent-responsiveness check before rollout; verify the parent can answer while a child remains active.

Use the [canonical plan](../../../.systematize/plans/coordination-replacement/plan.html) for slice boundaries and the next-slice recipes. Do not duplicate those recipes here.

## 1. PR 1: Durable child runs and parent results

- [x] 1.1 Add a compatibility reader or converter for `tta-v1`, `cra-v1`, `cre-v1`, extended records, snapshots, and the invalid-adoption case; verify fixed deployed bytes, safe dispositions, replay, and restore.
- [x] 1.2 Implement validated durable child acceptance, idempotent start, cancellation, approval correlation, status, and recovery through the current session owners; verify actor-level positive, failure, and restart cases.
- [x] 1.3 Deliver prepared results at the next safe boundary without child-finish ordering; verify ordinary input order, model-call and tool-batch safety, result consumption, and no child user-input admission.

- [x] 1.4 Report the open child runs in every `TurnCompleted` through one helper at all five emit sites, and map `openChildRunIds` in `SessionOutputDtoMapper` in both directions; verify `OpenChildRunsOnTurnCompletedTests` and the mapper round trip, with the field absent from an earlier daemon.
- [x] 1.5 Emit one `ToolCallOutput` and one `ToolResultOutput` for each delivered `child_run_result` pair, once per delivery call ID; verify the subscriber sees the child text.
- [x] 1.6 Make `HeadlessChannel` end a run on the first completed turn that reports no open child run, print `[subagent:waiting]`, and write the `[headless:open-runs]` line on every failure and stop path; verify `HeadlessAsyncChildEndToEndTests` in text and `--json` mode.
- [x] 1.7 Write one JSON envelope per run with `turnResponses`, a summed `usage`, and all `toolCalls`; verify the success, failed-turn, and no-child cases.
- [x] 1.8 Forward `SessionDeactivated` through the hub (`session_deactivated`) so a daemon restart ends a waiting headless run with exit `1`; verify the mapper round trip and the end-to-end deactivation case.
- [x] 1.9 Add the two `spawn_agent` description sentences; update the `netclaw-operations` skill (the `chat -p` wait, `[subagent:waiting]`, `[headless:open-runs]`) and bump its `metadata.version`; update `docs/runbooks/subagents.md` (resume a session after a stopped run); run the eval suite for the description change.
- [x] 1.10 Write the headless stop decision as `EndsRun(TurnCompleted)` in the form that `TOOLING.md` shows, between `// <headless-stop-rule>` and `// </headless-stop-rule>` comments; run `./scripts/run-headless-stop-rule-mutations.sh`, which expects three killed mutants.

## 2. PR 2: Recurrence rules

- [x] 2.1 Apply exact per-call recurrence, adjacent-cycle checks, and the trusted pending-job exception through the existing recurrence owner; verify generated operation sequences and productive changes.
- [x] 2.2 Remove obsolete limits only when recurrence proof covers their guarantees; verify terminal settlement, correction results, and no extra model request.

## 3. PR 3: Skills and behavioral proof

- [x] 3.1 Retain the coordination skill and task-worker profile; update logical-resource guidance, operator docs, and system skill versions; verify safe install preserves existing operator files.
- [x] 3.2 Add the focused spill hint only for retained output that the child can read; verify available, missing, and denied references.
- [ ] 3.3 Add three coordination eval cases and objective negative controls; verify each new case, existing skill eval gates, and the repository's full required eval command.
- [ ] 3.4 Run a real-provider parent-responsiveness check before rollout; verify the parent can answer while a child remains active.

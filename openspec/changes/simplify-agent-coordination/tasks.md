Use the [canonical plan](../../../.systematize/plans/coordination-replacement/plan.html) for slice boundaries and the next-slice recipes. Do not duplicate those recipes here.

## 1. PR 1: Durable child runs and parent results

- [ ] 1.1 Add a compatibility reader or converter for `tta-v1`, `cra-v1`, `cre-v1`, extended records, snapshots, and the invalid-adoption case; verify fixed deployed bytes, safe dispositions, replay, and restore.
- [ ] 1.2 Implement validated durable child acceptance, idempotent start, cancellation, approval correlation, status, and recovery through the current session owners; verify actor-level positive, failure, and restart cases.
- [ ] 1.3 Deliver prepared results at the next safe boundary without child-finish ordering; verify ordinary input order, model-call and tool-batch safety, result consumption, and no child user-input admission.

- [x] 1.4 Report the open child runs in every `TurnCompleted` through one helper at all five emit sites, and map `openChildRunIds` in `SessionOutputDtoMapper` in both directions; verify `OpenChildRunsOnTurnCompletedTests` and the mapper round trip, with the field absent from an earlier daemon.
- [x] 1.5 Emit one `ToolCallOutput` and one `ToolResultOutput` for each delivered `child_run_result` pair, once per delivery call ID; verify the subscriber sees the child text.
- [x] 1.6 Make `HeadlessChannel` end a run on the first completed turn that reports no open child run, print `[subagent:waiting]`, and write the `[headless:open-runs]` line on every failure and stop path; verify `HeadlessAsyncChildEndToEndTests` in text and `--json` mode.
- [x] 1.7 Write one JSON envelope per run with `turnResponses`, a summed `usage`, and all `toolCalls`; verify the success, failed-turn, and no-child cases.
- [x] 1.8 Forward `SessionDeactivated` through the hub (`session_deactivated`) so a daemon restart ends a waiting headless run with exit `1`; verify the mapper round trip and the end-to-end deactivation case.
- [x] 1.9 Add the two `spawn_agent` description sentences; update the `netclaw-operations` skill (the `chat -p` wait, `[subagent:waiting]`, `[headless:open-runs]`) and bump its `metadata.version`; update `docs/runbooks/subagents.md` (resume a session after a stopped run); run the eval suite for the description change.
- [x] 1.10 Write the headless stop decision as `EndsRun(TurnCompleted)` in the form that `TOOLING.md` shows, between `// <headless-stop-rule>` and `// </headless-stop-rule>` comments; run `./scripts/run-headless-stop-rule-mutations.sh`, which expects three killed mutants.
- [x] 1.11 End a headless run loudly on a lost connection: in `HeadlessChannel`, treat the `Disconnected` and `TransportClosed` connection states after the prompt was sent as a channel failure (one `[headless:error]` line, `kind=ChannelFailure`, the `[headless:open-runs]` line when a run is known, exit `1`, no JSON envelope). Ignore both states before the prompt was sent and after the run ended, and write no log record after the end. Set the exit code under the same lock as the ended mark. Verify `HeadlessWaitForChildrenTests` (disconnect and transport close during the wait, disconnect before the first turn, transport close after the prompt with no open run, transport close before the prompt, disconnect and transport close after the final turn, a bind failure that names its own reason, a connection state after the end).
- [x] 1.12 Make `TurnCompleted.OpenChildRunIds` a `required` property (or build every `TurnCompleted` through one helper) so that no site can skip it; the five sites in `LlmSessionActor` and `SessionOutputDtoMapper.FromDto` name the value. Verify `OpenChildRunsRejectedSlashCommandTests`, `OpenChildRunsRoutedSkillTurnTests`, and the `required` check in `OpenChildRunsOnTurnCompletedTests`.
- [x] 1.13 Add a focused mutation gate for `SessionState.GetOpenChildRunIds`: markers around the method, a script `./scripts/run-open-child-runs-mutations.sh`, the `TOOLING.md` section, and the `mutation-gates` group. `OpenChildRunsMutationTests` in `Netclaw.Actors.MutationTests` must kill every mutant of the span.
- [x] 1.14 Correct the false claims: SPEC-004 and the subagents runbook ("The run never hangs", "A daemon restart ... ends the run" is true only for a drained restart), the `TOOLING.md` missing-field claim, the JSON usage rate rule (the last turn that has a usage record), and the stale "received by the calling tool" scenario; document that an earlier CLI against a new daemon exits at turn 1.
- [x] 1.15 Parse the headless stdout and stderr lines of `HeadlessAsyncChildEndToEndTests` on Windows line endings (`\r\n`).
- [x] 1.16 Fix the `JsonUsage.Sum` code comment so that it says "the last turn that has a usage record" (the code takes `turns[^1]` of the turns with usage).

## 2. PR 2: Recurrence rules

- [ ] 2.1 Apply exact per-call recurrence, adjacent-cycle checks, and the trusted pending-job exception through the existing recurrence owner; verify generated operation sequences and productive changes.
- [ ] 2.2 Remove obsolete limits only when recurrence proof covers their guarantees; verify terminal settlement, correction results, and no extra model request.

## 3. PR 3: Skills and behavioral proof

- [ ] 3.1 Retain the coordination skill and task-worker profile; update logical-resource guidance, operator docs, and system skill versions; verify safe install preserves existing operator files.
- [ ] 3.2 Add the focused spill hint only for retained output that the child can read; verify available, missing, and denied references.
- [ ] 3.3 Add three coordination eval cases and objective negative controls; verify each new case, existing skill eval gates, and the repository's full required eval command.
- [ ] 3.4 Run a real-provider parent-responsiveness check before rollout; verify the parent can answer while a child remains active.

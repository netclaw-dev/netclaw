## 1. Tests and docs (test-author commit)

- [x] 1.1 Change the `TurnStateTracker` and `ToolCycleValidationTests` unit tests: a repeat after correction is `Correct` with the repeated call IDs refused, a new sibling is not refused, and a valid `_background: true` changes the identity. Verify that they fail on `dev`.
- [x] 1.2 Replace the parent session stop tests in `LlmSessionIntegrationTests` with refusal, nudge, and continuation tests. Verify that they fail on `dev`.
- [x] 1.3 Replace the user-input stop tests in `ToolCycleUserInputTests` with continuation tests. Verify that they fail on `dev`.
- [x] 1.4 Replace the child stop test in `SubAgentActorTests` with a child nudge test. Verify that it fails on `dev`.
- [x] 1.5 Replace the headless model-failure test with a headless success test: exit code 0, no `[turn:failed]`, and only the model text. Verify that it fails on `dev`.
- [x] 1.6 Update `docs/spec/SPEC-004-cli-contract.md` and `docs/runbooks/subagents.md`. Verify that no doc names the terminal cycle stop.
- [x] 1.7 Rework the eval case `tool_cycle_terminal` into `tool_cycle_repeat_nudge` and add `long_command_background_job`. Verify with `python3 -m unittest discover -s evals -p 'test_*.py'`.

## 2. Runtime (implementer commit)

- [x] 2.1 Remove the `Stop` decision kind and add the repeated call set in `TurnStateTracker`. Verify with the tracker unit tests.
- [x] 2.2 Add the background field to the call identity in `ToolCycleSignatureFactory.Prepare`. Verify with `ToolCycleValidationTests`.
- [x] 2.3 Add the parent nudge after the batch results through `ActiveToolBatchTracker`, and remove `FailCurrentTurn` for this case. Verify with the session and headless tests.
- [x] 2.4 Add the child nudge in `SubAgentActor` and remove the partial stop. Verify with `SubAgentActorTests`.
- [x] 2.5 Remove `ToolCycleMessages.TurnStopped` and `ToolCycleMessages.ChildStopped`, and update the `ErrorCategory.ModelBehavior` comment. Verify that the solution builds.

## 3. Skill and gates

- [x] 3.1 Add `references/long-running-commands.md` to `netclaw-operations`, move the background job details to it, add the routing row, and bump `metadata.version`. Verify that the nudge names the page path.
- [x] 3.2 Run `Netclaw.Actors.Tests`, `Netclaw.Actors.MutationTests`, `Netclaw.Cli.Tests`, `Netclaw.Daemon.Tests`, the covering Stryker gate, slopwatch, and the header check. Verify that all pass.

## 4. Escalation and quiet end (second review round)

- [x] 4.1 Add tests: the escalated nudge at refused repeat 5, the quiet end at refused repeat 6 (session, child, and headless text and JSON), the count reset for new and joining person input, `_background` identity only for `shell_execute`, one repeat nudge in history, and the eval timeout margin. Verify that they fail on f7d9f9327.
- [x] 4.2 Correct the bound text in the spec delta, `design.md`, and `docs/runbooks/subagents.md`, and flag the three open changes that keep the stop rule.
- [ ] 4.3 Add the refused repeat count and the `RefusedRepeat` decision to `TurnStateTracker`. Verify with the tracker unit tests.
- [ ] 4.4 Add the escalated nudge and the quiet end to `LlmSessionActor` and `SubAgentActor`, and keep only the latest repeat nudge in history. Verify with the session, child, and headless tests.
- [ ] 4.5 Add the background field only for `shell_execute`. Verify with `ToolCycleValidationTests`.
- [ ] 4.6 Update the skill page and `SKILL.md`, restore the dropped job details, and set `metadata.version` to `2.116.0`.
- [ ] 4.7 Run the gates again.

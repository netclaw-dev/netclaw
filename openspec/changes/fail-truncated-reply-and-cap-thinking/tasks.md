## 1. Stream classification and thinking cap

- [x] 1.1 Classify each stream update as Keepalive, Thinking, or Answer; empty or whitespace-only text is Thinking. Verify with the `StreamProgress` and `ProcessingWatchdogTests` classify cases.
- [x] 1.2 Arm the cap timer in the shared watchdog at three times the no-progress deadline; only Answer restarts it. Verify with the four cap cases in `ProcessingWatchdogTests`.
- [x] 1.3 Session: end the turn with a `ModelBehavior` error that names thinking when the cap fires. Verify with `LlmSessionThinkingCapTests` and `LlmSessionEmptyTextCapTests`.
- [x] 1.4 Sub-agent: fail with reason `thinking_only_timeout` when the cap fires. Verify with `SubAgentThinkingCapTests`.
- [x] 1.5 Sub-agent: throttle pings with the gate and keep the answer-at-once rule. Verify with `StreamPingGateTests`.

## 2. Truncated reply

- [x] 2.1 Session: end the turn with a `ModelBehavior` truncation error and do not post or store the cut-off text. Verify with `Text_reply_cut_off_by_length_fails_the_turn_and_is_not_posted_as_the_answer` in `EmptyResponseEscalationTests`.
- [x] 2.2 Sub-agent: fail with reason `output_truncated`, make no retry, and keep a 300-character preview. Verify with `Text_reply_cut_off_by_length_fails_with_output_truncated_reason` in `SubAgentActorTests`.
- [x] 2.3 Redact the whole stub, then cut on a text-element boundary. Verify with `SubAgentTruncationPreviewTests` (secret at character 295, emoji at character 300).
- [x] 2.4 Keep the tool-call-with-length path and the thinking-only-with-length retry. Verify with the control cases in `SubAgentActorTests` and `EmptyResponseEscalationTests`.
- [x] 2.5 Report a cut-off session reply as `category=model kind=ModelBehavior` in headless output. Verify with `Text_reply_cut_off_at_the_output_limit_is_a_model_failure` in `HeadlessTurnErrorCategoryEndToEndTests`.

## 3. Documentation and guidance

- [x] 3.1 Describe the cap and the two reasons for the sub-agent in `docs/runbooks/subagents.md` and SPEC-016. Verify by reading both files.
- [x] 3.2 Add the session path (`ModelBehavior` error for the cap and for the truncation) to `docs/runbooks/subagents.md`. Verify by reading the file.
- [x] 3.3 Update `feeds/skills/.system/files/subagent-authoring/SKILL.md` (no-progress paragraph, cap, two reasons) and bump `metadata.version` to 1.6.0. Verify by reading the file.
- [ ] 3.4 Run `./evals/run-evals.sh` for the skill change and verify that it passes.

## 4. Verification and follow-up

- [x] 4.1 Run `dotnet test src/Netclaw.Actors.Tests -c Release`, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Result after the rebase on `dev`: 6232 passed, 1 failed (`OneShotSettlementTests`, a reminder test outside this change), 41 skipped; 0 slopwatch issues; all headers present.
- [x] 4.2 Run `dotnet test src/Netclaw.Daemon.Tests -c Release` and verify 0 failures. Result: 1447 passed, 0 failed, 39 skipped.
- [ ] 4.3 Open a follow-up issue for the compaction summary and the session memory observer, which accept a `length` reply. Verify the issue link is in the PR text.
- [ ] 4.4 Run `/opsx-verify`, `/opsx-sync`, and `/opsx-archive` after the code merges.

## 1. Stream classification and thinking cap

- [x] 1.1 Classify each stream update as Keepalive, Thinking, or Answer; empty or whitespace-only text is Thinking. Verify with the `StreamProgress` and `ProcessingWatchdogTests` classify cases.
- [x] 1.2 Arm the cap timer in the shared watchdog at three times the no-progress deadline; only Answer restarts it. Verify with the four cap cases in `ProcessingWatchdogTests`.
- [x] 1.3 Session: end the turn with a Timeout error that names thinking when the cap fires. Verify with `LlmSessionThinkingCapTests` and `LlmSessionEmptyTextCapTests`.
- [x] 1.4 Sub-agent: fail with reason `thinking_only_timeout` when the cap fires. Verify with `SubAgentThinkingCapTests`.
- [x] 1.5 Sub-agent: throttle pings with the gate and keep the answer-at-once rule. Verify with `StreamPingGateTests`.

## 2. Truncated reply

- [x] 2.1 Session: end the turn with a truncation error and do not post or store the cut-off text. Verify with `Text_reply_cut_off_by_length_fails_the_turn_and_is_not_posted_as_the_answer` in `EmptyResponseEscalationTests`.
- [x] 2.2 Sub-agent: fail with reason `output_truncated`, make no retry, and keep a 300-character preview. Verify with `Text_reply_cut_off_by_length_fails_with_output_truncated_reason` in `SubAgentActorTests`.
- [x] 2.3 Redact the whole stub, then cut on a text-element boundary. Verify with `SubAgentTruncationPreviewTests` (secret at character 295, emoji at character 300).
- [x] 2.4 Keep the tool-call-with-length path and the thinking-only-with-length retry. Verify with the control cases in `SubAgentActorTests` and `EmptyResponseEscalationTests`.

## 3. Documentation and guidance

- [x] 3.1 Describe the cap and the two reasons for the sub-agent in `docs/runbooks/subagents.md` and SPEC-016. Verify by reading both files.
- [ ] 3.2 Add the session path (Timeout error for the cap, truncation error) to `docs/runbooks/subagents.md`. Verify by reading the file.
- [ ] 3.3 Update `feeds/skills/.system/files/subagent-authoring/SKILL.md` (no-progress paragraph, cap, two reasons), bump `metadata.version`, and run `./evals/run-evals.sh`. Land after the stacked D4 change merges.

## 4. Verification and follow-up

- [x] 4.1 Run `dotnet test src/Netclaw.Actors.Tests -c Release`, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`. Result: 5825 passed, 0 failed, 0 slopwatch issues.
- [ ] 4.2 Run `dotnet test src/Netclaw.Daemon.Tests -c Release` and verify 0 failures.
- [ ] 4.3 Open a follow-up issue for the compaction summary and the session memory observer, which accept a `length` reply. Verify the issue link is in the PR text.
- [ ] 4.4 Run `/opsx-verify`, `/opsx-sync`, and `/opsx-archive` after the code merges.

## 1. Spill in a session with no workspace folder

- [x] 1.1 Create the session workspace folder before the spill write, and refuse a link at that path. Verify: `ToolOutputSpillTests.Missing_session_folder_is_created_so_the_first_large_result_gets_a_continuation` and `Spill_rejects_a_dangling_link_at_the_session_folder_path` pass.
- [x] 1.2 Run the real dispatcher on a session folder that does not exist for `skill_load`, `skill_read_resource`, and one other tool. Verify: `ToolOutputSpillFreshSessionTests` passes, and its three call-ID tests fail when the folder creation is removed.

## 2. No silent fallback

- [x] 2.1 State in the result text that Netclaw did not keep the full output, and log `tool_output_spill_not_retained` with a reason. Verify: `ToolOutputSpillTests.A_spill_that_cannot_be_written_says_so_and_logs_a_warning` passes.
- [x] 2.2 Keep the audience profile as the owner of `tool_output_read` access. Verify: `ToolOutputSpillFreshSessionTests.An_audience_without_the_continuation_tool_cannot_read_the_spill` passes.

## 3. Guidance and evals

- [x] 3.1 Correct "Large tool output" in the `netclaw-operations` skill (default budget, each tool) and bump its version. Verify: the text matches `SessionTuning.MaxInlineToolResultChars` and `ShellTool.InlineOutputBudgetChars`.
- [x] 3.2 Add the eval case `complex_skill_spill_steer_single_turn` and its fixture skill. Verify: `python3 -m unittest discover -s evals -p test_spill_steer_evals.py` passes.
- [ ] 3.3 Run the eval case on `dev` and on the branch, three runs each, and record the table in the pull request.

## 4. Spec

- [ ] 4.1 Sync this delta into `openspec/specs/bounded-tool-output/spec.md` and archive the change after the merge. Verify: `openspec validate` passes.

## 1. Actor Interruption Semantics

- [x] 1.1 Add a `Processing`-phase interrupt path for real `SendUserMessage` commands received during an active tool-loop continuation.
- [x] 1.2 Reuse `ToolBatchAbandoned` persistence to close unanswered assistant tool calls before fresh-turn processing starts.
- [x] 1.3 Reset per-turn state and recall state before processing the interrupting message as a fresh turn.
- [x] 1.4 Ensure late LLM/tool callbacks from abandoned work cannot continue the interrupted turn.

## 2. Regression Tests

- [x] 2.1 Add a session integration test proving an interrupting user message is not appended into the active tool-loop continuation.
- [x] 2.2 Add a regression test proving the interrupting message starts a fresh turn after the old batch is abandoned.
- [x] 2.3 Add stale callback coverage proving late tool completion cannot restart an abandoned tool loop.

## 3. Documentation And Validation

- [x] 3.1 Public session documentation reviewed; no update required because the public lifecycle description did not change.
- [ ] 3.2 Add or update eval coverage for tool-loop interruption behavior.
- [x] 3.3 Run focused actor tests for session/tool-loop behavior.
- [ ] 3.4 Run `./evals/run-evals.sh`.
- [x] 3.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`.

Validation note: `./evals/run-evals.sh` was attempted twice against internal inference endpoints after provider details were supplied. The first run archived partial run `c0c0f07e-07c2-4f07-b369-0a086348f48e` and was stopped because the external inference service hung. The second run archived partial run `afaff694-ecd4-4db3-9ec6-2b91f26ded0c` and exceeded the 2-hour command timeout before the full suite completed.

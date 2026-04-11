# Tasks: compaction-rework

## 1. ExtractiveSessionReducer — user-message boundary truncation

- [x] 1.1 Replace the slice-by-count logic in
  `ExtractiveSessionReducer.ReduceAsync` with a backward walk: starting
  from `list.Count - keepCount`, walk backward until we hit a `User`-role
  message that is not prefixed with `SessionState.SystemNudgePrefix`
- [x] 1.2 Keep-zero edge case: skip the walk entirely and return just
  the system prompt (or empty when no system prompt is present)
- [x] 1.3 Unit tests in `ExtractiveSessionReducerTests.cs`:
  - [x] 1.3.1 `Window_walks_backward_to_user_boundary_when_naive_cut_would_orphan_tool_result`
  - [x] 1.3.2 `Window_walks_backward_past_assistant_tool_call_to_user_boundary`
  - [x] 1.3.3 `Window_skips_system_nudges_when_finding_user_boundary`
  - [x] 1.3.4 `Window_start_already_on_user_boundary_is_preserved`
  - [x] 1.3.5 `Window_falls_back_to_keep_all_post_system_when_no_user_message_found`
  - [x] 1.3.6 `Keep_zero_preserves_only_system_prompt` (and no-system variant)

## 2. ObservationPromptBuilder — structured 9-section prompt

- [x] 2.1 Rewrite `BuildObservationSystemPrompt` to accept `SessionId` and
  emit the nine-section template (Primary Request and Intent, Key Technical
  Concepts, Files and Code Sections, Problem Solving, Pending Tasks, Task
  Evolution, Current Work, Next Step, Required Files)
- [x] 2.2 Add explicit Task Evolution rule: include direct quotes from
  user messages that changed the task (anti-drift rule)
- [x] 2.3 Add explicit self-session-id disambiguation rule: "You are
  summarizing session {id}. Mark foreign session IDs as `session:{id}`,
  never conflate them with the self session."
- [x] 2.4 Add explicit "preserve prior summary" rule: "If the input
  already contains a `[session-summary ...]` block, preserve its sections
  verbatim and update in place — do not rewrite."
- [x] 2.5 `BuildObservationUserPrompt` — preserve tool-call arguments as
  compact `{name}({short-args})` evidence for the observer. Raise tool
  result truncation to 1500 chars.
- [x] 2.6 `WrapObservations` takes `SessionId` and produces a canonical
  `[session-summary session:{id}]` header block. Normalizes any
  pre-existing header-like first line to the canonical form.
- [x] 2.7 Update `ObservationPromptBuilderTests.cs`:
  - [x] 2.7.1 `System_prompt_embeds_self_session_id_for_disambiguation`
  - [x] 2.7.2 `System_prompt_lists_all_nine_structured_sections`
  - [x] 2.7.3 `System_prompt_requires_direct_quotes_in_task_evolution`
  - [x] 2.7.4 `System_prompt_instructs_preserve_prior_summary_verbatim`
  - [x] 2.7.5 `User_prompt_preserves_tool_call_arguments_as_short_projection`
  - [x] 2.7.6 `WrapObservations_uses_session_summary_marker_with_session_id`

## 3. SessionCompactionPipeline — thread SessionId

- [x] 3.1 Add `SessionId SessionId` field to `CompactionParameters` record
- [x] 3.2 Thread `SessionId` through `ExecuteAsync` to
  `GenerateObservationsAsync` and `WrapObservations` / `BuildObservationSystemPrompt`
- [x] 3.3 Store the summary as a User-role `SerializableChatMessage` at
  index 0 of the compacted messages list (the content begins with the
  `[session-summary session:{id}]` header per task 2.6, which is how
  consumers recognize it — no separate boundary index is persisted)
- [x] 3.4 Update `LlmSessionActor` to construct `CompactionParameters`
  with `_sessionId`

## 4. CompactionIntegrationTests

- [x] 4.1 `Compaction_observer_system_prompt_receives_self_session_id` —
  inspect `_fakeChatClient.ReceivedMessages` for the observer sidecar
  call and assert the self session id appears in the system text
- [x] 4.2 `Compaction_observation_wrapper_embeds_session_id_in_header` —
  after compaction, the next main-model call includes a User message
  whose content starts with `[session-summary session:{id}]`
- [x] 4.3 Existing scenarios continue to pass: buffer drain, session
  recovery after compaction+kill, emergency compaction with buffered
  message, summary format with context-summary tags

## 5. Quality gates

- [x] 5.1 `dotnet build src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj`
  passes with zero warnings
- [x] 5.2 `dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj`
  — all 909 tests pass
- [x] 5.3 `dotnet slopwatch analyze` reports no new violations against
  baseline
- [x] 5.4 `openspec validate compaction-rework` passes

## 6. PR + commit

- [x] 6.1 Commit with a message referencing the Slack failure and the
  research sources (Aider, OpenCode, Cline, Claude Code)
- [x] 6.2 Push branch `compaction-rework`, open PR against `dev`
- [x] 6.3 PR body references GH issues #595 and #596 as follow-ups

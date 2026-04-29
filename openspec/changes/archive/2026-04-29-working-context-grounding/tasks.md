# Tasks: working-context-grounding

## 1. WorkingContext record + SessionState integration

- [x] 1.1 Create `src/Netclaw.Actors/Sessions/WorkingContext.cs` —
  `public sealed record WorkingContext` with `ImmutableList<string>
  RecentFiles` defaulting to empty
- [x] 1.2 Add static `WorkingContext.Empty` singleton
- [x] 1.3 Add `WorkingContext.AddRecentFile(string path)` — returns
  same instance when path is already at head (ReferenceEquals
  short-circuit), rejects paths containing control characters, dedupes
  on repeat, caps at 10 entries
- [x] 1.4 Add `WorkingContext.IsEmpty` predicate
- [x] 1.5 Add `WorkingContext.ToContextBlock()` — renders
  `[working-context]\nrecent_files:\n  - ...` or empty string when
  `IsEmpty`
- [x] 1.6 Add `WorkingContext` field to `SessionState`, defaulting to
  `WorkingContext.Empty`
- [x] 1.7 Unit tests in `WorkingContextTests.cs`:
  - [x] 1.7.1 `Empty_has_no_recent_files`
  - [x] 1.7.2 `AddRecentFile_pushes_new_path_to_front`
  - [x] 1.7.3 `AddRecentFile_moves_existing_path_to_front_without_duplicating`
  - [x] 1.7.4 `AddRecentFile_caps_at_ten_entries`
  - [x] 1.7.5 `AddRecentFile_ignores_null_or_whitespace_path`
  - [x] 1.7.6 `AddRecentFile_rejects_path_containing_newline` (security)
  - [x] 1.7.7 `AddRecentFile_rejects_path_containing_carriage_return`
  - [x] 1.7.8 `AddRecentFile_rejects_path_containing_null_byte`
  - [x] 1.7.9 `AddRecentFile_returns_same_instance_when_path_is_already_at_head`
  - [x] 1.7.10 `AddRecentFile_returns_new_instance_on_real_change`
  - [x] 1.7.11 `IsEmpty_is_true_only_when_recent_files_is_empty`
  - [x] 1.7.12 `ToContextBlock_returns_empty_string_when_context_is_empty`
  - [x] 1.7.13 `ToContextBlock_renders_recent_files_section`
  - [x] 1.7.14 `Round_trips_through_protobuf_net_serialization`

## 2. SessionCompacted event + snapshot persistence

- [x] 2.1 Extend `SessionCompacted` in
  `src/Netclaw.Actors/Protocol/Events.cs` with an optional
  `WorkingContext? WorkingContext` field (ProtoMember 7, leaving
  ProtoMember 6 reserved — formerly `CompactionBoundaryIndex`)
- [x] 2.2 Extend `SessionSnapshot` in
  `src/Netclaw.Actors/Protocol/SessionSnapshot.cs` with a
  `WorkingContext? WorkingContext` field (ProtoMember 6, leaving
  ProtoMember 5 reserved — formerly `CompactionBoundaryIndex`)
- [x] 2.3 Update `SessionState.ToSnapshot` / `FromSnapshot` to
  round-trip `WorkingContext`, defaulting to `WorkingContext.Empty`
  when the snapshot has null
- [x] 2.4 Update `SessionState.Apply(SessionCompacted)` to preserve
  `WorkingContext` from the event when present, or retain the existing
  `WorkingContext` when the event's field is null (old-journal compat)

## 3. WorkingContextUpdater + LlmSessionActor tool hook

- [x] 3.1 Create `src/Netclaw.Actors/Sessions/WorkingContextUpdater.cs`
  — static helper with `UpdateFromToolResults` and `TryExtractFilePath`
- [x] 3.2 `TryExtractFilePath` probes well-known JSON field names
  (`path`, `file_path`, `filePath`, `file`, `filename`, `fileName`)
- [x] 3.3 `UpdateFromToolResults` builds a `CallId → ArgumentsJson`
  dictionary in a single backward pass of history (O(k + walk) per
  batch, not O(k*N))
- [x] 3.4 Returns the same `WorkingContext` instance when no changes
  were made (so the actor's `ReferenceEquals` guard skips the
  surrounding state allocation)
- [x] 3.5 Hook into `Command<ToolExecutionCompleted>` in
  `LlmSessionActor` — after the tool results are appended to history,
  call `WorkingContextUpdater.UpdateFromToolResults` and update
  `_state` only when the returned context differs by reference
- [x] 3.6 Unit tests in `WorkingContextUpdaterTests.cs`:
  - [x] 3.6.1 `TryExtractFilePath_returns_path_from_path_field`
  - [x] 3.6.2 `TryExtractFilePath_returns_path_from_file_path_field`
  - [x] 3.6.3 `TryExtractFilePath_returns_path_from_camelCase_filePath_field`
  - [x] 3.6.4 `TryExtractFilePath_returns_false_when_no_path_field_present`
  - [x] 3.6.5 `TryExtractFilePath_returns_false_for_empty_or_null_arguments`
  - [x] 3.6.6 `TryExtractFilePath_returns_false_on_malformed_json`
  - [x] 3.6.7 `UpdateFromToolResults_pushes_path_for_file_read_tool`
  - [x] 3.6.8 `UpdateFromToolResults_ignores_non_file_taking_tools`
  - [x] 3.6.9 `UpdateFromToolResults_ignores_results_without_matching_call`
  - [x] 3.6.10 `UpdateFromToolResults_dedupes_across_multiple_reads_of_same_file`

## 4. LlmSessionActor — inject [working-context] block

- [x] 4.1 Update `InjectDynamicContextLayers` to call
  `_state.WorkingContext.ToContextBlock()` and append it immediately
  after the existing `[session]` block when the context is non-empty
- [x] 4.2 When `WorkingContext.IsEmpty` is true, omit the block
  entirely (no barren header)

## 5. Integration tests

- [x] 5.1 `WorkingContext_populated_by_file_read_tool_execution` —
  fake chat client issues a `file_read` tool call, verify the next
  LLM call's system message contains a `[working-context]` block
  with the path
- [x] 5.2 `WorkingContext_survives_compaction` — populate
  `RecentFiles`, trigger compaction, assert the post-compaction
  `SessionState.WorkingContext` is unchanged
- [x] 5.3 `WorkingContext_survives_actor_recovery` — populate,
  kill actor, rejoin, assert `WorkingContext` is restored from
  snapshot

## 6. Quality gates

- [x] 6.1 `dotnet build src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj`
  passes with zero warnings
- [x] 6.2 `dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj`
  — all tests pass
- [x] 6.3 `dotnet slopwatch analyze` reports no new violations against
  baseline (SW003 on the JsonException catch is explicitly ignored
  via inline directive with justification)
- [x] 6.4 `openspec validate working-context-grounding` passes

## 7. PR + commit

- [x] 7.1 Commit on `working-context-grounding` branch
- [x] 7.2 Push branch, open PR against `compaction-rework`
  (automatically retargets to `dev` when that PR merges)
- [x] 7.3 PR body notes the dependency on PR1 and references GH
  issues #595 and #596 as follow-ups for CWD tracking and
  authoritative path resolution

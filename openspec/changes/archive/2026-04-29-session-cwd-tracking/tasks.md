## 1. WorkingContext Project Directory Field

- [x] 1.1 Add `ProjectDirectory` property (protobuf tag 2) to `WorkingContext` in `src/Netclaw.Actors/Sessions/WorkingContext.cs`
- [x] 1.2 Add `WithProjectDirectory(string path)` builder method to `WorkingContext`
- [x] 1.3 Update `WorkingContext.IsEmpty` to return false when project directory is set
- [x] 1.4 Update `WorkingContext.ToContextBlock()` to include `project_dir:` line
- [x] 1.5 Remove the "explicitly not in this struct" comment referencing #595/#596
- [x] 1.6 Extend `WorkingContextTests.cs`: `IsEmpty` false when project dir set but no files, `ToContextBlock()` includes `project_dir:` line, builder round-trips
- [x] 1.7 Extend `SerializationRoundTripTests.WorkingContext_round_trips`: verify round-trip preserves project directory, verify deserialize without field → null

## 2. Session Directory Visibility

- [x] 2.1 Add explicit `session_dir:` to the `[session]` block in `SessionMessageAssembler.BuildStaticContextBlock`

## 3. set_working_directory Tool

- [x] 3.1 Create `SetWorkingDirectoryTool` in `src/Netclaw.Actors/Tools/SetWorkingDirectoryTool.cs` — validates target is a real directory, resolves to absolute path, validates against audience trust profile roots via `ScopedFileAccessPolicy`, returns the resolved path
- [x] 3.2 Add `set_working_directory` to the profile-managed tool list in `ToolAudienceProfileResolver.IsProfileManagedTool`
- [x] 3.3 Wire tool result back to session actor to update `WorkingContext.ProjectDirectory` (extend post-tool-execution state update in `WorkingContextUpdater` or add callback on `ToolExecutionContext`)
- [x] 3.4 Create `SetWorkingDirectoryAudienceTests.cs` following `SchedulingToolAudienceTests` pattern: theory covering Public blocked, Team blocked, Personal allowed
- [x] 3.5 Test: valid directory within roots updates project directory; outside roots rejected; nonexistent directory rejected; personal audience allows any valid directory; switching projects replaces previous

## 4. Project Instructions in System Prompt

- [x] 4.1 Extend `SystemPromptAssembler.Assemble()` to accept optional project instructions content and include it alongside SOUL/AGENTS/TOOLING
- [x] 4.2 Update `LlmSessionActor.SetSystemPrompt()` to read project identity file from `_state.WorkingContext.ProjectDirectory` — check `.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, `CONTEXT.md` at project root (first match wins), skip gracefully on I/O errors
- [x] 4.3 Call `SetSystemPrompt()` again when `set_working_directory` changes the project directory
- [x] 4.4 Test: system prompt includes project content when project dir has identity file; no project dir → system prompt has only global layers; project switch re-assembles prompt with new project content

## 5. System Skill and Identity Template Updates

- [x] 5.1 Update `netclaw-operations` system skill to document project directory and `set_working_directory` tool
- [x] 5.2 Bump `metadata.version` in the skill's YAML frontmatter
- [x] 5.3 Update `netclaw-projects` skill to note that project instructions are automatically loaded when project directory is set
- [x] 5.4 Update `TOOLING.md` init wizard template to include guidance on session directory and project directory

## 6. Integration Verification

- [x] 6.1 `dotnet build` — compile check
- [x] 6.2 `dotnet test` — full test suite
- [x] 6.3 `dotnet slopwatch analyze` — no new violations
- [ ] 6.4 Run eval suite (`./evals/run-evals.sh`) — identity/prompt assembly changes require eval

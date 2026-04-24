## 1. WorkingContext Project Directory Field

- [ ] 1.1 Add `ProjectDirectory` property (protobuf tag 2) to `WorkingContext` in `src/Netclaw.Actors/Sessions/WorkingContext.cs`
- [ ] 1.2 Add `WithProjectDirectory(string path)` builder method to `WorkingContext`
- [ ] 1.3 Update `WorkingContext.IsEmpty` to return false when project directory is set
- [ ] 1.4 Update `WorkingContext.ToContextBlock()` to include `project_dir:` line
- [ ] 1.5 Remove the "explicitly not in this struct" comment referencing #595/#596
- [ ] 1.6 Extend `WorkingContextTests.cs`: `IsEmpty` false when project dir set but no files, `ToContextBlock()` includes `project_dir:` line, builder round-trips
- [ ] 1.7 Extend `SerializationRoundTripTests.WorkingContext_round_trips`: verify round-trip preserves project directory, verify deserialize without field → null

## 2. Session Directory Visibility

- [ ] 2.1 Add explicit `session_dir:` to the `[session]` block in `SessionMessageAssembler.BuildStaticContextBlock`

## 3. set_working_directory Tool

- [ ] 3.1 Create `SetWorkingDirectoryTool` in `src/Netclaw.Actors/Tools/SetWorkingDirectoryTool.cs` — validates target is a real directory, resolves to absolute path, validates against audience trust profile roots via `ScopedFileAccessPolicy`, returns the resolved path
- [ ] 3.2 Add `set_working_directory` to the profile-managed tool list in `ToolAudienceProfileResolver.IsProfileManagedTool`
- [ ] 3.3 Wire tool result back to session actor to update `WorkingContext.ProjectDirectory` (extend post-tool-execution state update in `WorkingContextUpdater` or add callback on `ToolExecutionContext`)
- [ ] 3.4 Create `SetWorkingDirectoryAudienceTests.cs` following `SchedulingToolAudienceTests` pattern: theory covering Public blocked, Team blocked, Personal allowed
- [ ] 3.5 Test: valid directory within roots updates project directory; outside roots rejected; nonexistent directory rejected; personal audience allows any valid directory; switching projects replaces previous

## 4. Project Instructions Context Layer

- [ ] 4.1 Create `ProjectInstructionLayerProvider` implementing `IContextLayerProvider` with `ContextLayerTiming.EveryTurn` — checks `.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, `CONTEXT.md` at project root (first match wins), frames as `Instructions from: {path}\n{content}`
- [ ] 4.2 Inject `Func<string?>` project directory accessor at construction time
- [ ] 4.3 Register the provider in the session's context layer list
- [ ] 4.4 Extend `SessionMessageAssemblerTests.cs`: project instructions injected when project dir has identity file; no project dir produces no `[project-instructions]` block; switching projects picks up new instructions

## 5. System Skill and Identity Template Updates

- [ ] 5.1 Update `netclaw-operations` system skill to document project directory and `set_working_directory` tool
- [ ] 5.2 Bump `metadata.version` in the skill's YAML frontmatter
- [ ] 5.3 Update `netclaw-projects` skill to note that project instructions are automatically loaded when project directory is set
- [ ] 5.4 Update `TOOLING.md` init wizard template to include guidance on session directory and project directory

## 6. Integration Verification

- [ ] 6.1 `dotnet build` — compile check
- [ ] 6.2 `dotnet test` — full test suite
- [ ] 6.3 `dotnet slopwatch analyze` — no new violations
- [ ] 6.4 Run eval suite (`./evals/run-evals.sh`) — identity/prompt assembly changes require eval

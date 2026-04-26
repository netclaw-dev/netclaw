## 1. Protocol and Types

- [ ] 1.1 Create `BackgroundJobProtocol.cs` with `StartBackgroundJob`, `BackgroundJobStarted`, `BackgroundJobCompleted`, `BackgroundJobStatus`, `CancelBackgroundJob` message types and `BackgroundJobDefinition` persistence record
- [ ] 1.2 Create `ActiveJobInfo` record type with `JobId`, `Command`, `Rationale`, `StartedAt`, `Audience`, `Boundary` properties

## 2. Background Job Manager Actor

- [ ] 2.1 Create `BackgroundJobManagerActor` as a singleton with `Ready` behavior, `StartBackgroundJob` handler, concurrency limit (default 5), and FIFO deferred queue
- [ ] 2.2 Implement job definition persistence to `~/.netclaw/jobs/{id}.json` (write on start, update on completion)
- [ ] 2.3 Implement startup reconciliation: load persisted definitions, reconcile incomplete jobs best-effort, mark lost/orphaned jobs as failed, deliver reconciliation results when possible
- [ ] 2.4 Implement completion handling: process deferred queue, update definition, report to parent
- [ ] 2.5 Implement cancellation: forward `CancelBackgroundJob` to child actor, kill process tree, mark cancelled
- [ ] 2.6 Register `BackgroundJobManagerActor` in `Program.cs` DI/actor system startup
- [ ] 2.7 Unit test: concurrency limit queues overflow jobs
- [ ] 2.8 Unit test: completion dispatches queued job
- [ ] 2.9 Unit test: startup reconciliation marks lost/orphaned jobs as failed

## 3. Background Job Execution Actor

- [ ] 3.1 Create `BackgroundJobExecutionActor` (child of manager) that spawns process with stdin closed, captures stdout/stderr to `~/.netclaw/jobs/{id}/output.log`
- [ ] 3.2 Implement process exit detection and result delivery via `DeliverTrustedSessionTurn` with originating audience/boundary
- [ ] 3.3 Implement timeout enforcement: kill entire process tree on timeout
- [ ] 3.4 Implement cancellation handling: kill process tree on `CancelBackgroundJob`
- [ ] 3.5 Unit test: successful process completion delivers result
- [ ] 3.6 Unit test: process timeout kills tree and delivers error
- [ ] 3.7 Unit test: cancellation kills tree and delivers cancellation notice

## 4. Pipeline Background Routing

- [ ] 4.1 Add background routing logic to `SessionToolExecutionPipeline`: when `ToolCallMeta.Background == true` and tool is `shell_execute`, send `StartBackgroundJob` to manager and return job handle
- [ ] 4.2 On `BackgroundJobStarted` response, persist `ActiveJobInfo` to session state and return job handle string as tool result
- [ ] 4.3 For non-shell tools with background signal, log warning and execute synchronously
- [ ] 4.4 Unit test: `_timeout_seconds` alone does not route shell to background
- [ ] 4.5 Unit test: explicit background flag routes shell to background
- [ ] 4.6 Unit test: non-shell tool with background flag executes synchronously

## 5. Session State Integration

- [ ] 5.1 Add `ActiveBackgroundJobs` (`ImmutableDictionary<string, ActiveJobInfo>`) to `SessionState` with persistence event support
- [ ] 5.2 Add background job dedup set (mirroring `ProcessedReminderIds` pattern)
- [ ] 5.3 Handle job result delivery in `LlmSessionActor`: remove from `ActiveBackgroundJobs`, add to dedup set, process result turn
- [ ] 5.4 Surface active jobs in working context / system prompt section
- [ ] 5.5 Unit test: `ActiveBackgroundJobs` round-trips through journal persistence
- [ ] 5.6 Unit test: duplicate job delivery is deduplicated
- [ ] 5.7 Unit test: active jobs restored on session recovery

## 6. check_background_job Tool

- [ ] 6.1 Create `CheckBackgroundJobTool` with `JobId` (required string) and `Cancel` (optional bool) parameters; expose it only when shell execution is available and use grant category `shell`
- [ ] 6.2 Implement status query: Ask `BackgroundJobManagerActor` for job status, return status, elapsed time, output tail or full result
- [ ] 6.3 Implement cancellation: forward cancel to manager, return confirmation
- [ ] 6.4 Register tool alongside shell availability/grants rather than as an independent always-on surface
- [ ] 6.5 Unit test: status query returns correct state for running/completed/failed jobs
- [ ] 6.6 Unit test: cancel kills running job and returns confirmation
- [ ] 6.7 Unit test: query for non-existent job returns error

## 7. Integration Testing

- [ ] 7.1 Integration test: background job completes and delivers result to active session
- [ ] 7.2 Integration test: background job delivers result to passivated session (session rehydrates)
- [ ] 7.3 Integration test: cancel running background job via check_background_job tool

## 8. Spec and Quality

- [ ] 8.1 In the implementation PR only, update `src/Netclaw.Cli/Resources/identity/AGENTS.template.md` with the explicit-only background-shell rule and the approval-before-start rule; do not change the live template ahead of implementation
- [ ] 8.2 In the implementation PR only, update the operator manual and runbook documentation for background shell execution after the feature behavior is implemented and verified
- [ ] 8.3 Run `openspec sync` to apply delta specs to `netclaw-session` main spec
- [ ] 8.4 Run `dotnet slopwatch analyze` — no new violations

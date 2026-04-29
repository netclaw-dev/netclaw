## 1. Shell Tokenizer and Command Policy Foundation

- [x] 1.1 Create `ShellTokenizer` in `Netclaw.Security` — extract `Tokenize()` from `ToolPathPolicy` into shared utility, add compound command splitting (`&&`, `||`, `;`, `|`), add `bash -c`/`sh -c` inner command extraction
- [x] 1.2 Refactor `ToolPathPolicy` to delegate tokenization to `ShellTokenizer` (no behavior change, verify existing tests still pass)
- [x] 1.3 Create `ShellCommandPolicy` in `Netclaw.Security` — hard deny list with structural verb+subcommand+flag matching, configurable defaults (self-destructive commands), `Evaluate(command)` returns `ShellCommandDecision`
- [x] 1.4 Add `ShellTokenizer` tests — tokenization, compound splitting, `bash -c` extraction, quoting edge cases
- [x] 1.5 Add `ShellCommandPolicy` tests — each default deny category (self-destruction, system-destructive), compound commands with denied segments, configurable additions/removals, case insensitivity

## 2. Approval Configuration Types

- [x] 2.1 Create `ToolApprovalMode` enum (Auto, Approval, Deny) and `ToolApprovalConfig` class (`DefaultMode`, `ToolOverrides` dictionary) in `Netclaw.Configuration`
- [x] 2.2 Add `ApprovalPolicy` property to `ToolAudienceProfile` — runtime default remains `Auto`; init-generated Personal config explicitly sets `shell_execute` to Approval mode
- [x] 2.3 Create `ToolApprovalStore` in `Netclaw.Configuration` — reads/writes `tool-approvals.json`, per-audience sections, thread-safe file I/O
- [x] 2.4 Add `ToolApprovalsPath` property to `NetclawPaths` (resolves to `~/.netclaw/config/tool-approvals.json`)
- [x] 2.5 Add `HardDenyPatterns` list to `ToolConfig` for operator-configurable hard deny patterns
- [x] 2.6 Update `netclaw-config.v1.schema.json` — add `ApprovalPolicy` to audience profile definition, add `HardDenyPatterns` to Tools section, add `ToolApprovalMode` enum

## 3. Approval Service and Matcher Infrastructure

- [x] 3.1 Create actor-backed approval state infrastructure in `Netclaw.Security` / `Netclaw.Actors` — `IToolApprovalService` backed by the tool approval actor with session-scoped and persistent per-audience lookups
- [x] 3.2 Define `IToolApprovalMatcher` interface — `ExtractPattern(toolCall)`, `IsApproved(toolCall, approvedPatterns)`, `FormatForDisplay(toolCall)`
- [x] 3.3 Implement `ShellApprovalMatcher` — verb-chain prefix extraction, compound command pattern collection, prefix matching against approved patterns
- [x] 3.4 Implement `DefaultApprovalMatcher` — tool-name-level matching for non-shell tools
- [x] 3.5 Add approval service / actor tests — session-scoped add/lookup, persistent backing, per-audience isolation
- [x] 3.6 Add `ShellApprovalMatcher` tests — pattern extraction from various commands, prefix matching, compound pattern collection

## 4. ToolAccessPolicy and Executor Integration

- [x] 4.1 Extend `ToolAccessDecision` with `RequiresApproval` variant — includes approval context (tool name, display text, extracted patterns, available options)
- [x] 4.2 Create `ToolApprovalRequiredException` in `Netclaw.Actors.Tools` — thrown by executor when `RequiresApproval` is returned
- [x] 4.3 Update `ToolAccessPolicy.AuthorizeInvocation()` — after existing Allow/Deny logic, consult `ToolApprovalConfig` for the resolved audience and return `RequiresApproval` context when the tool is approval-gated
- [x] 4.4 Update `DispatchingToolExecutor.ExecuteAsync()` — when `RequiresApproval` is returned, consult `IToolApprovalService` for still-unapproved patterns and throw `ToolApprovalRequiredException` only when approval is still needed
- [x] 4.5 Update `ShellTool` — add `ShellCommandPolicy` parameter, check hard deny before `ToolPathPolicy` in `ExecuteAsync`
- [x] 4.6 Update `ToolRegistrationExtensions.WithFirstPartyTools` — thread `ShellCommandPolicy` and approval dependencies through registration
- [x] 4.7 Add approval gate tests for `RequiresApproval` path — tool in Approval mode returns `RequiresApproval`, executor/service combination allows already-approved patterns to proceed, tool in Deny mode returns Deny
- [x] 4.8 Update `ShellToolTests` — hard-denied commands rejected before execution

## 5. Protocol Types and Approval Channel

- [x] 5.1 Create `ToolInteractionRequest` session output in `SessionOutput.cs` — `Kind` (approval), `CallId`, `ToolName`, `DisplayText`, `Patterns`, `Options` list; lifecycle (always delivered)
- [x] 5.2 Create `ToolInteractionResponse` session command — `CallId`, `SelectedKey` (approve_once, approve_session, approve_always, deny), `SessionId`
- [x] 5.3 Create `ApprovalDecision` enum — ApprovedOnce, ApprovedSession, ApprovedAlways, Denied, TimedOut
- [x] 5.4 Create `IApprovalChannel` interface and implementation — `WaitForApprovalAsync(callId, timeout, ct)` returns `Task<ApprovalDecision>`, `Complete(callId, decision)` resolves the TCS
- [x] 5.5 Add `IApprovalChannel` tests — wait/complete lifecycle, timeout behavior, unknown callId handling

## 6. Session Actor and Pipeline Integration

- [x] 6.1 Update `SessionToolExecutionPipeline.ExecuteSingleToolAsync` — catch `ToolApprovalRequiredException`, emit `ToolInteractionRequest` via callback, block on `IApprovalChannel.WaitForApprovalAsync`, execute on approval or return denial
- [x] 6.2 Update `SessionToolExecutionPipeline.ExecuteToolsAsync` signature — accept `IApprovalChannel` and approval request emission callback
- [x] 6.3 Update `LlmSessionActor` — create `IApprovalChannel` instance, pass to pipeline, handle `ToolInteractionResponse` in Processing behavior by calling `IApprovalChannel.Complete`
- [x] 6.4 Update `LlmSessionActor` — on ApproveOnce or ApproveAlways, record approval through `IToolApprovalService` with session or persistent scope
- [x] 6.5 Add channel `SupportsInteractiveApproval` capability to `ToolExecutionContext` (from `MessageSource` or channel metadata)
- [x] 6.6 Add actor integration tests — covered by ToolApprovalGateTests (unsupported channel auto-deny, approval mode returns RequiresApproval, already-approved allows) and ApprovalChannelTests (wait/complete, timeout, concurrent)

## 7. Slack Channel — Text Approval UI

- [x] 7.1 Render approval prompts in `SlackThreadBindingActor` as text-based A/B/C reply prompts posted into the active thread
- [x] 7.2 Parse matching Slack text replies into `ToolInteractionResponse` selections for the pending approval request
- [x] 7.3 Restrict approval replies to the requesting Slack sender and reject other users with a visible warning
- [x] 7.4 Route parsed approval replies through the session pipeline as `ToolInteractionResponse`
- [x] 7.5 Declare `SupportsInteractiveApproval = true` for Slack channel via ChannelType extension

## 8. Headless Channel and Input Adapters

- [x] 8.1 Declare `SupportsInteractiveApproval = false` for headless channel — ChannelType.Headless returns false from SupportsInteractiveApproval extension
- [x] 8.2 Verify headless channel auto-denies approval-gated tools with clear error message — ToolAccessPolicy returns "channel_does_not_support_approval" when SupportsInteractiveApproval is false

## 9. Init Wizard and Doctor Integration

- [x] 9.1 Add shell approval mode question to init wizard per-audience configuration — Approval remains the recommended Personal option; help text updated
- [x] 9.2 Write selected approval mode to `Tools.AudienceProfiles.{audience}.ApprovalPolicy.ToolOverrides` in generated config — Personal init output explicitly writes `shell_execute=Approval` instead of relying on runtime defaults
- [x] 9.3 Add `netclaw doctor` check — warn when Personal host shell is enabled without an explicit `shell_execute` approval gate
- [x] 9.4 Add `netclaw doctor` check — info advisory for stale patterns in `tool-approvals.json`

## 10. Audit Logging and Spec Sync

- [x] 10.1 Extend `ToolAuditEntry` with approval-related fields — `ApprovalDecision`, `ApprovalPattern`
- [x] 10.2 Log approval decisions (approved, denied, timed_out) in tool audit records
- [x] 10.3 Sync delta specs to main specs via `/opsx-sync`
- [x] 10.4 Run `dotnet slopwatch analyze` — verified 0 violations after all changes
- [ ] 10.5 Run eval suite — deferred to operator (requires running daemon with configured credentials; existing behavior preserved when ApprovalPolicy is unset)

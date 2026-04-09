## Why

Netclaw's tool security is binary per tool — once granted via audience profile,
there is no argument-level control. In the March 27 incident (#350), the agent
ran `netclaw daemon stop` via `shell_execute` and killed its own host process.
Destructive git commands (`git remote remove`, `git push --force`) were also
auto-approved because `shell_execute` has a blanket grant for Personal audience.
The existing `ToolPathPolicy` only checks file paths, not command verbs.

This change adds a general-purpose tool approval system — inspired by Claude
Code's permission model — that intercepts tool invocations and requires user
sign-off before execution. The approval flow is infrastructure-driven
(transparent to the LLM), works across all channels (Slack, TUI, plain text),
and dynamically builds the allow list from real usage instead of requiring
operators to manually write configuration. Relates to PRD-002 (SEC-003 tool
grant controls, SEC-006 approval surfaces, SEC-009 shell execution boundaries).

## What Changes

- **Tool approval config per audience**: `ToolApprovalConfig` on
  `ToolAudienceProfile` with per-tool approval mode overrides (Auto, Approval,
  Deny). Runtime defaults remain `Auto`; the init-generated Personal config
  explicitly writes `ApprovalPolicy.ToolOverrides.shell_execute = Approval` as
  the recommended shell-safe setting.
- **Three-layer invocation pipeline**: Hard deny floor (configurable, blocks
  self-destructive commands before approval) → tool access gate (existing
  audience allowlists) → approval gate (new, per-audience, per-tool).
- **Shell command pattern matching**: Verb-chain prefix extraction (e.g.,
  `git push origin main` → pattern `git push`). Compound command splitting.
  Recursive `bash -c` scanning. `IToolApprovalMatcher` interface for
  tool-specific pattern logic; default matcher uses tool name only.
- **Mid-turn approval pause**: Tool execution tasks block on
  `TaskCompletionSource` while awaiting user response. Other tools in the batch
  run independently. Session actor handles `ToolInteractionResponse` during
  Processing phase.
- **Channel-rendered approval UI**: New `ToolInteractionRequest` session output.
  Channels MUST render approval prompts and route responses back. MVP Slack
  uses a text-based A/B/C/D reply flow in-thread for approve once, approve for
  this chat, approve always, and deny. Channels that cannot support
  approvals auto-deny.
- **Persistent approval storage**: `~/.netclaw/config/tool-approvals.json`
  (separate from `netclaw.json` to avoid triggering config watcher restart).
  Per-audience sections. Only persistent approvals are written to disk;
  one-shot and current-session approvals stay in memory. Approval lookup and
  recording are mediated by an actor-backed `IToolApprovalService`.
- **Configurable hard deny list**: Defaults block self-destructive commands
  (kill daemon, `rm -rf /`, fork bombs). Operators can add or remove patterns.
- **Init wizard integration**: Asks about shell approval mode per audience
  during `netclaw init`.

## Capabilities

### New Capabilities

- `tool-approval-gates`: Core approval infrastructure — `ToolApprovalConfig`,
  `IToolApprovalMatcher`, actor-backed `IToolApprovalService`,
  `ToolApprovalStore`, `ToolInteractionRequest`/`ToolInteractionResponse`
  protocol, `ShellCommandPolicy`, `ShellTokenizer`, and configurable hard deny
  list.

### Modified Capabilities

- `netclaw-tools`: Tool invocation gains a third outcome (`RequiresApproval`)
  alongside Allow and Deny. `ToolAccessPolicy` decides when approval is needed;
  `DispatchingToolExecutor` consults `IToolApprovalService` to filter already-
  approved patterns before throwing `ToolApprovalRequiredException`. Shell tool
  gains hard deny check before execution. `ToolAccessDecision` extended. Audit
  logging records approval decisions.
- `netclaw-acl`: `ToolAudienceProfile` gains `ApprovalPolicy` property.
  Per-audience approval configuration with tool-level mode overrides. Persistent
  approval file per audience.
- `netclaw-session`: Session actor creates `IApprovalChannel`, passes it to tool
  execution pipeline, handles `ToolInteractionResponse` messages during
  Processing phase, and records current-session or persistent approvals through
  `IToolApprovalService` while handling one-shot approvals in-memory for the
  blocked retry only. New `ToolInteractionRequest` session output type
  (lifecycle, always delivered).
- `netclaw-slack-socket`: Slack channel renders `ToolInteractionRequest` as a
  text approval prompt with A/B/C/D reply options and routes matching text
  replies back as `ToolInteractionResponse` through the session pipeline.
- `netclaw-input-adapters`: Channel capability metadata includes
  `SupportsInteractiveApproval` flag. Channels that cannot support approvals
  trigger automatic deny for approval-gated tools.
- `netclaw-cli`: Init wizard asks about shell approval mode per audience and
  writes the selected `shell_execute` approval override into generated config.
  `netclaw doctor` validates approval config consistency (e.g., Personal host
  shell enabled without an explicit `shell_execute` approval gate).

## Impact

- **Netclaw.Security**: New `ShellTokenizer`, `ShellCommandPolicy`,
  `IToolApprovalService`, and approval-matcher types. `ToolPathPolicy`
  refactored to share tokenizer.
- **Netclaw.Configuration**: `ToolApprovalConfig`, `ToolApprovalMode` types.
  `ToolAudienceProfile` gains `ApprovalPolicy`. `ToolApprovalStore` for
  persistent file I/O. `NetclawPaths` gains `ToolApprovalsPath`. JSON schema
  updated.
- **Netclaw.Actors**: `ToolAccessPolicy` and `ToolAccessDecision` extended with
  `RequiresApproval`. `DispatchingToolExecutor` handles the new decision type
  and consults the actor-backed `IToolApprovalService` before requiring user
  interaction. `SessionToolExecutionPipeline` catches
  `ToolApprovalRequiredException` and blocks on `IApprovalChannel`.
  `LlmSessionActor` creates the approval channel, records approvals through
  `IToolApprovalService`, and handles responses during Processing. New protocol
  types in `SessionOutput.cs`.
- **Netclaw.Channels.Slack**: Text approval rendering in
  `SlackThreadBindingActor` and text reply parsing back into
  `ToolInteractionResponse`.
- **Netclaw.Channels**: `IChannel` or related interface gains approval support
  metadata.
- **Netclaw.Cli**: Init wizard step for approval mode. Doctor checks for
  approval config.
- **Config schema**: `netclaw-config.v1.schema.json` updated with
  `ApprovalPolicy` on audience profiles.
- **No breaking changes** to existing tool behavior when `ShellMode` remains
  `HostAllowed` with no approval overrides.

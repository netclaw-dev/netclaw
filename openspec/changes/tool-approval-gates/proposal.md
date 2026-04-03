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
  Deny). Default Personal profile sets `shell_execute` to Approval mode.
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
  Channels MUST render structured approval prompts and route responses back.
  Slack uses Block Kit buttons via Socket Mode. TUI uses inline keyboard
  prompts. Plain text channels use ABC option lists. Channels that cannot
  support approvals auto-deny.
- **Persistent approval storage**: `~/.netclaw/config/tool-approvals.json`
  (separate from `netclaw.json` to avoid triggering config watcher restart).
  Per-audience sections. Session-scoped approvals in transient
  `CommandApprovalCache`.
- **Configurable hard deny list**: Defaults block self-destructive commands
  (kill daemon, `rm -rf /`, fork bombs). Operators can add or remove patterns.
- **Init wizard integration**: Asks about shell approval mode per audience
  during `netclaw init`.

## Capabilities

### New Capabilities

- `tool-approval-gates`: Core approval infrastructure — `ToolApprovalConfig`,
  `IToolApprovalMatcher`, `CommandApprovalCache`, `ToolApprovalStore`,
  `ToolInteractionRequest`/`ToolInteractionResponse` protocol,
  `ShellCommandPolicy`, `ShellTokenizer`, and configurable hard deny list.

### Modified Capabilities

- `netclaw-tools`: Tool invocation gains a third outcome (`RequiresApproval`)
  alongside Allow and Deny. Shell tool gains hard deny check before execution.
  `ToolAccessDecision` extended. Audit logging records approval decisions.
- `netclaw-acl`: `ToolAudienceProfile` gains `ApprovalPolicy` property.
  Per-audience approval configuration with tool-level mode overrides. Persistent
  approval file per audience.
- `netclaw-session`: Session actor creates `IApprovalChannel`, passes it to tool
  execution pipeline, handles `ToolInteractionResponse` messages during
  Processing phase. New `ToolInteractionRequest` session output type (lifecycle,
  always delivered). `OutputFilter` updated if needed.
- `netclaw-slack-socket`: Slack channel registers `BlockAction` event handler
  for Socket Mode interactive responses. Renders `ToolInteractionRequest` as
  Block Kit `ActionsBlock` with approve/deny buttons. Routes `BlockAction`
  events back as `ToolInteractionResponse` through actor hierarchy.
- `netclaw-input-adapters`: Channel capability metadata includes
  `SupportsInteractiveApproval` flag. Channels that cannot support approvals
  trigger automatic deny for approval-gated tools.
- `netclaw-cli`: Init wizard asks about shell approval mode per audience.
  `netclaw doctor` validates approval config consistency (e.g., approval mode
  enabled but channel doesn't support it).

## Impact

- **Netclaw.Security**: New `ShellTokenizer`, `ShellCommandPolicy`,
  `CommandApprovalCache` types. `ToolPathPolicy` refactored to share tokenizer.
- **Netclaw.Configuration**: `ToolApprovalConfig`, `ToolApprovalMode` types.
  `ToolAudienceProfile` gains `ApprovalPolicy`. `ToolApprovalStore` for
  persistent file I/O. `NetclawPaths` gains `ToolApprovalsPath`. JSON schema
  updated.
- **Netclaw.Actors**: `ToolAccessPolicy` and `ToolAccessDecision` extended with
  `RequiresApproval`. `DispatchingToolExecutor` handles new decision type.
  `SessionToolExecutionPipeline` catches `ToolApprovalRequiredException` and
  blocks on `IApprovalChannel`. `LlmSessionActor` creates approval channel,
  handles responses during Processing. New protocol types in
  `SessionOutput.cs`.
- **Netclaw.Channels.Slack**: `BlockAction` event handler registration. Button
  rendering in `SlackThreadBindingActor`. Response routing through
  `SlackConversationActor` and `SlackGatewayActor`.
- **Netclaw.Channels**: `IChannel` or related interface gains approval support
  metadata.
- **Netclaw.Cli**: Init wizard step for approval mode. Doctor checks for
  approval config.
- **Config schema**: `netclaw-config.v1.schema.json` updated with
  `ApprovalPolicy` on audience profiles.
- **No breaking changes** to existing tool behavior when `ShellMode` remains
  `HostAllowed` with no approval overrides.

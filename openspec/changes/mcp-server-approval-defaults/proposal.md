## Why

Three interlocking MCP permissioning gaps break Netclaw's default-deny posture in
practice: `netclaw mcp add` silently exposes every tool on a new MCP server to
Personal audience (because `McpServersMode = All` combined with null
`McpServerToolGrants` means "all pass" at
`src/Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs:159-168`); MCP tools
never fire approval prompts because the approval system has no concept of a
per-server default — only exact-key `ToolOverrides[notion/create-pages]` entries,
and any tool discovered later falls through to `DefaultMode = Auto`; and the
editable `McpToolPermissionsPage` TUI cannot touch approval modes at all, while
`netclaw mcp tools <server>` is a read-only CLI table that operators mistake for
"the TUI". Relates to PRD-002 (security posture), the prior
`mcp-audience-tool-grants` change, and the `tool-approval-gates` spec.

## What Changes

- Add `McpServerDefaults: Dictionary<string, ToolApprovalMode>` field to
  `ToolApprovalConfig` in `src/Netclaw.Configuration/ToolApprovalConfig.cs`.
  Applies to all tools exposed by an MCP server unless overridden by an exact
  entry in `ToolOverrides`.
- Extend `ToolApprovalConfig.GetEffectiveMode` and
  `ToolAccessPolicy.ResolveApprovalMode` with a three-step lookup precedence:
  exact `ToolOverrides[{server}/{tool}]` → `McpServerDefaults[{server}]` →
  global `DefaultMode`. Newly discovered MCP tools inherit the server default
  automatically.
- Add `McpServerDefaults` to the `ToolApprovalConfig` definition in
  `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` with
  `"default": {}` so existing configs keep validating.
- Fix the stale `"mcp:server:tool"` comment in `ToolApprovalConfig.cs:30-31` —
  the actual exact-key format is `{server}/{tool}` (see `McpToolAdapter.cs:32`).
- Make `netclaw mcp add` fail-closed: after writing the `McpServers` entry,
  write `McpServerToolGrants[name] = []` for all three audience profiles and
  write `McpServerDefaults[name]` as `Approval` for Personal, `Approval` for
  Team, and `Deny` for Public. Add a `--grant-all` flag as a CI escape hatch
  that keeps the server-default writes but skips the grant writes (leaving
  grants null so legacy "all pass" applies). Emit a loud post-add hint
  pointing to `netclaw mcp permissions`.
- Extend `McpToolPermissionsPage` with a per-tool approval column and a
  server-default row. New keybindings: `M` cycles the server default
  (`Auto → Approval → Deny → Auto`), `P` cycles the highlighted tool's
  explicit override (`inherit → Auto → Approval → Deny → inherit`). Rendering
  shows `[Approval]` / `[Auto]` / `[Deny]` with `(def)` or `(override)` suffix.
- Extend `McpToolPermissionsViewModel` to track
  `_pendingServerDefaults` and `_pendingToolOverrides` (with a null sentinel
  for `inherit`); add `GetEffectiveMode`, `CycleServerDefault`,
  `CycleToolOverride`; extend `Save()` to write
  `ApprovalPolicy.McpServerDefaults` and `ApprovalPolicy.ToolOverrides`
  alongside existing `McpServerToolGrants` writes.
- Add `netclaw mcp permissions` subcommand alias that routes to the same
  Termina host as bare `netclaw mcp tools`. Append an `"Edit interactively:
  netclaw mcp permissions"` hint to `RunToolsList` output. Update help text.
- Extend `ToolAudienceProfilesDoctorCheck` with a new warning-level check: for
  each enabled MCP server, if Personal audience has `McpServersMode = All`
  AND no `ApprovalPolicy.McpServerDefaults[server]` entry AND no matching
  per-tool override, warn `"MCP server '{server}' has no approval default on
  Personal. Tools invoke without prompting."` No `--fix` auto-remediation.
- Bump `metadata.version` and update
  `feeds/skills/.system/files/netclaw-operations/SKILL.md` with `netclaw mcp
  permissions`, fail-closed `mcp add`, and the server-default model.

None of these changes are breaking for existing configs: null `McpServerToolGrants`
still means "all pass", absent `McpServerDefaults` still falls through to
`DefaultMode`, and existing servers in `netclaw.json` are untouched.

## Capabilities

### New Capabilities

_None._ This change extends four existing capabilities.

### Modified Capabilities

- `tool-approval-gates`: add `McpServerDefaults` to `ToolApprovalConfig`;
  extend approval-mode resolution with a three-step precedence (exact override
  → server default → global default) in both `GetEffectiveMode` and the
  runtime `ResolveApprovalMode` path.
- `netclaw-mcp`: change `netclaw mcp add` to be fail-closed by writing empty
  `McpServerToolGrants[name]` and per-audience `McpServerDefaults[name]` entries
  (Personal=Approval, Team=Approval, Public=Deny). Add `--grant-all` escape
  hatch. Add `netclaw mcp permissions` subcommand alias.
- `netclaw-acl`: extend `ToolAudienceProfilesDoctorCheck` with a
  warning-severity check for Personal audience servers lacking an approval
  default. Existing null-grants advisory remains unchanged.
- `netclaw-cli`: extend `McpToolPermissionsPage` TUI with a per-tool approval
  column, a server-default row, and `M`/`P` keybindings. Append discoverability
  hint to `RunToolsList` output. Update help text.

## Impact

- **Config schema:** additive — new `McpServerDefaults` property on
  `ToolApprovalConfig`. `"default": {}` keeps existing configs validating.
- **Enforcement path:** `ToolAccessPolicy.ResolveApprovalMode` gains one
  dictionary lookup per MCP tool invocation. Negligible hot-path cost.
- **Default behavior for new servers:** changes from "all tools auto-execute on
  Personal" to "zero tools until explicitly granted; approval required on
  Personal/Team, Deny on Public". Opt-out via `--grant-all` flag for CI.
- **Default behavior for existing servers:** unchanged. Pre-existing
  `netclaw.json` entries with null grants still "pass" at the tool gate; the
  new doctor warning surfaces the posture but does not auto-remediate.
- **Operator workflow:** `netclaw mcp permissions` becomes the one obvious
  place to manage MCP tool grants and approval modes. TUI gains two new
  keybindings; existing keys preserved.
- **Security:** closes the silent-exposure regression observed when adding
  Notion via `netclaw mcp add`. Satisfies PRD-002's default-deny requirement
  for new MCP servers. Supply-chain detection via the existing tool-drift
  warnings is unchanged.
- **System skills:** `netclaw-operations` must update its `metadata.version`
  and document the new command, per the `CLAUDE.md` system-skills sync rule.
- **Tests:** new unit tests for `GetEffectiveMode` precedence,
  `ResolveApprovalMode` precedence, `McpCommand.RunAdd` fail-closed writes,
  TUI `CycleServerDefault` / `CycleToolOverride` / `Save` round-trip, and
  doctor warning. Regression: existing `EmptyGrantList_NoToolsExposed` and
  approval exact-key tests must still pass unchanged.
- **No new runtime dependencies.** Slack approval plumbing, `DaemonApi`,
  `ConfigFileHelper`, and Termina helpers are reused without modification.

## 1. Config Model & Schema

- [ ] 1.1 Add `McpServerDefaults: Dictionary<string, ToolApprovalMode>` property to `ToolApprovalConfig` in `src/Netclaw.Configuration/ToolApprovalConfig.cs` with XML doc explaining the inheritance semantics.
- [ ] 1.2 Fix stale `mcp:server-name:tool-name` comment on `ToolOverrides` at `ToolApprovalConfig.cs:30-31` — the actual exact-key format is `{serverName}/{toolName}`.
- [ ] 1.3 Extend `ToolApprovalConfig.GetEffectiveMode(string toolName)` with a three-step lookup: exact `ToolOverrides[toolName]` → `McpServerDefaults[serverName]` (when `toolName` contains `/`) → `DefaultMode`.
- [ ] 1.4 Add `McpServerDefaults` to the `ToolApprovalConfig` definition in `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` with `"default": {}` and value type referencing the existing `ToolApprovalMode` enum.
- [ ] 1.5 Run `netclaw doctor` locally against an existing `netclaw.json` to confirm `ConfigSchemaDoctorCheck` still passes (schema addition is backward-compatible).

## 2. Runtime Approval Policy

- [ ] 2.1 Refactor `ToolAccessPolicy.ResolveApprovalMode` at `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs:189-213` so that the no-custom-matcher-key case delegates to `ToolApprovalConfig.GetEffectiveMode(toolName)`. Preserve the existing matcher-specific key branch and the fail-closed-on-Personal escape.
- [ ] 2.2 Add unit tests in `src/Netclaw.Actors.Tests/Tools/ToolAccessPolicyApprovalTests.cs` (or the existing approval test file) that assert the three-step precedence for the runtime gate:
  - Exact MCP override beats server default beats `DefaultMode`.
  - `shell_execute` (no slash) does not match an MCP server default.
  - `GetEffectiveMode` and `ResolveApprovalMode` return the same value for the same input.
  - Fail-closed-on-Personal still fires when no override and no server default exist.
- [ ] 2.3 Add unit tests in `src/Netclaw.Configuration.Tests/ToolApprovalConfigTests.cs` (create file if missing) that cover `GetEffectiveMode` precedence for the four decision points in decision #2 of `design.md`.

## 3. Fail-closed `netclaw mcp add`

- [ ] 3.1 Add a private helper `ApplySecureDefaultsForNewServer(Dictionary<string, object> config, string serverName, bool grantAll)` to `src/Netclaw.Cli/Mcp/McpCommand.cs` that, for each audience profile under `Tools.AudienceProfiles`, writes `McpServerToolGrants[serverName] = []` (skipped when `grantAll` is true) and `ApprovalPolicy.McpServerDefaults[serverName]` per the decision matrix (Personal=Approval, Team=Approval, Public=Deny). Reuse `ConfigFileHelper.GetOrCreateSection` throughout.
- [ ] 3.2 Wire the helper into `RunAdd` after the `mcpServers[name] = SerializeEntry(entry)` line at `McpCommand.cs:192`. Ensure the helper runs before `WriteConfigFile` so both config and secrets writes remain atomic.
- [ ] 3.3 Parse a new `--grant-all` flag in the `RunAdd` positional/flag loop at `McpCommand.cs:72-123`. Default is false (fail-closed); when true, the helper skips the grant writes but still writes the approval defaults.
- [ ] 3.4 Update the post-add output at `McpCommand.cs:211` to print a multi-line security hint identifying the number of granted tools (`0` without `--grant-all`, `all` with `--grant-all`) and the per-audience approval-default choices, and directing the operator to `netclaw mcp permissions`.
- [ ] 3.5 Update `WriteHelp` at `McpCommand.cs:1197` to document the `--grant-all` flag and reference `netclaw mcp permissions` alongside `netclaw mcp tools`.
- [ ] 3.6 Add unit tests in `src/Netclaw.Cli.Tests/Mcp/McpCommandRunAddTests.cs` (create if missing) that exercise `RunAdd` against a temp `netclaw.json`:
  - Default invocation writes empty grants and the correct server defaults on all three audiences.
  - `--grant-all` invocation skips grants but still writes the server defaults.
  - Pre-existing servers are untouched when adding a new server.
  - Missing `ApprovalPolicy` section is created during the write.

## 4. TUI: Per-tool approval column + server-default row

- [ ] 4.1 Add two new pending-state dictionaries on `McpToolPermissionsViewModel` at `src/Netclaw.Cli/Mcp/McpToolPermissionsViewModel.cs` near the existing `_pendingGrants` field: `_pendingServerDefaults` keyed by `(string Audience, string Server)` and `_pendingToolOverrides` keyed by `(string Audience, string Server, string Tool)` with a nullable `ToolApprovalMode?` value where `null` is the `inherit` sentinel.
- [ ] 4.2 Extend `HasUnsavedChanges` at line 47 to also return true when either new dictionary is non-empty.
- [ ] 4.3 Extend `InitializePendingGrantsFromConfig` at lines 123-144 so that loading a server also seeds the view with the existing `ApprovalPolicy.McpServerDefaults[server]` values and any exact `ToolOverrides` entries keyed by `{server}/{tool}`.
- [ ] 4.4 Add `CycleServerDefault()` method that advances the current audience/server pair through `Auto → Approval → Deny → Auto`, storing the result in `_pendingServerDefaults` and calling `NotifyStateChanged()`.
- [ ] 4.5 Add `CycleToolOverride(string toolName)` method that advances `_pendingToolOverrides` through `inherit (null) → Auto → Approval → Deny → inherit` for the `(audience, server, tool)` tuple.
- [ ] 4.6 Add `GetEffectiveMode(string toolName)` helper that returns `(ToolApprovalMode mode, bool isInherited)` by consulting, in order: pending tool override → config `ToolOverrides[server/tool]` → pending server default → config `McpServerDefaults[server]` → global `DefaultMode`. `isInherited` is true when the resolved value came from the server-default or global steps.
- [ ] 4.7 Extend `Save()` at lines 290-358 with two new write loops, keeping grant writes unchanged:
  - For each `_pendingServerDefaults` entry, write `ApprovalPolicy.McpServerDefaults[{server}]` under the target audience section. Allocate the `ApprovalPolicy` section via `ConfigFileHelper.GetOrCreateSection` if missing.
  - For each `_pendingToolOverrides` entry: if the value is `null` (inherit), remove the exact key from `ApprovalPolicy.ToolOverrides`; otherwise write the exact mode. Do not normalize "matches the server default" to removal — explicit overrides stay explicit.
  - Clear both pending dictionaries on successful save.
- [ ] 4.8 Update the TUI page layout in `src/Netclaw.Cli/Mcp/McpToolPermissionsPage.cs`:
  - Add a `[M] Server default: [mode]` row inside `BuildToolGrid` between the existing "Server enabled" row and the tool-list loop (around line 131). Render the mode name with the existing color convention (Color.White for Auto, Color.Yellow for Approval, Color.Red for Deny).
  - Extend the tool-row rendering loop at lines 133-152 to append an effective-mode badge and a `(def)` / `(override)` suffix sourced from `ViewModel.GetEffectiveMode`. Use a fixed-width column so rows align even when tool names vary.
- [ ] 4.9 Update the key handler at `McpToolPermissionsPage.cs:211-272`:
  - Add `ConsoleKey.M` (and lowercase `'m'` KeyChar) branches that call `ViewModel.CycleServerDefault()`.
  - Add `ConsoleKey.P` (and lowercase `'p'` KeyChar) branches that call `ViewModel.CycleToolOverride(DiscoveredTools[_toolCursor])` — gated on `IsServerAllowedForSelectedAudience() && DiscoveredTools.Count > 0` so the cycle does nothing when there is no tool under the cursor.
- [ ] 4.10 Extend the footer hint builder at line 171 to include `[M] Server default  [P] Tool mode` alongside the existing hints.
- [ ] 4.11 Add unit tests in `src/Netclaw.Cli.Tests/Mcp/McpToolPermissionsViewModelTests.cs` covering:
  - `CycleServerDefault` from `Auto` twice lands on `Deny`.
  - `CycleToolOverride` from `inherit` cycles through `Auto → Approval → Deny → inherit`.
  - `Save()` after mixed edits writes the correct combination of `McpServerDefaults`, `ToolOverrides`, and grant entries and removes inherited overrides rather than writing `"Auto"`.
  - Loading a config with existing `McpServerDefaults` populates the view's effective-mode display.

## 5. CLI Discoverability

- [ ] 5.1 Add `"permissions"` to the subcommand switch in `McpCommand.RunAsync` at line 41 and route it to the same TUI path as bare `netclaw mcp tools`.
- [ ] 5.2 Add a `mcpSubcommand is "permissions"` branch at `src/Netclaw.Cli/Program.cs:606` that falls through into the existing `AddTermina("/mcp-tools", …)` block at lines 609-621. Both commands should map to the same `McpToolPermissionsPage` route.
- [ ] 5.3 Append `writer.WriteLine("Edit interactively: netclaw mcp permissions");` to the end of `RunToolsList` at `McpCommand.cs:966`.
- [ ] 5.4 Update the TUI page title in `McpToolPermissionsPage.BuildHeader` at line 40 from "MCP Tool Permissions" to "MCP Permissions".
- [ ] 5.5 Update the help text written by `WriteHelp` at `McpCommand.cs:1197` to list `permissions` as the recommended interactive command and describe `tools <server>` as the read-only CLI view.

## 6. Doctor Warning

- [ ] 6.1 Extend `src/Netclaw.Cli/Doctor/ToolAudienceProfilesDoctorCheck.cs` with a new check method that iterates `Tools.McpServers`. For each enabled server, if Personal profile has `McpServersMode = All` AND no `ApprovalPolicy.McpServerDefaults[server]` entry AND no `ToolOverrides` entries whose key begins with `{server}/`, emit a warning-severity diagnostic pointing at `netclaw mcp permissions`.
- [ ] 6.2 Leave the existing null-grants advisory at lines 113-120 unchanged — the new check lives alongside it.
- [ ] 6.3 Add unit tests in `src/Netclaw.Cli.Tests/Doctor/ToolAudienceProfilesDoctorCheckTests.cs` covering:
  - Warning fires when the server has no Personal approval default and no per-tool overrides.
  - Warning does not fire when `McpServerDefaults[server]` is set.
  - Warning does not fire when at least one `ToolOverrides` entry matches `{server}/*`.
  - Warning does not fire when the server is not in `McpServers`.
  - Warning exit code path exercises the "warnings only" (2) branch rather than the error (1) branch when it is the sole finding.

## 7. System Skill Sync

- [ ] 7.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` to document `netclaw mcp permissions`, the fail-closed `mcp add` semantics, and the per-audience approval-default model (Personal/Team=Approval, Public=Deny). Add a short "migrating existing MCP servers" section explaining the doctor warning and how to resolve it via the TUI.
- [ ] 7.2 Bump `metadata.version` in the SKILL.md YAML frontmatter.
- [ ] 7.3 Do NOT run `generate-skill-manifest.sh` locally. Confirm via `git diff` that no other skill area is touched (netclaw-identity, netclaw-memory, search-citation, skill-authoring, netclaw-projects).

## 8. Quality Gates & Docs

- [ ] 8.1 Run `dotnet build` against the changed projects and fix any warnings introduced by the additions.
- [ ] 8.2 Run targeted `dotnet test` for `Netclaw.Configuration.Tests`, `Netclaw.Actors.Tests`, and `Netclaw.Cli.Tests` and confirm all new and existing tests pass. In particular verify `McpToolAudienceGrantsTests.EmptyGrantList_NoToolsExposed` still passes.
- [ ] 8.3 Run `dotnet slopwatch analyze` and confirm no new violations. Add any unavoidable entries to `.slopwatch/baseline.json` with justification only as a last resort.
- [ ] 8.4 Manual end-to-end verification per design.md §Migration Plan:
  - Add a throwaway MCP server via `netclaw mcp add`, inspect `netclaw.json` for empty grants and the expected approval defaults, and confirm the post-add hint.
  - Start the daemon, run `netclaw mcp permissions`, enable the server for Personal, grant two tools, cycle server default via `M`, cycle one tool override via `P`, save. Restart daemon.
  - Invoke a granted-but-unoverridden tool from a Slack thread and confirm the approval prompt appears. Invoke the overridden tool and confirm it runs without prompting. Invoke an ungranted tool and confirm deny reason `mcp_tool_not_allowed_for_audience_profile`.
  - Run `netclaw doctor` before and after setting a server default and confirm the new warning fires/clears as expected.
- [ ] 8.5 Update `docs/spec/` only if any runtime behavior in the spec matches the new precedence rules — per CLAUDE.md discovery rules, specs in `openspec/specs` are the authoritative artifacts, and this change already writes delta specs. No `docs/spec` edits expected unless a doc file cross-references the old two-step lookup.
- [ ] 8.6 Invoke `/opsx-verify` to confirm implementation matches the change artifacts.
- [ ] 8.7 Invoke `/opsx-sync` to merge the delta specs into the main specs under `openspec/specs/`.
- [ ] 8.8 Invoke `/opsx-archive` once the PR is merged to archive the change.

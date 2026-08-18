# Tasks: fix-mcp-new-tool-exposure

## 1. Runtime ACL: posture-aware per-tool grant

- [x] 1.1 Update `ToolAudienceProfileResolver.IsMcpToolAllowed`
  (`src/Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs`): when the profile
  `McpServersMode` is `All`, return `true` for a tool that the grant list does
  not name. Keep the closed allow-list for `Allowlist` posture.
- [x] 1.2 Update the XML doc comment on `IsMcpToolAllowed` to state the
  posture-aware behavior.
- [x] 1.3 Add resolver unit tests: `All` posture passes an unnamed tool;
  `Allowlist` posture denies an unnamed tool; both postures pass a named tool.

## 2. Runtime exposure: Deny hides the MCP tool

- [x] 2.1 In `ToolAccessPolicy.IsToolExposed` MCP branch
  (`src/Netclaw.Actors/Tools/ToolAccessPolicy.cs`), after the server and
  per-tool ACL checks pass, resolve the MCP tool effective approval mode via the
  audience `ApprovalPolicy` precedence (`ToolOverrides` -> `McpServerDefaults` ->
  `DefaultMode`) and return `false` when the mode is `Deny`.
- [x] 2.2 Reuse the existing approval-mode resolver
  (`ResolveApprovalMode` / `McpApprovalMatcher`). Add a small helper only if the
  exposure path cannot supply invocation arguments; the MCP mode resolution does
  not depend on arguments.
- [x] 2.3 Keep the change scoped to MCP tools. Do not alter built-in tool
  exposure.
- [x] 2.4 Add exposure unit tests: a `Deny` MCP tool is absent from
  `FilterExposedTools` and `FilterDiscoverableTools`; an `Auto`/`Approval` MCP
  tool stays present.

## 3. Regression: new tool inherits server default

- [x] 3.1 Add a regression test for issue #1959: `All` posture, a grant snapshot
  that does not name a newly discovered tool. Assert the new tool is exposed.
- [x] 3.2 Assert the new tool effective mode equals the server default. Cover
  both `McpServerDefaults` = `Approval` and `McpServerDefaults` = `Auto`.
- [x] 3.3 Add a fail-closed guard test: `Allowlist` posture (Team/Public) keeps a
  newly discovered tool hidden.

## 4. TUI: MCP Permissions page

- [x] 4.1 In `McpToolPermissionsViewModel`
  (`src/Netclaw.Cli/Mcp/McpToolPermissionsViewModel.cs`), for `All` posture, map
  the tool checkbox to the approval override: unchecked writes `Deny`, checked
  clears the override (server default). Stop seeding a full snapshot allow-list
  in `All` posture. For `Allowlist` posture, keep writing `McpServerToolGrants`.
- [x] 4.2 Update `IsToolGranted`/`GetEffectiveMode` so a row reflects `Deny` as
  disabled in `All` posture and reflects allow-list membership in `Allowlist`
  posture.
- [x] 4.3 Update row rendering in `McpToolPermissionsPage`
  (`src/Netclaw.Cli/Mcp/McpToolPermissionsPage.cs`) so a disabled row (Deny or
  un-granted) is greyed and the `(def)`/`(override)` badge stays correct.
- [x] 4.4 Add headless ViewModel tests: uncheck in `All` posture persists `Deny`;
  check clears the override; `Allowlist` posture still writes the allow-list.

## 5. CLI: `netclaw mcp`

- [x] 5.1 In `McpCommand` (`src/Netclaw.Cli/Mcp/McpCommand.cs`), make
  grant/revoke/`--snapshot` posture-aware. In `All` posture, "revoke" writes
  `Deny`; in `Allowlist` posture, "revoke" removes from the allow-list.
- [x] 5.2 Add CLI tests for the posture-aware grant/revoke behavior.

## 6. Daemon: drift warning

- [x] 6.1 In `McpClientManager.LogToolDrift`
  (`src/Netclaw.Daemon/Mcp/McpClientManager.cs`), fire the ungranted/stale
  drift warning only for `Allowlist` posture audiences. `All`-posture grant
  lists are additive, so they produce no drift. (An inert-snapshot log notice
  was implemented then removed in review: it was low-value operator noise, and
  the daemon restart on a config change already resets any per-server state.
  An active warning, if wanted, belongs in `netclaw doctor`.)
- [x] 6.2 Add a daemon test (`McpToolDriftTests`) that asserts an `All`-posture
  grant snapshot produces no drift and an `Allowlist` grant list still reports
  ungranted/stale tools.

## 7. Cross-boundary contract test

- [x] 7.1 Add a producer/consumer test: the TUI ViewModel writes `Deny` on
  uncheck; a fresh `ToolAccessPolicy`/resolver built from the written config
  hides the tool. Prove the persisted representation matches the runtime consumer.

## 8. Migration / round-trip

- [x] 8.1 Add a load/round-trip test: a config with an `All`-posture full-snapshot
  `McpServerToolGrants` plus a new discovered tool exposes the new tool.

## 9. Skill, docs, quality gates

- [x] 9.1 Update the `netclaw-operations` skill
  (`feeds/skills/.system/files/netclaw-operations/SKILL.md`) with the MCP
  permissions model: `Deny` hides a tool; new tools inherit the server default in
  open posture; Team/Public stay fail-closed. Bump `metadata.version`.
- [x] 9.2 Run `dotnet slopwatch analyze` and fix any new violation.
- [x] 9.3 Run `./scripts/Add-FileHeaders.ps1 -Verify` for copyright headers.
- [ ] 9.4 Run `./scripts/smoke/run-smoke.sh` for the MCP Permissions page if the
  prompt flow changed. **Deferred to CI:** the local environment has no `vhs`
  and the harness needs `sudo` to install Ollama. The `mcp-permissions` tape only
  smokes page-open/no-daemon/exit; it never reaches the tool grid, so it does not
  exercise the changed toggle logic. Headless ViewModel tests cover that behavior.
- [ ] 9.5 Run the eval suite if tool exposure/grant-category behavior needs a new
  case. **Deferred to CI:** the eval runner needs an external provider endpoint
  (`NETCLAW_EVAL_PROVIDER_ENDPOINT`) unavailable here. The change is a runtime ACL
  fix and additive skill documentation; it adds no tool, schema, or grant category,
  so no new eval case is warranted. Runtime/integration tests cover the behavior.

## 10. OpenSpec finish

- [ ] 10.1 Run `/opsx-verify` then `/opsx-sync` and `/opsx-archive` after the
  implementation lands and gates pass.

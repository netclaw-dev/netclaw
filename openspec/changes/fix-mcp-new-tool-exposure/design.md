## Context

An MCP tool has two independent access axes in Netclaw:

- Approval mode (`ToolApprovalConfig`: `ToolOverrides` -> `McpServerDefaults` ->
  `DefaultMode`). For an unseen tool this correctly falls through to the server
  default.
- Enabled state (`ToolAudienceProfile.McpServerToolGrants`, a positive
  allow-list). For an unseen tool this fails closed (hidden).

`ToolAudienceProfileResolver.IsMcpToolAllowed` enforces the enabled axis. It
gates exposure (`ToolAccessPolicy.IsToolExposed`) and invocation
(`AuthorizeInvocationCore`, deny reason
`mcp_tool_not_allowed_for_audience_profile`). The two sibling checks
`IsMcpServerAllowed` and `IsToolAllowed` short-circuit to `true` in `All`
posture. `IsMcpToolAllowed` does not. It always treats a grant entry as a closed
allow-list.

The MCP Permissions TUI seeds `McpServerToolGrants[server]` from a point-in-time
snapshot of the discovered tools. A later remote-added tool is absent from the
snapshot, so the resolver hides it. The daemon detects this drift but only logs
a warning.

The allow-list cannot separate two states: "operator disabled this tool" and
"tool did not exist yet." Both states read as "absent from the list." The fix
must move the explicit-disable signal off the allow-list.

## Goals / Non-Goals

**Goals:**

- A new MCP tool inherits the server default posture in open (`All`) posture.
- A disabled MCP tool leaves the tool list that the model sees.
- Team and Public audiences stay fail-closed for unseen tools.
- Reuse the existing `ToolApprovalMode.Deny`. Add no new config property.

**Non-Goals:**

- Change built-in (non-MCP) tool exposure logic.
- Rewrite persisted config files during migration.
- Reconstruct historical per-tool disable choices from a snapshot.

## Decisions

### Decision 1: `Deny` is the single "disabled" signal, and `Deny` hides the tool

A tool with effective approval mode `Deny` leaves the exposed tool list. Today
`IsToolExposed` does not read the approval mode, so a `Deny` tool is shown and
then blocked at invocation. The MCP branch of `IsToolExposed` now resolves the
approval mode and returns `false` on `Deny`.

Rationale: an LLM must not receive a tool that it cannot call. A hidden tool
prevents a wasted turn. This reuses the approval-mode resolver
(`GetApprovalMode` / `ResolveApprovalMode`) instead of a new code path.

Alternative considered: a new `McpServerToolDenials` deny-list field. Rejected.
It duplicates state that `Deny` already carries and needs a schema change plus
plumbing.

### Decision 2: `McpServerToolGrants` becomes posture-aware

`IsMcpToolAllowed` returns `true` for a tool absent from the grant list when the
audience posture is `All`. It keeps the closed allow-list for `Allowlist`
posture. This matches the existing pattern in `IsMcpServerAllowed` and
`IsToolAllowed`.

Rationale: `All` posture means "expose everything unless explicitly denied."
`Allowlist` posture means "expose only the listed items." The per-tool layer now
follows the same rule as the server and built-in-tool layers.

Alternative considered: auto-reconcile new tools into the grant list at
discovery time. Rejected. The daemon would write operator config silently, and a
blind union would wrongly grant new tools to Team and Public.

### Decision 3: TUI and CLI express disable as `Deny` in open posture

In `All` posture the MCP Permissions checkbox toggles between `Deny` (disabled,
hidden) and a cleared override (server default). In `Allowlist` posture the
checkbox keeps writing the `McpServerToolGrants` allow-list. The `netclaw mcp`
grant/revoke/snapshot paths follow the same posture rule.

Rationale: the persisted representation must match what the runtime consumes. A
disabled tool in open posture persists as a `Deny` override, which the resolver
reads back as hidden.

## Risks / Trade-offs

- [A `Deny` tool that operators expected to see-but-refuse now disappears] ->
  This is the intended behavior and matches the issue. Document it in the
  `netclaw-operations` skill and the change notes.
- [A pre-existing `All`-posture grant snapshot silently stops filtering] -> The
  snapshot was almost always a full catalog, so no tool changes state in
  practice. The TUI shows real tool state, and `tools.md` documents the inert
  behavior.
- [An operator genuinely curated a Personal subset via the allow-list] -> The
  feature is new/MVP. Such an operator re-expresses the subset with `Deny`. An
  active warning, if wanted, belongs in `netclaw doctor` (operator-run,
  idempotent), not a daemon log line.
- [Scope creep to built-in tools] -> Explicitly out of scope. The MCP branch of
  `IsToolExposed` is the only exposure path that changes.

## Migration Plan

1. Deploy the resolver and exposure changes. No config schema field changes.
2. On tool discovery, when a profile in `All` posture carries a
   `McpServerToolGrants[server]` entry, treat it as inert for the closed-list
   decision. No config rewrite; the TUI and `tools.md` cover the behavior.
3. Rollback: revert the resolver and exposure changes. The persisted config is
   unchanged, so a rollback restores the prior closed-list behavior.

## Open Questions

None. The model and scope are settled.

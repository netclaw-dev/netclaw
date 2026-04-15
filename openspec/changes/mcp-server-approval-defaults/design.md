## Context

Three interlocking gaps caused a real regression when Aaron added Notion via
`netclaw mcp add`: all tools were immediately available to Personal, no
approval prompts fired, and the `McpToolPermissionsPage` TUI had no way to
touch approval modes. The prior `mcp-audience-tool-grants` change
deliberately adopted the "null grants = pass" invariant as
backward-compatible opt-in tightening, but for brand-new servers there is
nothing to be backward-compatible with — the default is silent exposure.

Current enforcement lives in two places that must agree:

- **Config accessor**: `ToolApprovalConfig.GetEffectiveMode` in
  `src/Netclaw.Configuration/ToolApprovalConfig.cs` returns the mode for a
  tool name via a two-step lookup (`ToolOverrides` exact → `DefaultMode`).
- **Runtime gate**: `ToolAccessPolicy.ResolveApprovalMode` in
  `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs` (lines 189-213) reproduces
  the same lookup but also consults `IToolApprovalMatcher.GetApprovalModeKey`
  for argument-aware overrides and `IToolApprovalMatcher.IsFailClosedOnPersonal`
  for the Personal-audience fail-closed escape.

MCP tool names are `{serverName}/{toolName}` — slash-separated — per
`src/Netclaw.Actors/Tools/McpToolAdapter.cs:32`. The stale comment at
`ToolApprovalConfig.cs:30-31` says `mcp:server:tool`; it is wrong and must
be fixed.

The editable TUI at `netclaw mcp tools` (bare) routes through
`src/Netclaw.Cli/Program.cs:617` to `/mcp-tools`, rendering
`McpToolPermissionsPage` with `McpToolPermissionsViewModel`. `Save()` at
lines 290-358 already uses `ConfigFileHelper.GetOrCreateSection` /
`WriteConfigFile`; the same helper fits the approval-default writes
cleanly.

Stakeholders: operators who add MCP servers; the daemon at
`McpClientManager.ConnectAsync`; the Slack approval UI in
`SlackApprovalBlockBuilder` / `SessionToolExecutionPipeline`; doctor users
running `netclaw doctor` as a posture check.

## Goals / Non-Goals

**Goals:**

- Give MCP servers a first-class per-audience approval default so newly
  discovered tools inherit a sensible mode without per-tool enumeration.
- Make `netclaw mcp add` fail-closed by default: empty grants plus
  per-audience `McpServerDefaults` entries (Personal=Approval,
  Team=Approval, Public=Deny) with a `--grant-all` CI escape hatch.
- Give the TUI a single place to set the server default and per-tool
  overrides, with an obvious discoverability path via `netclaw mcp
  permissions` and a post-add hint.
- Keep the two approval-resolution code paths (`GetEffectiveMode` and
  `ResolveApprovalMode`) bit-for-bit consistent.
- Preserve backward compatibility: existing configs with null grants and
  no `McpServerDefaults` entries SHALL continue to resolve exactly as they
  do today.

**Non-Goals:**

- Flipping global `DefaultMode` to `Approval` for MCP tools. That would
  break every user who restarts the daemon after upgrading.
- Auto-migrating existing `netclaw.json` entries. Pre-existing null-grants
  configurations stay null; the doctor warning surfaces posture, operators
  opt in manually via the TUI.
- Changing the `McpServersMode = All` default for Personal or the "null
  grants = pass" invariant encoded at
  `ToolAudienceProfileResolver.cs:161-167`.
- Wildcard keys in `ToolOverrides` (`notion/*`). We chose explicit
  `McpServerDefaults` dict over wildcard overloading.
- Per-MCP-server approval default on `McpServerEntry`. Approval is
  per-audience; splitting it between `McpServers` and
  `Tools.AudienceProfiles` creates two sources of truth.
- Hot-reload of approval config. `ConfigWatcherService` already excludes
  `tool-approvals.json`; the new fields live in `netclaw.json` and inherit
  its existing reload semantics (daemon restart).

## Decisions

### 1. Explicit `McpServerDefaults` dict, not wildcard keys in `ToolOverrides`

**Decision:** Add a new `Dictionary<string, ToolApprovalMode>
McpServerDefaults` field to `ToolApprovalConfig`. Keys are MCP server
names (`notion`, not `notion/*`).

**Rationale:** The dict form is explicit in the schema and in `netclaw.json`
— operators can see "what is the server default" as a distinct concept from
"what is a per-tool override". Wildcard keys in `ToolOverrides` would
require every reader of the dictionary to be aware of the convention, and
we would have to special-case wildcard matching in both the config
accessor and the runtime gate. Having a separate field localizes the
concept and keeps `ToolOverrides` purely tool-keyed.

**Alternatives considered:**

- **Wildcard keys** (`notion/*` in `ToolOverrides`): rejected. Implicit
  convention encoded in key shape is harder to reason about and would
  require parsing logic in both `GetEffectiveMode` and
  `ResolveApprovalMode`.
- **Server default on `McpServerEntry`**: rejected. Approval is per-audience;
  splitting the field between two config sections creates two sources of
  truth. It also prevents giving Public a different default than Personal.

### 2. Three-step lookup precedence (exact → server default → global default)

**Decision:** Both `ToolApprovalConfig.GetEffectiveMode` and
`ToolAccessPolicy.ResolveApprovalMode` SHALL consult overrides in a fixed
order: exact `ToolOverrides[toolName]` → `McpServerDefaults[serverName]` (if
`toolName` contains `/`) → matcher fail-closed-on-Personal → audience
`DefaultMode`. The MCP server-default step slots in between the exact key
check and the fail-closed matcher branch so that:

- A deliberate per-tool override always wins.
- Unconfigured MCP tools on a server with a default inherit the default
  before the matcher even sees them.
- Tools on a server without a default still fall through to the existing
  fail-closed-on-Personal safety net.

**Rationale:** The ordering preserves every existing precedence guarantee
in `tool-approval-gates` and only adds one new step. `GetEffectiveMode`
becomes the single source of truth: `ResolveApprovalMode` delegates the
no-matcher case to it, and only diverges when an `IToolApprovalMatcher`
provides an argument-aware key (e.g. `file_write:control-plane`) which is
unrelated to MCP tools.

**Alternatives considered:**

- **Server default above exact override**: rejected. Breaks the existing
  guarantee that operators can override specific tools to escape a broad
  policy.
- **Server default below fail-closed-on-Personal**: rejected. For an MCP
  server with an explicit default on Personal, the fail-closed fallback
  would never fire — which is fine — but the precedence becomes harder to
  reason about if the two interact. Server default first keeps the
  hierarchy flat.

### 3. Fail-closed `mcp add` writes empty grants AND server defaults

**Decision:** `netclaw mcp add` writes both `McpServerToolGrants[name] = []`
(across all three audiences) and `McpServerDefaults[name]` per audience
(Personal=Approval, Team=Approval, Public=Deny). The `--grant-all` escape
hatch skips the grant writes but still writes the approval defaults.

**Rationale:** The two writes cover two orthogonal concerns. Empty grants
close the tool-exposure gap at the `ToolAudienceProfileResolver` layer
(which runs before approval). Server defaults close the approval gap
(which runs after grants succeed). Writing both means that even if an
operator runs `--grant-all` for a CI scenario, the approval plumbing is
still primed for interactive sessions. Writing only one of them leaves a
half-secure posture that depends on which audience the session runs in.

The per-audience approval-default choice reflects approver authority:
Personal operators can approve themselves; Team members have authority
within the team context; Public users do not have authority to grant
trust and therefore get `Deny` as belt-and-suspenders even if the operator
later adds the server to Public's `AllowedMcpServers`.

**Alternatives considered:**

- **Interactive prompt at add time**: rejected. Breaks CI/automation and
  relies on tool names being known before the daemon has connected to the
  server.
- **Default to `Approval` globally with flag to disable**: rejected. A
  global flip breaks existing non-MCP users on daemon restart; per-server
  entries localize the blast radius.

### 4. TUI surface: server-default row + per-tool override column

**Decision:** Extend `McpToolPermissionsPage.BuildToolGrid` to render a new
`[M] Server default: [Approval]` row under the existing "Server enabled"
line and a new per-tool `[mode] (def|override)` column on each tool row.
Add two keybindings — `M` cycles the server default, `P` cycles the
highlighted tool's explicit override (with an `inherit` sentinel that
removes the entry on save).

**Rationale:** The user explicitly chose per-tool column over server-level
only. The server-default row provides the "most common" lever (one
keystroke changes all inherited tools), while the per-tool column provides
escape-hatch visibility (which tools are overriding, which inherit). The
two keybindings are orthogonal: `M` affects state shared across every
inherited row, `P` affects only the highlighted row.

`inherit` as a fourth cycle state for `P` is the only clean way to remove
an existing override without introducing a modal "delete override?" prompt.
It renders as `(def)` and forces the view to re-resolve the effective mode
via `GetEffectiveMode`, keeping render logic declarative.

**Alternatives considered:**

- **Server default only, no per-tool column**: rejected. The user
  explicitly wanted per-tool visibility.
- **Separate sub-screen for approval modes**: rejected. Two surfaces for
  related state doubles the cognitive load and splits the save path.

### 5. `ResolveApprovalMode` delegates to `GetEffectiveMode` when there is
no argument-aware matcher

**Decision:** `ToolAccessPolicy.ResolveApprovalMode` calls
`ToolApprovalConfig.GetEffectiveMode(toolName)` directly for the common
case (non-matcher, non-argument), keeping the two code paths identical by
construction. The matcher branch (control-plane file paths, shell verb
chains) remains untouched and continues to use its own key form.

**Rationale:** Keeping the two resolvers in sync is a maintenance burden
and a latent bug. Delegating is free — `GetEffectiveMode` already has the
three-step lookup, and the runtime path was mirroring it anyway. The only
reason the runtime path was separate in the first place is that it needs
to consult the matcher's custom key before falling back to the base tool
key. We preserve that behavior: exact matcher key → exact tool key →
`GetEffectiveMode(toolName)` → fail-closed-on-Personal → `DefaultMode`.

**Alternatives considered:**

- **Duplicate the three-step lookup inline in `ResolveApprovalMode`**:
  rejected. Future changes to precedence would require editing two files
  in sync and introduce drift risk.

### 6. Doctor check is warning-only, no `--fix`

**Decision:** `ToolAudienceProfilesDoctorCheck` gains a new warning-severity
check for Personal audience servers lacking a `McpServerDefaults` entry.
No `--fix` auto-remediation. Existing null-grants advisory stays at
advisory severity.

**Rationale:** Writing approval defaults on behalf of the operator could
silently flip their workflow from auto-run to approval-prompts on next
daemon restart. That is exactly the kind of invisible posture change the
"no silent fallbacks" rule in `CLAUDE.md` prohibits. The warning surfaces
the posture without touching config; the operator opts in via the TUI.

**Alternatives considered:**

- **Error severity**: rejected. Would break `netclaw doctor` CI pipelines
  on first upgrade.
- **`--fix` auto-writes defaults**: rejected per the above.
- **Stay at advisory**: rejected. Advisory didn't catch the Notion
  regression; the severity elevation is the whole point.

## Risks / Trade-offs

- **Three consumers of the same precedence**. `GetEffectiveMode`,
  `ResolveApprovalMode`, and the TUI's `GetEffectiveMode` helper all encode
  the same order. Mitigation: the TUI helper delegates to the config
  accessor; `ResolveApprovalMode` delegates to it for the no-matcher case;
  unit tests assert both callers agree for the same input. If we ever
  fork the precedence, we do it in one place.
- **Null-grant invariant is load-bearing**. Existing users rely on `null =
  all pass`. The new `mcp add` flow writes empty lists for new servers
  only; pre-existing null entries remain null. The
  `EmptyGrantList_NoToolsExposed` test at
  `src/Netclaw.Actors.Tests/Tools/McpToolAudienceGrantsTests.cs:32`
  pins the empty-list semantic.
- **Aaron's existing Notion server** is already in `netclaw.json` with
  null grants and no server defaults. The new `mcp add` path does not
  retroactively mutate it — the doctor warning is the migration signal,
  and the fix is `netclaw mcp permissions` → cycle server default → save
  → restart.
- **TUI keybinding collision**. `P` is unused today at
  `McpToolPermissionsPage.cs:211-272`, but we must double-check that no
  shared Termina key handler (e.g. for quitting) intercepts it. Fallback
  is `Shift+M` or `Ctrl+P`.
- **Schema additive-only**. The new `McpServerDefaults` property must be
  added to `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`
  in the same PR (CLAUDE.md Configuration Schema Sync Rule). Include
  `"default": {}` so existing configs continue to validate.
- **Doctor warning false negatives**. If an operator uses `McpServersMode
  = Allowlist` for Personal with an explicit `AllowedMcpServers` entry but
  no approval default, the warning still fires. That is intentional: the
  server is reachable for Personal and therefore needs a default.
- **Public `Deny` as belt-and-suspenders** is potentially surprising if
  an operator later tries to add the server to Public's allowlist — they
  will see denials until they change the default. Document in the
  `netclaw-operations` skill update and surface the situation in doctor
  output.

## Migration Plan

1. Ship the schema, config model, policy, CLI, TUI, and doctor changes
   together.
2. On first daemon start after upgrade, existing `netclaw.json` files
   continue to validate (new field is optional with `"default": {}`).
3. Pre-existing MCP servers with null grants continue to work unchanged.
4. `netclaw doctor` emits the new warning for Personal-audience servers
   without an approval default — this is the operator's signal to run
   `netclaw mcp permissions`.
5. New `netclaw mcp add` invocations are fail-closed by default; operators
   who need the legacy behavior for CI pass `--grant-all`.
6. No data migration, no config rewrite, no daemon-level one-time upgrade
   step.
7. **Rollback**: reverting the package restores the old `mcp add` behavior
   and the two-step approval resolution. New `McpServerDefaults` entries
   persisted to `netclaw.json` become unknown fields; `additionalProperties:
   false` in the schema would reject them, but the old binary no longer
   runs schema validation against this path, so rollback remains safe for
   the runtime even if the schema check complains. Operators who rolled
   forward and want to roll back should remove `McpServerDefaults` keys
   manually from `netclaw.json` before launching the older binary.

## Open Questions

- **None.** All design decisions in this document were validated with the
  primary stakeholder before writing. The approved plan at
  `/home/petabridge/.claude/plans/virtual-brewing-quasar.md` locked in the
  four decisions above (config shape, fail-closed mcp add, TUI layout,
  doctor severity). Remaining open items are implementation details
  captured in `tasks.md` (e.g. final column alignment for the `(def)` /
  `(override)` suffix).

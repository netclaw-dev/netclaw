## Why

Directory-scoped persistent approvals (PR #896) accumulate one entry per
trusted directory in `~/.netclaw/config/tool-approvals.json` per audience and
tool. Today the only way to inspect or revoke those grants is to hand-edit
JSON. As the file grows operators lose visibility into what they have
trusted, and the friction-reduction benefit of persistent grants regresses
into a security-hygiene liability. Issue #921 asks for an operator CLI so
users can audit and revoke grants without touching the file directly.

## What Changes

- Add a `netclaw approvals` command surface with two modes:
  - Bare `netclaw approvals` (and `netclaw approvals tui`) launches an
    interactive Termina TUI page that lists grants grouped by audience and
    tool, with a revoke action.
  - Single-shot subcommands `list`, `revoke`, and `help` for scripting:
    - `list` supports `--audience`, `--tool`, and `--json`.
    - `revoke <pattern>` removes only exact matches (same case-sensitivity
      rules as the daemon's matcher) optionally scoped by `--audience` and
      `--tool`.
    - `revoke --tool <name> --all` clears every entry for that tool in the
      targeted audience(s).
- Extend `Netclaw.Configuration.ToolApprovalStore` with `RemoveApproval`,
  `RemoveAllForTool`, and a read-only `Snapshot` API. All writes remain under
  the existing per-instance lock. The JSON schema is unchanged.
- The CLI talks to the file directly through `ToolApprovalStore`. The daemon
  already reloads the file on every approval check, so out-of-band CLI
  edits are picked up without a restart and without new RPC on
  `IToolApprovalService`.
- Update the `netclaw-operations` system skill to point users at the new
  CLI instead of hand-editing JSON.

## Capabilities

### New Capabilities

None. The existing `netclaw-cli` capability already covers operator CLI
surface area; this change adds a new requirement under it rather than
introducing a new top-level capability.

### Modified Capabilities

- `netclaw-cli`: ADD a new requirement covering the `netclaw approvals`
  command surface (TUI, list, revoke, exit-code semantics).
- `tool-approval-gates`: MODIFY the existing
  "Persistent approval storage" requirement to clarify that the file is
  operator-editable both via direct edit and via the `netclaw approvals`
  CLI, and that the daemon picks up changes on the next approval check
  without a restart.

## Impact

**Code**:
- `src/Netclaw.Configuration/ToolApprovalStore.cs` — new `RemoveApproval`,
  `RemoveAllForTool`, and `Snapshot` methods.
- `src/Netclaw.Cli/Approvals/` — new `ApprovalsCommand` and helpers.
- `src/Netclaw.Cli/Tui/ApprovalsManagerPage.cs` and view model.
- `src/Netclaw.Cli/CliArgsParser.cs` and `Program.cs` — add `approvals`
  command registration and dispatch.
- `src/Netclaw.Cli.Tests/Approvals/` — new test suite.

**Skills**:
- `feeds/skills/.system/files/netclaw-operations/SKILL.md` — Approval
  Prompts section update; metadata version bump.

**APIs**: No public-protocol or wire-format changes. `IToolApprovalService`
is unchanged. JSON file format is unchanged.

**Security**: The CLI is a privileged-local operator tool (same trust level
as direct file edit). Revoke operations only remove grants — they cannot
add new privileges. Case sensitivity for `revoke` matching is delegated to
`ApprovalPatternMatching` so it stays symmetric with the daemon's gate
logic. There is no daemon-side cache to invalidate.

**Operational**: Operators no longer need to know the JSON layout to
manage their grants. The skill update teaches the running agent to
recommend `netclaw approvals list / revoke` instead of editing JSON.

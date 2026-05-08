## Context

PR #896 introduced directory-scoped persistent approvals for `shell_execute`,
storing one entry per trusted directory per audience per tool in
`~/.netclaw/config/tool-approvals.json`. The file is loaded by
`ToolApprovalStore.Load()` on every approval check inside `ToolApprovalActor`
and is **not** cached or watched by `ConfigWatcherService`. Today the only
operator surface for managing those grants is direct JSON editing.

The CLI host already routes commands through `CliArgsParser` (`KnownCommands`
set) and a switch in `Program.cs`. There is established precedent for
hybrid commands that launch a TUI on bare invocation and accept single-shot
subcommands otherwise (`netclaw provider`, `netclaw model`).

Stakeholders: end-user operators managing trusted directories, agent
authors that need a stable scriptable surface, and the running Netclaw
agent itself (via the `netclaw-operations` skill recommendations).

## Goals / Non-Goals

**Goals:**

- Operator-driven inspection and revocation of persistent grants without
  hand-editing JSON.
- Single command surface (`netclaw approvals`) that mirrors the existing
  TUI-or-subcommand pattern of `netclaw provider`.
- Zero new daemon RPC; zero new wire formats.
- The daemon picks up CLI mutations on the next approval check without a
  restart.
- Symmetric matching semantics between the CLI and the daemon's approval
  gate (no surprises where the CLI removes an entry the daemon would not
  have matched, or vice versa).

**Non-Goals:**

- Adding, broadening, or upgrading existing grants. The CLI never grants
  privilege; it only inspects and revokes.
- `prune` or stale-entry detection. Deferred until real drift is observed.
- Importing/exporting approval bundles between machines.
- Changes to `IToolApprovalService` or the actor protocol.
- Changes to the JSON file format.

## Decisions

### File-direct I/O over daemon RPC

`ToolApprovalActor` already calls `store.Load()` on every approval check
(`ToolApprovalStore.cs:117`). There is no in-memory cache to keep
coherent. CLI mutations therefore become visible to the daemon on the
next approval gate, with no restart and no message-passing round-trip.

Routing through `IToolApprovalService` would force the daemon to be
running for `list` and `revoke`, add a new RPC surface to maintain, and
buy nothing operationally. The schema-sync rule does not apply because
`tool-approvals.json` is not part of `netclaw-config.v1.schema.json`.

**Alternative considered**: hybrid (try daemon first, fall back to
file). Rejected — silent fallback is forbidden by the constitution and
the only fallback signal would be "is the daemon up", which is exactly
the condition the file-direct path already handles correctly.

### Exact-match revoke, no widening

`ApprovalPatternMatching.MatchesShellApprovalEntry` matches an
invocation against a stored entry by exact equality (or by
directory-root containment when the entry is a path). It explicitly does
not widen by verb prefix. The CLI's `revoke <pattern>` follows the same
discipline: exact equality only, using
`ApprovalPatternMatching.Comparer` so case sensitivity is identical to
the daemon (Ordinal POSIX, OrdinalIgnoreCase Windows).

`revoke --tool <name> --all` is offered as the explicit way to bulk-clear
a tool's entries. Directory-root containment as a `revoke` mode (e.g.,
`revoke /home/user/` removes everything under it) was rejected because
it inverts the safe default — a fat-fingered prefix could revoke far
more than intended. Operators wanting that behavior can pipe
`list --json` to a script.

### Bare invocation launches TUI

`netclaw provider` and `netclaw model` already follow this pattern; the
TUI host is configured via `Host.CreateApplicationBuilder` plus
`AddTermina("/approvals", ...)`. Reusing this convention keeps the
operator surface consistent. A `tui` subcommand alias is provided for
explicitness in scripts.

### `--audience` accepts wire values only

`TrustAudience.ToWireValue()` returns `personal` / `team` / `public`. The
CLI flag accepts those exact values and rejects anything else with a
clear error. No friendly aliases — the wire value is what shows up in
the file and in `list` output, so users only have to learn one
vocabulary.

### Empty file is a normal state

A missing or empty `tool-approvals.json` causes `list` to print
`No persistent approvals.` and exit 0. It is not an error — that is the
out-of-the-box state on a fresh install. `revoke` against the same
state exits 1 with `No matching approval found.` because the user
asked for a removal that did not happen; silent success would mask
typos in patterns or audience flags.

## Risks / Trade-offs

- **Race between daemon write and CLI write**:
  `ToolApprovalStore` already uses a per-instance lock for read-modify-
  write under `_lock`. The CLI and the daemon use different `ToolApprovalStore`
  instances pointed at the same file. The window between
  `Load → mutate → File.WriteAllText` is small and bounded; the worst
  case is that one writer's change is overwritten by the other on a
  near-simultaneous edit. Mitigation: this is the same risk the file
  has had since PR #896 (operators could edit the file by hand while
  the daemon was writing); not regressed by this change. If that risk
  materializes in practice, a follow-up can introduce a file lock or
  move writes onto the actor.
- **Quarantined file (`.invalid`) leaves operator without their grants**:
  Today `Load` already silently quarantines a malformed file. The CLI
  must surface that fact rather than silently re-running on an empty
  store. Mitigation: the CLI prints a single warning line on the next
  invocation when it observes a `.invalid` sibling.
- **Termina version drift**: The TUI page mirrors `ProviderManagerPage`
  exactly, including reactive primitives. If Termina's reactive
  surface changes, both pages move together — no incremental risk
  here. Mitigation: tests focus on the single-shot `ApprovalsCommand`
  surface where regressions are cheapest to catch; TUI is exercised
  manually during the verification step.
- **Comparer drift**: The CLI must use exactly the comparer
  `ApprovalPatternMatching` uses, or revoke can fail to find an entry
  the daemon would have matched. Mitigation: expose the comparer
  publicly (or a small `Equals(left, right)` helper) and use it from
  the CLI rather than re-deriving the platform check.

## Migration Plan

No schema or wire changes. Rolling back amounts to deleting the new
files and removing the `approvals` registration. Existing
`tool-approvals.json` files remain readable by both old and new
versions throughout.

## Open Questions

None at this time. All four design questions surfaced during planning
were resolved before this change was scaffolded.

## Why

When the daemon re-prompts an operator for a tool approval they believe they
already granted, there is no way to confirm whether a matching grant exists or
to diagnose why it failed to match. `ApprovalEntry` carries no temporal
metadata (only `verb` + `directory`), and the approval gate logs nothing when a
same-verb persisted grant is a near-miss. Operators cannot prove a duplicate
prompt, and silent near-misses hide real matcher bugs (path normalization,
symlink-segment rejection, case rules). This change adds the missing
provenance: a creation timestamp on every new grant, plus loud diagnostics at
the gate. Relevant PRDs: PRD-002 (gateway security envelope), PRD-003 (operator
ops console), PRD-004 (CLI).

## What Changes

- Add an optional `createdAt` (`DateTimeOffset?`) field to `ApprovalEntry`.
  New grants are stamped at write time in `ToolApprovalStore.AddApproval` via
  an injected `TimeProvider`. Entries already on disk read back as `null`
  ("added before timestamps were tracked").
- **No schema-version bump.** `createdAt` is an additive optional field that
  is backward- and forward-compatible. Bumping `tool-approvals.json` from
  `version: 2` to `3` would quarantine every existing file to `.v1.bak` and
  wipe all persisted grants, because `IsCurrentSchema` requires exactly `2`.
- `ToolApprovalEntryComparer` continues to compare `verb` + `directory` only.
  Idempotent re-grants of an existing approval keep the **original**
  timestamp; `Normalize` passes `createdAt` through unchanged.
- Add a near-miss diagnostic at the approval gate: when a pattern is
  unapproved but a persisted entry exists with the **same verb**, the daemon
  logs why it did not match (cwd not under the grant's directory, symlink
  segment along the path, or verb case mismatch), including the grant's
  `createdAt`. Diagnostics go to the daemon log only — no change to the
  approval prompt body.
- Surface creation time as relative text ("added 3 days ago", "added —" for
  null) in the `netclaw approvals` interactive TUI list and the
  `netclaw approvals list` CLI output. The `--json` output exposes the raw
  `createdAt` field.

## Capabilities

### New Capabilities

<!-- None. This change modifies existing capabilities only. -->

### Modified Capabilities

- `tool-approval-gates`: adds a requirement for an optional `createdAt` field
  on `ApprovalEntry`, and a requirement for near-miss approval-gate
  diagnostics.
- `netclaw-cli`: adds a requirement for creation-time display in both the
  `netclaw approvals list` output and the interactive `netclaw approvals` TUI
  (the TUI is specced under this capability, not `netclaw-operator-ui`).

## Impact

- `Netclaw.Configuration`: `ApprovalEntry` (new field), `ToolApprovalStore`
  (constructor gains `TimeProvider`; `AddApproval` stamps `createdAt`),
  `ToolApprovalEntryComparer` (`Normalize` passes `createdAt` through;
  equality unchanged).
- `Netclaw.Security`: `ApprovalPatternMatching` gains a diagnostic-producing
  method that explains same-verb near-misses without changing match results.
- `Netclaw.Actors`: `ToolApprovalActor` logs near-miss diagnostics; DI wiring
  passes `TimeProvider` into `ToolApprovalStore`.
- `Netclaw.Cli`: `ApprovalsManagerViewModel` / approvals TUI view and
  `netclaw approvals list` rendering show relative creation time. TUI change
  requires the interactive tape harness per the testing policy.
- DI / construction sites of `ToolApprovalStore` (daemon host, CLI, TUI,
  tests) must supply a `TimeProvider`.
- `tool-approvals.json` on-disk format: additive optional `createdAt`,
  `version` stays `2`. No migration, no quarantine.
- System skill `netclaw-operations` (CLI/diagnostics surface changed).
- No security-posture change: diagnostics are read-only logging; the matcher's
  decisions are untouched.

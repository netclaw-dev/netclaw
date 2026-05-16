## 1. Configuration: `createdAt` field and write-time stamping

- [x] 1.1 Add `DateTimeOffset? CreatedAt { get; init; }` to `ApprovalEntry`
  with `[JsonPropertyName("createdAt")]` and an XML doc-comment noting it is
  optional, null for pre-feature entries, and excluded from approval equality.
- [x] 1.2 In `ToolApprovalEntryComparer.Normalize`, ensure `CreatedAt` is
  carried through unchanged (the `entry with { ... }` path must not drop it);
  confirm `Equals` still compares verb + directory only.
- [x] 1.3 Add an optional `TimeProvider? timeProvider = null` parameter to the
  `ToolApprovalStore` constructor, resolving to `TimeProvider.System`.
- [x] 1.4 In `ToolApprovalStore.AddApproval`, stamp `CreatedAt` with
  `timeProvider.GetUtcNow()` when the (normalized) entry's `CreatedAt` is
  null; leave a non-null incoming value untouched. Ensure the idempotent
  "already exists" branch does not restamp the existing entry.
- [x] 1.5 Confirm `version` stays `2` and `IsCurrentSchema` is unchanged;
  verify a v2 file with no `createdAt` properties is not quarantined.

## 2. Security: near-miss explainer

- [x] 2.1 Add a pure method to `ApprovalPatternMatching` that, given an
  unapproved shell candidate (verb, candidate directory, cwd) and the
  persisted entries, returns same-verb near-misses with a classified reason
  (directory-not-under-grant, symlink-segment-on-path, verb-case-mismatch).
  Reuse the path/symlink logic from `MatchesShellApproval` so the diagnostic
  cannot drift from the matcher.
- [x] 2.2 Cover the case where a persisted entry's verb equals the candidate
  only under case-insensitive comparison but not under the platform comparer
  (verb-case-mismatch), so POSIX case rules are explained.

## 3. Actors: gate-side diagnostic logging

- [x] 3.1 Add an `ILoggingAdapter` (`Context.GetLogger()`) to
  `ToolApprovalActor`.
- [x] 3.2 In the `GetUnapprovedPatterns` handler, for each pattern reported
  unapproved, invoke the explainer and log one diagnostic line per near-miss
  including the grant's verb, directory scope, `CreatedAt`, and reason. Emit
  nothing when there is no same-verb persisted entry.
- [x] 3.3 Verify the diagnostic runs only on the unapproved branch and does
  not alter `UnapprovedPatternsResponse`.
- [x] 3.4 Pass a `TimeProvider` into `ToolApprovalStore` at the daemon
  construction site (`Netclaw.Daemon/Program.cs`) — `TimeProvider.System` or
  the daemon's existing injected provider if one is in scope.

## 4. CLI and TUI: creation-time display

- [x] 4.1 Add a relative-time formatter for `DateTimeOffset?` returning
  `added <relative>` for a value and `added —` for null. Reuse an existing
  CLI humanizer if one exists; otherwise add a minimal buckets helper.
- [x] 4.2 Render the relative creation time per entry in `netclaw approvals
  list` human output (`ApprovalsCommand` / `ApprovalsListView` rendering),
  as distinct metadata, not mixed into the scope-label column.
- [x] 4.3 Confirm `netclaw approvals list --json` carries `createdAt` on each
  entry (raw ISO-8601, or absent-as-null consistent with `directory`).
- [x] 4.4 Show the relative creation time in the `netclaw approvals` TUI list
  rows (`ApprovalsManagerViewModel.ApprovalDisplayItem` / approvals page
  view).

## 5. Tests

- [x] 5.1 `ToolApprovalStore` tests: new grant stamped via `FakeTimeProvider`;
  legacy v2 file (no `createdAt`) loads with null and is not quarantined;
  idempotent re-grant preserves the original `CreatedAt`.
- [x] 5.2 `ToolApprovalEntryComparer` test: entries differing only by
  `CreatedAt` compare equal; `Normalize` preserves `CreatedAt`.
- [x] 5.3 `ApprovalPatternMatching` explainer tests: directory-not-under-grant,
  symlink-segment, and verb-case-mismatch near-misses each classified;
  no near-miss when no same-verb entry exists.
- [x] 5.4 `ToolApprovalActor` test: a same-verb directory near-miss produces a
  log line and does not change the unapproved result.
- [x] 5.5 CLI tests: `list` shows relative time and the `added —` placeholder;
  `list --json` round-trips `createdAt`.
- [x] 5.6 No VHS tape covers the `netclaw approvals` page; the page is
  validated by the headless xUnit Termina test
  `ApprovalsManagerPageTests.ListView_ShowsRelativeCreationTime`, which drives
  the real render pipeline via `VirtualTerminal` and asserts the new "Added"
  column and relative text.

## 6. Quality gates and docs

- [x] 6.1 Update the `netclaw-operations` system skill if CLI approval-output
  guidance references the `approvals list` shape.
- [x] 6.2 Run `dotnet slopwatch analyze` — no new violations.
- [x] 6.3 Run `./scripts/Add-FileHeaders.ps1 -Verify` for any new `.cs` files.
- [x] 6.4 `openspec validate approval-creation-timestamps --strict` passes;
  change merged in #1010 and archived.

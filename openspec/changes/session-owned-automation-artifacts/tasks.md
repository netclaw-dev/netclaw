## 1. Canonical Session Paths

- [x] 1.1 Extend `SessionDirectoryHelper` with fixed reminder and job subdirectory paths. Reuse `NetclawPaths.SessionsDirectory`.
- [x] 1.2 Add path tests for session containment and encoded artifact IDs.

## 2. Current-Session Reminder Storage

- [x] 2.1 Make `ReminderDefinitionStore` scan daemon and session definitions by the current global reminder ID.
- [x] 2.2 Route `CurrentSession` definitions through their stored `Delivery.SessionId`. Keep `Channel` and `None` definitions in the daemon directory.
- [x] 2.3 Make `ReminderHistoryStore` use the actual definition directory for read, append, and delete operations.
- [x] 2.4 Add store and manager tests for restart lookup, daemon/session routing, legacy path retention, conflicts, corrupt files, and owner mismatch.
- [x] 2.5 Assert that `ReminderPayload`, reminder manager commands, retry settlement, and delivery routes remain unchanged.

## 3. Background Job Storage

- [x] 3.1 Make `BackgroundJobDefinitionStore` derive definition and output paths from the required source `SessionId`.
- [x] 3.2 Update job manager calls to supply the source session for reads, writes, deletes, status checks, and output access.
- [x] 3.3 Add store and manager tests for session routing, restart reconciliation, legacy path retention, conflicts, owner mismatch, and log access.
- [x] 3.4 Assert that the singleton manager, global capacity limit, FIFO queue, passivation reap, and trusted delivery behavior remain unchanged.

## 4. Operations Guidance

- [x] 4.1 Update `docs/runbooks/background-jobs.md` with session-owned paths and legacy path compatibility.
- [x] 4.2 Update the `netclaw-operations` skill and its scheduling reference. Increase the skill metadata version.
- [x] 4.3 Update source documentation that names the legacy reminder or job paths.

## 5. Validation

- [x] 5.1 Run the focused reminder, background job, path-policy, serialization, and daemon endpoint tests.
- [x] 5.2 Run the affected project test suites and record all environment-dependent skips.
- [x] 5.3 Run `dotnet slopwatch analyze` and `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 5.4 Run strict OpenSpec validation and `git diff --check`.

## 6. Adversarial Review Hardening

- [x] 6.1 Reserve session reminder and job artifacts from generic writes.
- [x] 6.2 Reject session artifact paths that contain a symbolic link or reparse point.
- [x] 6.3 Count only valid definitions when duplicate IDs are resolved.
- [x] 6.4 Validate reminder storage ownership before a scheduler mutation.
- [x] 6.5 Add focused regression tests and update operations guidance.
- [x] 6.6 Run the focused suites, quality gates, strict OpenSpec validation, and `git diff --check`.

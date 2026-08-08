## 1. Canonical Session Paths

- [ ] 1.1 Extend `SessionDirectoryHelper` with fixed reminder and job subdirectory paths. Reuse `NetclawPaths.SessionsDirectory`.
- [ ] 1.2 Add path tests for session containment, encoded artifact IDs, duplicate IDs, and symbolic-link escapes.

## 2. Current-Session Reminder Storage

- [ ] 2.1 Make `ReminderDefinitionStore` index daemon and session definitions by the current global reminder ID.
- [ ] 2.2 Route `CurrentSession` definitions through their stored `Delivery.SessionId`. Keep `Channel` and `None` definitions in the daemon directory.
- [ ] 2.3 Make `ReminderHistoryStore` use the indexed definition owner for read, append, and delete operations.
- [ ] 2.4 Add the convergent legacy migration for current-session definitions and history. Preserve source data after a conflict or invalid owner.
- [ ] 2.5 Add store and manager tests for restart lookup, daemon/session routing, migration restart, conflicts, corrupt files, and owner mismatch.
- [ ] 2.6 Assert that `ReminderPayload`, reminder manager commands, retry settlement, and delivery routes remain unchanged.

## 3. Background Job Storage

- [ ] 3.1 Make `BackgroundJobDefinitionStore` derive definition and output paths from the required source `SessionId`.
- [ ] 3.2 Update job manager calls to supply the source session for reads, writes, deletes, status checks, and output access.
- [ ] 3.3 Add the convergent legacy migration for job definitions and output directories. Preserve source data after a conflict or invalid owner.
- [ ] 3.4 Add store and manager tests for session routing, restart reconciliation, migration restart, conflicts, owner mismatch, and log access.
- [ ] 3.5 Assert that the singleton manager, global capacity limit, FIFO queue, passivation reap, and trusted delivery behavior remain unchanged.

## 4. Operations Guidance

- [ ] 4.1 Update `docs/runbooks/background-jobs.md` with the session-owned paths and legacy migration behavior.
- [ ] 4.2 Update the `netclaw-operations` skill and its scheduling reference. Increase the skill metadata version.
- [ ] 4.3 Update source documentation that names the legacy reminder or job paths.

## 5. Validation

- [ ] 5.1 Run the focused reminder, background job, path-policy, serialization, and daemon endpoint tests.
- [ ] 5.2 Run the affected project test suites and record all environment-dependent skips.
- [ ] 5.3 Run `dotnet slopwatch analyze` and `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`.
- [ ] 5.4 Run strict OpenSpec validation and `git diff --check`.

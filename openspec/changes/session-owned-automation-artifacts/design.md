## Context

Netclaw stores reminder files under `~/.netclaw/schedules/reminders/`. It stores background job files under `~/.netclaw/jobs/`.

`CurrentSession` reminders return to one source session. Every background job also records one source `SessionId` and returns there.

The session directory provides the canonical file root for session artifacts. The file policy also treats that directory as a trusted root.

The reminder and job managers are daemon singletons. They coordinate concurrent work across sessions and must remain singletons.

## Goals / Non-Goals

**Goals:**

- Put session-owned reminder and job files under the source session directory.
- Keep daemon-owned reminder files under the daemon schedule directory.
- Keep the current actors, messages, scheduler payload, and tool contracts.
- Preserve all compatible files during the path migration.
- Let a future session-retention process remove session artifacts as one unit.

**Non-Goals:**

- Implement the future 30-day session-retention process.
- Move manager actor ownership into each session actor.
- Change reminder execution, delivery, retry, or settlement behavior.
- Change background process execution, passivation, or delivery behavior.
- Permit duplicate reminder or job IDs across sessions.
- Add a new configuration value or storage service.
- Add a background-job-specific artifact retention timer.

## Decisions

### The session directory is the physical ownership boundary

Netclaw will use these canonical paths:

```text
~/.netclaw/sessions/{session-key}/reminders/{reminder-id}.json
~/.netclaw/sessions/{session-key}/reminders/{reminder-id}.history.jsonl
~/.netclaw/sessions/{session-key}/jobs/{job-id}.json
~/.netclaw/sessions/{session-key}/jobs/{job-id}/output.log
```

`CurrentSession` reminder files will use the session paths. All background job files will use the session paths.

`Channel` and `None` reminder files will remain under `~/.netclaw/schedules/reminders/`. Those reminders create new sessions and have daemon scope.

The implementation will reuse `NetclawPaths.SessionsDirectory` and `SessionDirectoryHelper`. It will not add a parallel path configuration.

Alternative: Keep the global directories and add a separate artifact purge. This duplicates session ownership and requires two retention mechanisms.

### Manager actors remain global coordinators

The current singleton managers will retain concurrency control, reconciliation, and delivery coordination. Physical file ownership will not change actor ownership.

This decision avoids one manager per session. It also avoids a new actor protocol for cross-session capacity control.

Alternative: Move execution managers below each session actor. This adds actor lifecycle changes and splits the global concurrency limit.

### Store indexes preserve the current ID-only contracts

Each store will maintain an in-memory map from its globally unique ID to a canonical file path. Startup scans will rebuild each map.

The reminder store will scan the daemon reminder directory and each fixed session reminder directory. The job store will scan each fixed session job directory.

`ReminderPayload` will continue to contain only `ReminderId`. Manager commands will also keep their current ID-only forms.

This decision avoids an Akka.Reminders payload migration. It also avoids changes to protobuf messages, CLI routes, tools, and actor commands.

Alternative: Persist an absolute definition path in `ReminderPayload`. That path can become stale after a home-directory move or data restore.

Alternative: Add `SessionId` to all reminder messages. This gives stronger type context but adds unnecessary protocol changes for the current global ID model.

### The stores derive and validate every session path

A caller will provide typed ownership data, not an arbitrary file path. The store will derive the full path from `SessionId` and the artifact ID.

The store will require the result to remain under the exact source session directory. It will reject path traversal and symbolic-link escapes.

A loaded definition must name the same session as its containing session directory. The store will reject a mismatch and log the reason.

The current audience and boundary checks will remain unchanged. A path under another trusted root will not satisfy the session-owner check.

### Startup performs a convergent legacy migration

The reminder store will inspect each legacy global definition. It will move only a `CurrentSession` definition with a valid `Delivery.SessionId`.

The store will move the history file before the definition file. The definition move will act as the migration commit point.

The job store will inspect each legacy global job definition. It will move the output directory before the definition file.

Each move will use a temporary path and an atomic rename within the destination file system. A restart will resume an incomplete migration.

The migration will never overwrite a destination artifact. A conflict or invalid owner will produce an error and preserve the source data.

The stores will continue to read a valid legacy source after a migration error. This compatibility path will be explicit and observable.

No scheduler replacement is necessary. The persisted reminder payload still resolves through the ID index after the definition moves.

### Future session retention must respect live session automation

This change does not add session retention. A later retention design must not silently delete a session with an enabled `CurrentSession` reminder.

That design can pin the session or cancel its reminders before removal. The future OpenSpec change must select and test one policy.

Background jobs do not need a retention pin. The current passivation contract reaps them before the session becomes inactive.

## Risks / Trade-offs

- **A startup scan can cost more with many sessions.** The stores scan only fixed `reminders/` and `jobs/` subdirectories.
- **An interrupted migration can split related files.** The definition file acts as the commit point, and the next startup resumes the move.
- **A duplicate ID can make ownership ambiguous.** The stores retain global ID uniqueness and fail on duplicate definitions.
- **A direct file edit can bypass a tool confirmation.** Store validation still rejects invalid schema, owner, audience, and boundary data.
- **A future janitor can delete an active reminder.** The later retention change must define a live-reminder policy before file removal.

## Migration Plan

1. Start the daemon with the new owner-aware stores.
2. Scan the current daemon directories and the fixed session artifact directories.
3. Move valid legacy session-owned artifacts to their canonical session paths.
4. Build the ID indexes from the canonical and compatible legacy files.
5. Run the current reminder and job reconciliation paths without protocol changes.
6. Log each migration failure and keep its source files intact.

Rollback can use the prior binary after operators move session artifacts back to the legacy directories. The JSON formats do not change.

## Open Questions

None.

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
- Preserve all current files at their present paths.
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

### Fixed-directory scans preserve the current ID-only contracts

Each store will scan only the daemon directory and the fixed artifact directory under each session.

The stores will not add an index, a new service, or a new persistence record.

`ReminderPayload` will continue to contain only `ReminderId`. Manager commands will also keep their current ID-only forms.

This decision avoids an Akka.Reminders payload migration. It also avoids changes to protobuf messages, CLI routes, tools, and actor commands.

Alternative: Persist an absolute definition path in `ReminderPayload`. That path can become stale after a home-directory move or data restore.

Alternative: Add `SessionId` to all reminder messages. This gives stronger type context but adds unnecessary protocol changes for the current global ID model.

### The stores derive and validate every session path

A caller will provide typed ownership data, not an arbitrary file path. The store will derive the full path from `SessionId` and the artifact ID.

The store will require the result to remain under the exact source session directory. It will reject a path traversal attempt.

A loaded definition must name the same session as its containing session directory. The store will reject a mismatch and log the reason.

The current audience and boundary checks will remain unchanged. A path under another trusted root will not satisfy the session-owner check.

The stores will reject a session path that contains a symbolic link or reparse point. They will reuse `PathUtility.ContainsSymlinkSegment`.

The generic write policy will reserve only the direct reminder and job definition files. The agent can still read the definitions and write logs or history.

This rule protects the stored trust envelope from a generic file edit. The reminder and job tools remain the supported mutation surfaces.

### Current artifacts stay at their present paths

The stores will read both the daemon directories and the fixed session subdirectories. They will not move files during startup.

An update will write to the path that already owns the definition. This rule keeps active reminders and background job records stable.

Only a new `CurrentSession` reminder or background job will use a session directory. `Channel` and `None` reminders will use the daemon directory.

No scheduler replacement is necessary. The persisted reminder payload still resolves by its current reminder ID.

The stores will count only valid definitions when they detect duplicate IDs. A corrupt or owner-mismatched candidate will not hide a valid legacy definition.

A reminder update will validate its storage owner before it changes the scheduler. An invalid owner transition will leave the prior definition and schedule unchanged.

### Future session retention must respect live session automation

This change does not add session retention. A later retention design must not silently delete a session with an enabled `CurrentSession` reminder.

That design can pin the session or cancel its reminders before removal. The future OpenSpec change must select and test one policy.

Background jobs do not need a retention pin. The current passivation contract reaps them before the session becomes inactive.

## Risks / Trade-offs

- **A lookup can cost more with many sessions.** The stores scan only fixed `reminders/` and `jobs/` subdirectories.
- **A duplicate ID can make ownership ambiguous.** The stores retain global ID uniqueness and fail on duplicate definitions.
- **A direct file edit can change a trust field.** The generic write policy reserves definition JSON files while it leaves other session artifacts writable.
- **A future janitor can delete an active reminder.** The later retention change must define a live-reminder policy before file removal.

## Migration Plan

No file migration occurs. The new stores read current daemon paths and new session paths.

Rollback can use the prior binary for all existing files. New session-owned files require the new binary.

## Open Questions

None.

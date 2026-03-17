## 1. Data Model and History Store

- [x] 1.1 Add `HistoryRecord` record type to `ReminderProtocol.cs` with fields: `FiredAt` (DateTimeOffset), `Success` (bool), `DurationMs` (long), `SessionId` (string), `ErrorMessage` (string?)
- [x] 1.2 Create `ReminderHistoryStore` class in `Netclaw.Actors/Reminders/` with `AppendAsync(ReminderId, HistoryRecord)` and `ReadAsync(ReminderId, int maxRecords)` methods
- [x] 1.3 Implement append logic: serialize record as single JSON line, append to `~/.netclaw/reminders/{id}.history.jsonl`
- [x] 1.4 Implement trim-on-overflow: when count exceeds cap, write trimmed records to `.tmp` file then atomic rename
- [x] 1.5 Implement `ReadAsync`: read tail of file, deserialize lines, return up to `maxRecords` most recent entries; return empty list if file absent
- [x] 1.6 Add `HistoryMaxRecords` (default: 500) to `ReminderConfig` and bind in configuration

## 2. Execution Actor Integration

- [x] 2.1 Inject `ReminderHistoryStore` into `ReminderExecutionActor` via constructor/DI
- [x] 2.2 Record `startedAt` timestamp at execution start using `TimeProvider.GetUtcNow()`
- [x] 2.3 Call `AppendAsync` with the completed `HistoryRecord` after sending completion/failure message to manager; catch and log any write exception without propagating

## 3. Reminder Deletion Cleanup

- [x] 3.1 In the reminder delete path (CLI handler + `CancelReminderTool`), delete `{id}.history.jsonl` after deleting the definition file; log a warning if delete fails but do not error

## 4. CLI History Command

- [x] 4.1 Add `history` subcommand to `ReminderCommand` with `<id>` argument and `--last` option (default: 20)
- [x] 4.2 Add `GET /api/reminders/{id}/history?last={n}` endpoint to the daemon API, backed by `ReminderHistoryStore.ReadAsync`
- [x] 4.3 Implement CLI table rendering: columns `fired_at`, `status`, `duration_ms`, `session_id`; print "No execution history recorded for {id}" when list is empty
- [x] 4.4 Return HTTP 404 with clear message when no reminder definition exists for the given ID

## 5. Agent Tool

- [x] 5.1 Create `GetReminderHistoryTool` in the scheduling tools namespace with parameters `reminder_id` (required) and `last` (optional, default 20, max 100)
- [x] 5.2 Register `GetReminderHistoryTool` under the `scheduling` grant in the tool registry
- [x] 5.3 Return structured `HistoryRecord` list from the tool; return empty list (not error) when no history file exists

## 6. Tests

- [x] 6.1 Unit test `ReminderHistoryStore`: append creates file, trim fires at cap, atomic rename leaves valid file, read returns empty on missing file
- [x] 6.2 Unit test `ReminderHistoryStore`: trim preserves newest records and drops oldest when cap exceeded
- [x] 6.3 Integration test `ReminderExecutionActor`: successful execution appends a `success: true` record; failed execution appends `success: false` with `errorMessage`
- [x] 6.4 Unit test `GetReminderHistoryTool`: `last` capped at 100, returns empty list for unknown reminder ID, rejected without `scheduling` grant

## 7. System Skill Update

- [x] 7.1 Update `feeds/skills/.system/files/netclaw-manual/SKILL.md` to document the `get_reminder_history` tool (parameters, grant requirement, return shape) and the `netclaw reminder history` CLI command
- [x] 7.2 Bump `metadata.version` in the skill frontmatter and update the embedded copy in `src/Netclaw.Daemon/BuiltInSkills/`

## 8. Spec Sync

- [x] 8.1 Run `/opsx-sync` to merge delta specs into `openspec/specs/netclaw-scheduling/spec.md` and promote `reminder-execution-history` to `openspec/specs/`

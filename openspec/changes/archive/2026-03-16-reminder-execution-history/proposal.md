## Why

Scheduled reminder executions leave no queryable record — operators and the
agent itself cannot determine when a job last ran, whether it succeeded, or
how long it took without grepping daemon logs. Adding a lightweight, per-reminder
execution history makes operational visibility a first-class concern.

## What Changes

- `ReminderExecutionActor` appends a structured execution record to a
  per-reminder `.history.jsonl` file on every completion (success or failure)
- `ReminderConfig` gains a `HistoryMaxRecords` field (default: 500); the store
  trims oldest entries when the cap is exceeded
- New CLI subcommand: `netclaw reminder history <id> [--last N]` reads and
  formats the history file for a given reminder
- New agent tool: `get_reminder_history` (grant: `scheduling`) returns recent
  execution records so the agent can reason about job health inline
- The history record stores a `sessionId` pointer — full execution content is
  retrievable by launching the corresponding session; no output duplication

## Capabilities

### New Capabilities

- `reminder-execution-history`: Per-reminder append-only execution log with
  cap-and-trim storage, CLI read access, and an agent tool for inline queries

### Modified Capabilities

- `netclaw-scheduling`: Add execution history requirements — recording,
  retention policy, CLI history subcommand, and `get_reminder_history` tool

## Impact

- **`Netclaw.Actors`**: `ReminderExecutionActor` gains file-write on exit;
  new `ReminderHistoryStore` type owns append and trim logic
- **`Netclaw.Configuration`**: `ReminderConfig` adds `HistoryMaxRecords: int`
- **`Netclaw.Cli`**: New `history` subcommand under `reminder`
- **`Netclaw.Tools`** (or `Netclaw.Actors`): New `GetReminderHistoryTool`
  registered under `scheduling` grant
- **Storage**: `~/.netclaw/reminders/{id}.history.jsonl` — one file per reminder,
  no schema migration required
- **No breaking changes** to existing reminder API, CLI subcommands, or
  persistence format

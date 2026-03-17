## Context

Each scheduled reminder execution already creates an isolated LLM session
(`reminder/{id}/{firedAtMs}`). `ReminderExecutionActor` tracks success,
failure, and duration in memory and reports back to `ReminderManagerActor`,
but no durable record survives the process. Operators and the agent itself
cannot answer "when did this last run?" or "has it been failing?" without
grepping daemon logs. The session record holds the full execution detail; what
is missing is a lightweight, queryable index of past executions per reminder.

## Goals / Non-Goals

**Goals:**

- Append one structured record per execution to `~/.netclaw/reminders/{id}.history.jsonl`
- Cap history at `ReminderConfig.HistoryMaxRecords` (default 500); trim oldest on overflow
- Expose history via `netclaw reminder history <id> [--last N]` CLI command
- Expose history via `get_reminder_history` agent tool (grant: `scheduling`)
- Link each record to its session ID so full execution detail is one lookup away

**Non-Goals:**

- Storing execution output or LLM turns in the history record (session already has it)
- Cross-reminder aggregation, dashboards, or trend analytics
- Migrating or restructuring existing reminder definition files

## Decisions

### 1. Append-only `.jsonl` per reminder, not a shared SQLite table

Each reminder gets its own `{id}.history.jsonl` alongside its `{id}.json`
definition file. Entries are newline-delimited JSON objects, one per line.

**Why over SQLite:** No schema, no migrations, no additional dependency. Files
are directly inspectable and greppable. The per-reminder split means reads and
trims never touch other reminders' data. At 500 records × ~200 bytes, each
file stays under 100 KB.

**Why over a shared JSON array:** Appending a line is O(1) and safe under
single-writer conditions (only one `ReminderExecutionActor` per reminder ID
runs at a time due to the concurrency gate). A JSON array requires reading
and rewriting the whole file on every append.

### 2. Trim on overflow via full-file rewrite

When `AppendAsync` would push the record count past `HistoryMaxRecords`, the
store reads all lines, drops the oldest `(count - max + 1)`, and rewrites
the file. This is O(n) on a small file and happens at most once per execution.
No background compaction job is needed.

**Why not rotation:** Rolling files (`.history.1.jsonl`, etc.) complicate the
CLI read path and add no value for a 500-record cap.

### 3. `ReminderHistoryStore` — file-backed, non-actor

A plain `ReminderHistoryStore` class (not an actor) owns append and read
logic. `ReminderExecutionActor` receives it via DI and calls
`AppendAsync(ReminderId, HistoryRecord)` immediately after sending its
completion/failure message back to the manager.

**Why non-actor:** The write is a local file operation scoped to one
execution. Making it an actor adds lifecycle complexity with no concurrency
benefit — the existing concurrency gate (max 3 executions) already ensures
at most 3 concurrent writers for different reminder IDs.

**Failure mode:** If the file write fails, log a warning and continue. History
loss is acceptable; it must never block or fail the execution report back to
the manager.

### 4. Write happens in `ReminderExecutionActor` before actor stops

The actor sends `ExecutionCompleted`/`ExecutionFailed` to the manager and
then calls `AppendAsync` before stopping. This ordering ensures the manager
is unblocked even if the write is slow, and the actor's stop is deferred only
by the (fast) local file I/O.

### 5. `GetReminderHistoryTool` returns structured records, not formatted text

The tool returns a list of `HistoryRecord` objects so the agent can reason
about patterns (e.g., "last 3 runs failed"). Formatting is left to the LLM.
Default return count: 20. Max: 100 (to stay within a reasonable token budget).

### 6. CLI `--last N` default: 20, formatted as a table

The CLI command reads the tail of the `.jsonl` file and renders a table with
columns: `fired_at`, `status`, `duration`, `session_id`. If no history file
exists for the ID, a clear "no history found" message is shown rather than an
error.

## Risks / Trade-offs

- **Concurrent writes for same reminder ID**: Structurally prevented — the
  concurrency gate defers execution if another instance of the same reminder
  is running. If this invariant is ever relaxed, file locking will be needed.
  → Mitigation: Document the assumption; add an assertion in `ReminderHistoryStore`
  that the append path is single-writer per ID.

- **Trim rewrite on power loss**: A partial rewrite leaves a truncated file.
  → Mitigation: Write to a `.tmp` file then atomic rename. Standard pattern for
  file-backed stores already used by `ReminderDefinitionStore`.

- **History file for deleted reminder**: If a reminder is deleted, its
  `.history.jsonl` is left on disk.
  → Mitigation: `CancelReminderTool` and the CLI `delete` command also delete
  the history file. If the file is absent, the CLI and tool return empty results
  gracefully.

- **Token cost of full history in agent tool**: Returning 500 records to the
  LLM would be wasteful.
  → Mitigation: Hard cap of 100 records on the tool; default 20.

## Migration Plan

No migration required. History files are created on first execution after
the feature ships. Existing reminders with no history file return empty
results — this is the correct behavior for "no recorded history yet."

No changes to existing `.json` definition file format or Akka.Reminders
timer state.

## Open Questions

- Should `netclaw reminder delete` prompt before deleting a non-empty history
  file, or always delete silently? Current preference: silent delete (consistent
  with how definition files are handled).

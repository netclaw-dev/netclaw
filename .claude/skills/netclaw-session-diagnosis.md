# Netclaw Session Diagnosis

Use this skill when investigating Netclaw session quality issues — identity
failures, memory recall misses, skill loading failures, amnesia, or unexpected
behavior reported from production sessions.

## Log Locations

| Log Type | Path | Format |
|----------|------|--------|
| Session logs | `~/.netclaw/sessions/{sanitized_id}/logs/{timestamp}.log` | `[ISO 8601] {message}` |
| Legacy session logs | `~/.netclaw/logs/sessions/{timestamp}_{sanitized_id}.log` | Same (pre-0.7.2) |
| Daemon logs | `~/.netclaw/logs/daemon-{YYYY-MM-DD}.log` | `HH:mm:ss.fff [LVL] Category: message` |
| Crash logs | `~/.netclaw/logs/crash-{yyyyMMdd-HHmmss}.log` | Full stack trace |
| Headless logs | `~/.netclaw/logs/headless-{guid}.log` | `[ISO 8601] EVENT: data` |
| Reminder logs | `~/.netclaw/sessions/reminder_{name}_{id}/logs/{timestamp}.log` | Same as session |

**Session ID sanitization:** `/` → `_`, `.` → `_`
Example: `D0AC6CKBK5K/1774023557.531309` → `D0AC6CKBK5K_1774023557_531309`

**Multiple log files** in a session's `logs/` directory = the session passivated
(idle timeout or daemon restart) and was rehydrated. Each file is one lifecycle.

**Per-session directory layout:**
```
~/.netclaw/sessions/{sanitized_id}/
├── logs/           # Timestamped log files (one per lifecycle)
│   ├── 20260320-154444.log
│   └── 20260320-154847.log
└── media/          # Session-scoped temp files, attachments
```

## Investigation Workflow

Given a session ID (e.g., `D0AC6CKBK5K/1774023557.531309`):

### Step 1: Find all session log files

```bash
SANITIZED="D0AC6CKBK5K_1774023557_531309"
# New location (0.7.2+)
ls -la ~/.netclaw/sessions/${SANITIZED}/logs/
# Legacy location (pre-0.7.2)
ls -la ~/.netclaw/logs/sessions/*${SANITIZED}* 2>/dev/null
```

Multiple files = passivation cycles. Note timestamps to identify gaps.

### Step 2: Check turn completions

```bash
grep "Turn.*completed" ~/.netclaw/sessions/${SANITIZED}/logs/*.log
```

- Present: turns were persisted to journal. State survives restart.
- **Absent: turns were never committed.** All in-flight work is lost on
  restart/passivation. This is the smoking gun for unpersisted state loss.

### Step 3: Check daemon log for recovery state

```bash
DATE="2026-03-20"  # adjust to session date
grep "D0AC6CKBK5K/1774023557.531309" ~/.netclaw/logs/daemon-${DATE}.log | grep "Recovery complete"
```

Output: `Recovery complete (turns=N, history=N)`
- `turns=0, history=0` → brand new session or state was lost
- `turns=N, history=M` → recovered from journal/snapshot

### Step 4: Check memory recall

```bash
grep "turn_memory_recall" ~/.netclaw/logs/daemon-${DATE}.log | grep "D0AC6CKBK5K/1774023557.531309"
```

Key fields:
- `degraded=True` → recall coordinator failed, returned empty
- `itemCount=0` → no memories matched the query
- `itemCount=N` → N memories recalled, check `itemIds`
- `durationMs=0` → instant return (possibly empty store or fast search)
- `durationMs>300` → approaching the 300ms timeout

### Step 5: Check skill auto-loading

```bash
grep "turn_skill_auto_load" ~/.netclaw/logs/daemon-${DATE}.log | grep "D0AC6CKBK5K/1774023557.531309"
```

- Present: shows which skills loaded
- **Absent: no skills auto-loaded.** Check that skill index is in system prompt.

### Step 6: Check for daemon restarts

```bash
grep "ConfigWatcherService" ~/.netclaw/logs/daemon-${DATE}.log
```

- `Config change detected` → config file was modified → restart triggered
- Check for `file_write` tool calls to `netclaw.json` nearby in the daemon log

### Step 7: Check for errors

```bash
grep "\[ERR\]\|\[WRN\]" ~/.netclaw/logs/daemon-${DATE}.log | grep "D0AC6CKBK5K/1774023557.531309" | head -20
```

### Step 8: Check skill index in system prompt

The skill index is a compressed pipe-delimited listing injected into the
system prompt at session start. It points the agent at SKILL.md files on disk.
If skills are not being discovered, check:

```bash
# Verify skills are registered
grep "skill.*scan\|SkillScanner" ~/.netclaw/logs/daemon-${DATE}.log | tail -10
```

- Skills appear in scan results → index should be populated
- Scan issues logged → check for frontmatter validation failures

## Data Store Queries

SQLite memory store at `~/.netclaw/netclaw.db`:

```bash
# Count memories by domain
sqlite3 ~/.netclaw/netclaw.db "SELECT domain, COUNT(*) FROM memory_documents GROUP BY domain;"

# Search for specific memories
sqlite3 ~/.netclaw/netclaw.db "SELECT document_id, title, domain FROM memory_documents WHERE title LIKE '%keyword%';"

# Check session catalog
sqlite3 ~/.netclaw/netclaw.db "SELECT persistence_id, turn_count, status, title FROM sessions ORDER BY last_activity DESC LIMIT 10;"
```

## Common Failure Patterns

| Symptom | Log Pattern | Root Cause |
|---------|-------------|------------|
| Identity failure ("I'm OpenClaw") | No `turn_skill_auto_load` + zero recall + SOUL.md lacks bot identity | Missing identity grounding (#327) |
| Post-passivation amnesia | Multiple log files, `Turn 1` in later file | Transient state lost on rehydration (#315) |
| Config overwrite → restart | `file_write` to `netclaw.json` + `ConfigWatcherService` restart | Bot edited config, daemon restarted (#326) |
| Hallucinated action/inaction | Tool call in history but bot claims otherwise | LLM reasoning failure (#324) |
| Zero memory recall every turn | `itemCount=0` + `durationMs=0` on all turns | Empty domain, bad search terms, or planner failure (#329) |
| Skills never load | No `turn_skill_auto_load` in daemon log | Enrichment race (#316), "netclaw" blacklisted (#328) |
| Turn never completed | Zero `Turn N completed` in session log | LLM in tool call loop, or daemon restarted mid-turn |

## Code References

| Component | File |
|-----------|------|
| Session log actor | `src/Netclaw.Actors/Sessions/SessionLogActor.cs` |
| LLM session actor | `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` |
| Skill registry & matching | `src/Netclaw.Actors/Skills/SkillRegistry.cs` |
| Skill enrichment & generic keywords | `src/Netclaw.Daemon/Services/SystemSkillSyncService.cs` |
| Memory recall coordinator | `src/Netclaw.Actors/Sessions/SQLiteMemoryRecallCoordinator.cs` |
| Config watcher → restart | `src/Netclaw.Daemon/Services/ConfigWatcherService.cs` |
| System prompt / identity | `src/Netclaw.Configuration/ISystemPromptProvider.cs` |
| Session directory helper | `src/Netclaw.Actors/Protocol/SessionDirectoryHelper.cs` |
| Paths (directory layout) | `src/Netclaw.Configuration/NetclawPaths.cs` |

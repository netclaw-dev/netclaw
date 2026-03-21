# Netclaw Eval Suite

Behavioral eval suite that tests identity, skill loading, memory, tool use,
grounding, and autonomy against a running Netclaw daemon instance.

## Quick Start

```bash
# Ensure daemon is running
netclaw daemon start

# Run the full suite
./evals/run-evals.sh
```

## What It Tests

The suite runs prompts via `netclaw -p` and verifies both **stdout output**
(tool calls, text content) and **daemon log patterns** (skill loading, memory
recall, checkpoint formation).

| Category | Cases | What It Validates |
|----------|-------|-------------------|
| Identity & Self-Awareness | 4 | Bot knows its name, version, repo, session ID |
| Skill Auto-Loading | 4 | Keyword matching triggers correct skills |
| Memory Pipeline | 2 | Memory recall is active, new memories form |
| Tool Discovery & Use | 4 | Progressive tool discovery and invocation |
| Grounding & Alignment | 3 | Uses tools to verify facts, admits uncertainty |
| Autonomy & Execution | 2 | Executes tasks rather than describing them |
| Complex Task Execution | 3 | Multi-step tool chains complete successfully |

## How It Works

1. Verifies daemon is running via `netclaw daemon status`
2. For each eval case, picks a random prompt variant from the case's list
3. Runs the prompt via `netclaw -p "prompt"`, capturing stdout
4. Records daemon log position before/after to isolate new entries
5. Runs assertion functions against stdout and daemon log tail
6. Repeats N times per case (default: 5) to account for LLM non-determinism
7. A case passes if it meets the threshold across runs (default: 80%)

### Prompt Variants

Each case defines multiple natural phrasings of the same intent. Each run picks
a random variant, testing whether behavior is robust across phrasing — not just
one magic prompt.

### Assertion Types

- **stdout assertions** — check `netclaw -p` output for tool calls (`[tool:call]`),
  text content, or absence of hallucinated content
- **daemon log assertions** — check daemon log for structured patterns like
  `turn_skill_auto_load`, `turn_memory_recall`, `turn_memory_checkpoint_enqueued`

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `NETCLAW_EVAL_RUNS` | `5` | Runs per case |
| `NETCLAW_EVAL_THRESHOLD` | `0.80` | Pass threshold (0.0-1.0) |
| `NETCLAW_EVAL_TIMEOUT` | `180` | Per-prompt timeout in seconds |
| `NETCLAW_BIN` | `netclaw` | Path to netclaw binary |
| `NETCLAW_HOME` | `~/.netclaw` | Netclaw home directory |
| `NETCLAW_EVAL_DAEMON_LOG` | `~/.netclaw/logs/daemon-YYYY-MM-DD.log` | Daemon log path |

### Examples

```bash
# Quick smoke test (1 run, lower threshold)
NETCLAW_EVAL_RUNS=1 NETCLAW_EVAL_THRESHOLD=0.50 ./evals/run-evals.sh

# Thorough run (10 iterations)
NETCLAW_EVAL_RUNS=10 ./evals/run-evals.sh

# Use a specific netclaw binary
NETCLAW_BIN=/usr/local/bin/netclaw ./evals/run-evals.sh
```

## Results Database

Results are stored in `~/.netclaw/evals/results.db` (SQLite) for trend analysis.
Requires `sqlite3` CLI — if not available, the script still runs but skips
persistence.

### Trend Queries

```bash
# Score by version
sqlite3 ~/.netclaw/evals/results.db \
  "SELECT netclaw_ver, AVG(overall_score) FROM eval_runs GROUP BY netclaw_ver;"

# Case pass rate over time
sqlite3 ~/.netclaw/evals/results.db \
  "SELECT r.netclaw_ver, e.case_name, AVG(e.passed)
   FROM eval_results e JOIN eval_runs r ON e.run_id = r.run_id
   GROUP BY r.netclaw_ver, e.case_name;"

# Worst performing cases
sqlite3 ~/.netclaw/evals/results.db \
  "SELECT case_name, AVG(passed) as rate FROM eval_results
   GROUP BY case_name ORDER BY rate ASC LIMIT 10;"
```

## Adding New Cases

1. Define an assertion function in the "Case Assertion Functions" section:
   ```bash
   assert_my_new_case() {
       stdout_contains '\[tool:call\] my_tool' && stdout_contains 'expected text'
   }
   ```

2. Add the case to the appropriate category in `run_all()`:
   ```bash
   run_case my_new_case "description of pass criteria" \
       "Prompt variant 1" \
       "Prompt variant 2" \
       "Prompt variant 3"
   ```

### Assertion Helpers

| Helper | Description |
|--------|-------------|
| `stdout_contains 'pattern'` | Case-insensitive grep of stdout (basic regex) |
| `stdout_not_contains 'pattern'` | Inverse of above |
| `daemon_log_contains 'pattern'` | Extended regex grep of daemon log entries added during the prompt |

### When to Add Cases

- New system skill added -> add a skill auto-load case
- New tool added -> add a tool discovery/use case
- Identity grounding rules changed -> update identity assertions
- Production session failure -> add a regression case

## Scoring

- **Per-case:** passes / total runs >= threshold -> case passes
- **Per-category:** GREEN (all pass), YELLOW (>= 80%), RED (< 80%)
- **Overall:** cases passed / total cases
- **Exit code:** 0 if all cases pass, 1 if any fail

## Limitations (v1)

- **Local instance only** — tests against the running daemon, same database and
  identity files. No isolation between evals and production.
- **Single-turn only** — `netclaw -p` is one prompt per session. Multi-turn
  conversation evals are deferred.
- **No clean-slate testing** — tests current production state, not fresh-install.
  Isolated instance support depends on configurable `NETCLAW_HOME` and bind address.

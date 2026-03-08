# Memory Health And Eval Runbook

Use this runbook when validating the SQLite memory subsystem, checkpoint curation,
and automatic recall quality.

## Runtime Health Checks

1. Start daemon status check:

```bash
netclaw status
```

2. Confirm memory status section shows:
   - `provider: sqlite`
   - `status: healthy` (or `degraded` when unavailable)
   - `databasePath: ~/.netclaw/memory/netclaw-memory.db`
   - `pendingCheckpoints: <n>`

3. Run offline diagnostics:

```bash
netclaw doctor
```

4. Review `Memory Checkpoint Health`:
   - `PASS` when backlog is small
   - `WARNING` when pending checkpoints exceed backlog threshold

## Inspect Pending Checkpoints Directly

```bash
sqlite3 "$HOME/.netclaw/memory/netclaw-memory.db" \
  "select status, count(*) from memory_checkpoints group by status;"
```

```bash
sqlite3 "$HOME/.netclaw/memory/netclaw-memory.db" \
  "select checkpoint_id, trigger_type, priority, retry_count, created_at from memory_checkpoints where status='pending' order by priority desc, created_at asc limit 20;"
```

## Subagent Findings Audit Surface

Subagent-originated memory candidates are surfaced as session `SubAgentOutput`
completion events with:

- `memoryDecision` (`accepted`, `deferred`, `rejected`)
- `memoryDecisionReason` (when decision is not accepted)
- `findingsCount`

Only `accepted` findings are enqueued into the memory checkpoint pipeline.

## Eval Execution

Run the seeded memory quality tests:

```bash
dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj --filter "FullyQualifiedName~SubAgentActorTests"
dotnet test src/Netclaw.Cli.Tests/Netclaw.Cli.Tests.csproj --filter "FullyQualifiedName~MemoryCheckpointHealthDoctorCheckTests|FullyQualifiedName~DaemonClientMappingTests"
```

Run quality gate checks:

```bash
dotnet slopwatch analyze
```

## Local Ollama Gate Profile

Use local small models as the default memory gate before larger hosted models:

```bash
netclaw doctor
```

Recommended model profile values in `~/.netclaw/config/netclaw.json`:

- provider: `ollama`
- model: `qwen2.5:3b-instruct` (or equivalent small local model)
- recall budget: max 3 auto-recall items

Passing a larger hosted model run does not waive a failing local Ollama run.

If feed-published system skills lag behind local source changes, disable startup
skill feed sync in `~/.netclaw/config/netclaw.json` to force use of local
built-in skill copies:

```bash
python3 - <<'PY'
import json, pathlib
p = pathlib.Path.home() / '.netclaw' / 'config' / 'netclaw.json'
obj = json.loads(p.read_text())
obj.setdefault('SkillSync', {})['DisableSystemSkillSync'] = True
p.write_text(json.dumps(obj, indent=2) + '\n')
print(p)
PY
```

Then restart the daemon from local binaries before running evals.

## Reproducible Memory Score (Non-LLM Judge)

Run the deterministic memory score script:

```bash
scripts/evals/memory-score.sh
```

Suites:

- `SUITE=smoke` (default): direct retrieval checks and pipeline sanity.
- `SUITE=realistic`: indirect/paraphrased prompts for stronger recall validation.

Profiles:

- `PROFILE=fast` (default): tuned for 9B-ish local models.
- `PROFILE=slow`: longer prompt timeout for larger/slower models (e.g. 27B+).

Optional overrides:

```bash
RUNS=3 \
SUITE=realistic \
PROFILE=slow \
DB_PATH="$HOME/.netclaw/netclaw.db" \
LOG_PATH="$HOME/.netclaw/logs/daemon-$(date +%F).log" \
scripts/evals/memory-score.sh
```

Outputs:

- `artifacts/evals/memory/eval-results.json`
- `artifacts/evals/memory/eval-summary.md`

Scoring model (100 points total):

- Recall hit rate: 30
- Noise suppression: 20
- Privacy/sensitivity leaks: 20 (hard gate)
- Update correctness: 10
- Checkpoint/curation reliability: 10
- Latency SLO adherence: 10

Hard gates:

- Any privacy leak fails the run.
- Deploy candidate requires score >= 85 and no hard-gate failure.

This eval uses deterministic fixture seeding, structured memory/turn observability,
and SQLite/log inspection. It does not use another LLM to grade outputs.

For recall miss diagnostics in realistic suites, inspect daemon logs for:

- `memory_recall_query_trace` (query terms, fallback terms, selected IDs)
- `turn_memory_recall` (bundle size and IDs)
- `turn_memory_checkpoint_enqueued` + `Memory checkpoint curation completed`

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
   - `databasePath: <NETCLAW_HOME>/netclaw.db`
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
sqlite3 "${NETCLAW_HOME:-$HOME/.netclaw}/netclaw.db" \
  "select status, count(*) from memory_checkpoints group by status;"
```

```bash
sqlite3 "${NETCLAW_HOME:-$HOME/.netclaw}/netclaw.db" \
  "select checkpoint_id, trigger_type, priority, retry_count, created_at from memory_checkpoints where status='pending' order by priority desc, created_at asc limit 20;"
```

## Subagent Findings Audit Surface

Subagent-originated memory candidates are surfaced as session `SubAgentOutput`
completion events with:

- `outcome` (`completed`, `partial`, `failed`)
- `outcomeReason` (when the run ended for a machine-readable non-completed reason)
- `memoryDecision` (`accepted`, `deferred`, `rejected`)
- `memoryDecisionReason` (when decision is not accepted)
- `findingsCount`

Only `accepted` findings are enqueued into the memory checkpoint pipeline.
Partial runs can still produce accepted findings; failed runs are treated as
operator-visible diagnostics rather than durable-memory evidence by default.

## Eval Execution

Run the provider-independent memory quality tests:

```bash
dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj --filter "FullyQualifiedName~MemoryRedesignedEvalSuiteTests|FullyQualifiedName~MemoryEvalSeedSuiteTests"
dotnet test src/Netclaw.Cli.Tests/Netclaw.Cli.Tests.csproj --filter "FullyQualifiedName~MemoryCheckpointHealthDoctorCheckTests|FullyQualifiedName~DaemonClientMappingTests"
```

Redesigned eval coverage now includes:

- `formation_then_auto_recall`
- `formation_then_intentional_search`
- `evidence_vs_durable_separation`
- `proposal_gate_rejection`
- `soul_boundary`
- `expiry_and_staleness`

These suites are synthetic/sanitized and do not require live provider credentials.

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

## Relevance Gate Health

The relevance gate (`memory-relevance-gate`) is a post-floor cross-encoder
stage: for each of the (≤3) candidates that already cleared the cosine
floor, a small ONNX model (`ms-marco-minilm-l-6-v2`) scores `(query,
candidate)` jointly and drops anything below the calibrated threshold.
Activation follows `Memory.Embeddings.Enabled` unless
`Memory.Recall.RelevanceGate.Enabled`/`Threshold` explicitly override it.

1. Run offline diagnostics and review the `Memory Relevance Gate` check:

```bash
netclaw doctor
```

   - `PASS` + "disabled (follows Memory.Embeddings.Enabled...)" or "disabled
     (Memory.Recall.RelevanceGate.Enabled is explicitly false)" — expected,
     healthy state for any deployment that hasn't opted into embeddings, or
     that opted out of the gate specifically. Not an error.
   - `PASS` + "Relevance gate healthy: model '...' provisioned (threshold
     ...)" — the model is present, hash-verified, and its manifest-carried
     (or config-overridden) threshold is reported.
   - `ERROR` + "missing or fails hash verification at `<path>`" — the model
     was never provisioned or the on-disk artifact doesn't match the pinned
     SHA-256. Restart the daemon to re-provision if `AutoDownload` is
     enabled; otherwise provision manually and restart.

2. Check the degradation log line. When the gate is skipped for a turn
   (model unavailable, its sub-budget exceeded — a 120 ms ceiling clamped to
   whatever remains of the outer 300 ms `Memory.RecallTimeoutMs` envelope, so
   a turn where earlier stages already ran long gets less than 120 ms; raised
   from a fixed 60 ms by a 2026-07 production-canary finding of cold-start
   timeouts — or recall running in lexical mode because there's no query
   vector), the coordinator logs a rate-limited marker instead of silently
   changing what gets injected:

```
memory_recall_gate_degraded session=<id> reason=<reason> elapsedMs=<ms>
```

   `reason` is one of `gate_disabled_by_config`, `no_scorer_configured`,
   `scorer_unavailable`, `sub_budget_exceeded`, or `score_failed:<ExceptionType>`.
   `elapsedMs` is 0 for the first three (no scoring attempt ever started) and
   the measured time spent before degrading for the latter two — useful for
   telling a genuine cold-start/contention timeout apart from an instant
   failure. Logged at `Warning` when the gate is enabled but a turn still
   degraded (a genuine runtime condition worth noticing); logged at `Debug`
   when the gate is off by config (the default, intentional state — not
   spam). Rate-limited per-reason with the same cooldown as
   `memory_recall_vector_degraded`, so expect at most one `Warning` line per
   reason per cooldown window even under sustained degradation, not one per
   turn.

3. Read `gateScores`/`droppedByGate`/`gateElapsedMs` on `memory_retrieval_final`
   when diagnosing over- or under-injection or quantifying gate latency
   margin against the 120 ms ceiling:

```bash
grep memory_retrieval_final "$HOME/.netclaw/logs/daemon-$(date +%F).log" | tail -20
```

   - `droppedByGate` — how many of the floor's survivors the gate rejected
     this turn. `0` on a turn that also injected nothing means the floor
     itself already filtered everything (or the gate didn't run); a nonzero
     `droppedByGate` with zero final `injectedCount` means the gate is the
     reason nothing was injected, not the floor.
   - `gateScores` — the cross-encoder score for every candidate the gate
     scored (`id=score`, e.g. `doc-abc123=0.014`), regardless of whether it
     survived. Compare against the active threshold (config override, or the
     manifest's calibrated default reported by the doctor check) to see how
     close a dropped candidate came, or how comfortably a survivor cleared
     the bar. Absent `gateScores` (empty) on a hybrid-mode turn is itself a
     signal the gate didn't run for that turn — check for a paired
     `memory_recall_gate_degraded` line first before assuming a config
     problem.
   - Zero `gateScores` and zero `droppedByGate` on a turn is normal whenever
     the floor itself already produced zero survivors — the gate never runs
     against an empty candidate set. This is not a gate failure.

See `openspec/changes/memory-relevance-gate/design.md` for the calibration
procedure (threshold-sweep protocol, model shoot-out, and out-of-sample
validation numbers) if the operating point ever needs to be re-verified
against a different relevance model or corpus.

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

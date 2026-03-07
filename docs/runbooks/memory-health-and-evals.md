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

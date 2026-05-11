# Netclaw Eval Suite

Behavioral eval suite that tests identity, skill loading, memory, tool use,
grounding, and autonomy against an ephemeral `netclawd` Docker container.
Completely isolated from the operator's real `~/.netclaw` state.

## Quick Start

```bash
# One-time: run netclaw init on the host so the eval script can borrow
# your identity files (SOUL.md, AGENTS.md, TOOLING.md).
netclaw init

# Run the full suite against your preferred LLM endpoint.
NETCLAW_EVAL_PROVIDER_TYPE=ollama \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://my-gpu-server.tailnet.ts.net:11434 \
NETCLAW_EVAL_MODEL_ID=qwen3:30b \
  ./evals/run-evals.sh
```

If any of `NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, or
`NETCLAW_EVAL_MODEL_ID` is unset, the script prompts for the missing values
on stdin (requires a terminal). In non-interactive contexts (CI, piped
scripts) the script fails loudly — it never silently falls back to a
default provider.

## How It Works

1. `scripts/docker/build-image.sh dev` (or a published release image) builds
   the `netclawd` Docker image.
2. On every invocation, `run-evals.sh` spins up an ephemeral container
   from that image with `docker run --rm --network host`, a throwaway
   `$EVAL_HOME` temp directory, and `NETCLAW_*` env vars that route it at
   your LLM endpoint.
3. Identity files are **copied** from `~/.netclaw/identity/` into
   `$EVAL_HOME/identity/` (never bind-mounted from the real location, so
   the operator's real identity cannot be mutated).
4. Daemon logs land in `$EVAL_HOME/logs/daemon-YYYY-MM-DD.log` via a
   writable bind-mount of `/root/.netclaw/logs`. Assertion helpers tail
   this file with per-prompt offsets, exactly like the pre-container
   version did.
5. The CLI is pointed at the eval daemon via
   `NETCLAW_DAEMON_ENDPOINT=http://127.0.0.1:$EVAL_PORT` and its own path
   resolution is sandboxed via `NETCLAW_HOME=$EVAL_HOME`. The host's
   `~/.netclaw/` is never touched by the CLI during the run.
6. On exit (success, failure, or SIGINT) the container is stopped and
   `$EVAL_HOME` is deleted. A throwaway root-in-container cleanup step
   handles files the daemon wrote as UID 0.

`--network host` is the default because operators often host their LLM on
a Tailscale node — MagicDNS hostnames like `my-gpu-server.tailnet.ts.net` only
resolve when the container shares the host's DNS resolver. macOS/Windows
operators need a different endpoint resolution strategy (Docker Desktop
reduces `--network host` to bridge mode).

## What It Tests

The suite runs prompts via `netclaw chat -p` against the eval container and
verifies both **stdout output** (tool calls, text content) and **daemon
log patterns** (skill loading, memory recall, checkpoint formation).

| Category | Cases | What It Validates |
|----------|-------|-------------------|
| Identity & Self-Awareness | 4 | Bot knows its name, version, repo, session ID |
| Skill Auto-Loading | 4 | Keyword matching triggers correct skills |
| Memory Pipeline | 4 | Memory recall is active, identity-vs-memory routing is correct, explicit saves use memory tools, and automatic checkpointing still fires |
| Tool Discovery & Use | 4 | Progressive tool discovery and invocation |
| Grounding & Alignment | 3 | Uses tools to verify facts, admits uncertainty |
| Autonomy & Execution | 2 | Executes tasks rather than describing them |
| Complex Task Execution | 3 | Multi-step tool chains complete successfully |
| Multi-Turn Conversation | 7 | Session resume and speaker attribution recall |

Each case defines multiple natural phrasings of the same intent. Each
run picks a random variant, testing whether behavior is robust across
phrasing — not just one magic prompt.

### Assertion Types

- **stdout assertions** — check `netclaw chat -p` output for tool calls
  (`[tool:call]`), text content, or absence of hallucinated content.
- **daemon log assertions** — check the daemon's file log (tailed from
  `$EVAL_HOME/logs/daemon-$(date +%F).log`) for structured patterns like
  `turn_skill_auto_load`, `turn_memory_recall`, and
  `turn_memory_checkpoint_enqueued`.

### Memory Pipeline Semantics

The memory category intentionally separates three behaviors that used to be
conflated by a single case:

- **Identity preference routing** validates that personal preferences route into
  `SOUL.md` through identity-file edits when identity guidance says they should
  shape future sessions.
- **Explicit memory write** validates that a direct save request results in a
  `store_memory` tool call.
- **Automatic checkpoint enqueue** validates that the session enqueues a memory
  checkpoint for non-identity facts without taking an explicit memory-write tool
  path.

This means `memory_checkpoint_enqueue` is the case to watch for automatic memory
formation regressions, while `memory_identity_preference_routing` and
`memory_explicit_store` cover user-facing routing behavior.

## Environment Variables

### Eval target (required)

| Variable | Description |
|----------|-------------|
| `NETCLAW_EVAL_PROVIDER_TYPE` | Provider type (`ollama`, `openai`, `openai-compatible`, `openrouter`, `anthropic`) |
| `NETCLAW_EVAL_PROVIDER_ENDPOINT` | Provider URL the container should call |
| `NETCLAW_EVAL_MODEL_ID` | Main model id |

If any of these is unset and stdin is a terminal, the script prompts for
the missing values. In non-interactive contexts it fails loudly.

### Eval target (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `NETCLAW_EVAL_FALLBACK_MODEL_ID` | `NETCLAW_EVAL_MODEL_ID` | Fallback model id |
| `NETCLAW_EVAL_COMPACTION_MODEL_ID` | `NETCLAW_EVAL_MODEL_ID` | Compaction model id |
| `NETCLAW_EVAL_CONTEXT_WINDOW` | — | Override `Models:Main:ContextWindowTokens` — useful for triggering compaction in future eval cases |

### Container + runtime (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `NETCLAW_IMAGE` | `ghcr.io/netclaw-dev/netclaw:latest` | Image ref |
| `NETCLAW_EVAL_PORT` | `5299` | Host-side port for the eval daemon |
| `NETCLAW_BIN` | `netclaw` | Path to the netclaw CLI on the host |

### Eval suite knobs (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `NETCLAW_EVAL_RUNS` | `5` | Runs per case |
| `NETCLAW_EVAL_THRESHOLD` | `0.80` | Pass threshold (0.0-1.0) |
| `NETCLAW_EVAL_TIMEOUT` | `60` | Per-prompt timeout in seconds |

### Examples

```bash
# Quick smoke test (1 run, lower threshold)
NETCLAW_EVAL_PROVIDER_TYPE=ollama \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://my-gpu-server:11434 \
NETCLAW_EVAL_MODEL_ID=qwen3:30b \
NETCLAW_EVAL_RUNS=1 NETCLAW_EVAL_THRESHOLD=0.50 \
  ./evals/run-evals.sh

# Run against a locally-built dev image
NETCLAW_IMAGE=ghcr.io/netclaw-dev/netclaw:dev \
NETCLAW_EVAL_PROVIDER_TYPE=ollama \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://127.0.0.1:11434 \
NETCLAW_EVAL_MODEL_ID=qwen3:30b \
  ./evals/run-evals.sh
```

## Results Database

Results are stored in `$EVAL_HOME/evals/results.db` (SQLite) inside the
per-run throwaway directory, NOT under `~/.netclaw/`. This means results
don't persist across runs by default — on script exit, the database is
deleted along with `$EVAL_HOME`.

If you want to retain results for trend analysis, copy the database out
of `$EVAL_HOME` before the EXIT trap fires (look for the "Results:
..."  line at the bottom of the script output to get the path). A
dedicated results-retention follow-up may add a `NETCLAW_EVAL_RESULTS_DB`
override.

Requires `sqlite3` CLI — if not available, the script still runs but
skips persistence.

## Adding New Cases

1. Define an assertion function in the "Case Assertion Functions"
   section:

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

- New system skill added → add a skill auto-load case
- New tool added → add a tool discovery/use case
- Identity grounding rules changed → update identity assertions
- Production session failure → add a regression case

## Scoring

- **Per-case:** passes / total runs >= threshold → case passes
- **Per-category:** GREEN (all pass), YELLOW (>= 80%), RED (< 80%)
- **Overall:** cases passed / total cases
- **Exit code:** 0 if all cases pass, 1 if any fail, 2 if the eval
  container died during startup or mid-run

## Prerequisites

- `docker` (the host needs a working Docker daemon)
- `netclaw` CLI installed on the host (`curl` install script or local build)
- `~/.netclaw/identity/SOUL.md` — run `netclaw init` once on the host
- `timeout`, `curl`, `awk` (coreutils, standard on most Linux distros)
- `sqlite3` (optional — results persistence degrades gracefully without it)

## Limitations (v2)

- **Local LLM required**: the eval container needs to reach an LLM
  endpoint the operator supplies. CI execution is not yet wired up — it
  requires a remote LLM endpoint secret and runtime budget. Track as a
  follow-up.
- **`--network host` is Linux-only**: the Tailscale MagicDNS use case
  depends on inheriting the host's DNS resolver. Docker Desktop
  (macOS/Windows) degrades `--network host` to bridge mode; set
  `NETCLAW_EVAL_PROVIDER_ENDPOINT` to a reachable IP/hostname instead.
- **Multi-turn support**: `netclaw chat -p --resume <id>` enables multi-turn
  scripted conversations against a named session.
- **No native ACL/authority eval mode yet**: the current scored runner exercises
  multi-turn attribution behavior, but it does not yet simulate restricted
  channel posture with distinct authorized vs unauthorized speakers.
- **Identity is borrowed from host**: the container does not
  self-bootstrap identity. CI will need a committed fixture under
  `evals/fixtures/identity/` — tracked as a follow-up.
- **Daemon does not fail fast on empty config**: a follow-up task will
  make `netclawd` refuse to start when identity or provider config is
  missing. Today, missing config produces a running-but-broken daemon
  whose LLM calls fail at request time. Not relevant to the eval path
  itself (the script always supplies valid config) but noted for anyone
  exploring the Docker image directly.

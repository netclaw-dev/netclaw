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

Set `NETCLAW_EVAL_PROVIDER_API_KEY` when the selected provider requires an API
key. The harness passes it only to the ephemeral provider configuration and
does not write it into run metadata.

If the value uses Netclaw's `ENC:` form, set
`NETCLAW_EVAL_DATA_PROTECTION_KEYS` to its key-ring directory. The harness
copies those keys only into the throwaway eval home and excludes them from
archived results.

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

The harness preloads `evals/fixtures/config/netclaw.json` into the ephemeral
home before startup. It auto-approves tools and grants read/write access for the
Personal audience because headless sessions cannot answer approval prompts or
edit an interactive trust policy. A companion `tool-approvals.json` trusts Git
for shell-based coding cases. Tool exposure and command-deny rules still apply,
and these policies are never copied into an operator's config.

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
| Identity & Self-Awareness | 5 | Bot knows its name, version, repo, session ID, and routes all identity-file concerns without a skill dependency |
| Skill Discovery and Activation | 20 | Models load relevant file, feed, and MCP prompt skills while they skip unrelated skills |
| Memory Pipeline | 4 | Memory recall is active, identity-vs-memory routing is correct, explicit saves use memory tools, and automatic checkpointing still fires |
| Tool Discovery & Use | 16 | Progressive discovery, structured workspace selection, web search, and timestamped webhook configuration |
| Grounding & Alignment | 4 | Uses tools to verify facts, admits uncertainty, and resolves announced attachment paths from the authoritative session root |
| Autonomy & Execution | 2 | Executes tasks rather than describing them |
| Deployment Mission | 1 | Applies the disk mission playbook, loads its required skill, and returns reviewed sales email |
| Subagents | 3 | Delegates through `spawn_agent`, completes ambiguous work, preserves specialized guidance, and declares a different named project before shell inspection |
| Coding Context | 1 | Repeatedly switches between isolated linked worktrees, alternates branch and one-of-four target files by run, and verifies Git grounding, wrong-file/worktree safety, and path-free child handoff |
| Session Storage | 4 | Verifies managed temporary APIs, parent-child log handoff, and managed worktree creation |
| Complex Task Execution | 5 | Multi-step tool chains complete successfully, incl. bounded tool output — given only the goal (no handling hints), the agent retrieves a deep line from oversized shell output and from a large file, which is only possible by coping with the bound the way AGENTS.md/skills/steer text direct |
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

### Tool Cycle Cases

The cycle cases use the existing harness and provider relay. They require an
OpenAI-compatible endpoint with a `/v1` API base. The default suite excludes them.
Select the category or one `tool_cycle_*` case explicitly.
The cases require Docker, Bash, Python 3, jq, and sqlite3.

```bash
NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://your-model-server:8000/v1 \
NETCLAW_EVAL_MODEL_ID=your-model \
NETCLAW_EVAL_CATEGORY='Tool cycles' \
NETCLAW_EVAL_RUNS=5 \
NETCLAW_EVAL_TIMEOUT=180 \
  ./evals/run-evals.sh
```

| Case | Required evidence |
|------|-------------------|
| `tool_cycle_correction` | The third request receives a runtime correction. The model uses `file_read` to complete an alternative. |
| `tool_cycle_terminal` | The repeated blocked request causes a text-only call. The model reports incomplete work and two completed executions. |
| `tool_cycle_compaction` | Normal compaction completes between executions one and two. The third request receives a correction. The model completes an alternative. |
| `tool_cycle_changed_result` | The same command returns a different result each time. All three executions complete. |
| `tool_cycle_metadata_repair` | Two requests lack required metadata. The corrected request executes once without a cycle correction. |

The relay scripts only the initial tool requests. Each request has a fresh call ID.
The real daemon executes tools and emits the intervention. The target model then
controls the response and any alternative tool use. The fixture never supplies a
successful final answer.

Each trial uses synthetic files in the isolated workspace. A separate counter
checks actual side effects. Strict assertions require paired runtime results,
the expected tool exposure, a real alternative result, and an accurate JSON report.
Every trial must pass. The category fixes the pass threshold at 100 percent.

The compaction case reports synthetic token usage above the normal threshold.
The real daemon must complete compaction and reduce history before the second
execution. The isolated config retains one recent tool result and disables title
requests. Production config and resource limits do not change.

The relay permits at most eight main model requests and eight compaction requests
per trial. The common prompt timeout also applies. The relay accepts a plain API
key through the existing environment variable. It does not accept encrypted keys.

The common archive includes a synthetic relay snapshot and an assertion report.
These cases cover the parent actor. They do not replace child actor tests,
private incident replay, or observe-only acceptance evidence.

Run the assertion tests without a model:

```bash
python3 -m unittest discover -s evals -p 'test_*evals.py' -v
bash -n evals/run-evals.sh
bash -n evals/cycle_evals.sh
```

## Environment Variables

### Eval target (required)

| Variable | Description |
|----------|-------------|
| `NETCLAW_EVAL_PROVIDER_TYPE` | Provider type (`ollama`, `openai`, `openai-compatible`, `openrouter`, `anthropic`) |
| `NETCLAW_EVAL_PROVIDER_ENDPOINT` | Provider URL the container should call |
| `NETCLAW_EVAL_MODEL_ID` | Main model id |
| `NETCLAW_EVAL_PROVIDER_API_KEY` | Optional API key for the eval provider |
| `NETCLAW_EVAL_DATA_PROTECTION_KEYS` | Optional key ring for an encrypted API key |

If any of these is unset and stdin is a terminal, the script prompts for
the missing values. In non-interactive contexts it fails loudly.

### Eval target (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `NETCLAW_EVAL_FALLBACK_MODEL_ID` | `NETCLAW_EVAL_MODEL_ID` | Fallback model id |
| `NETCLAW_EVAL_COMPACTION_MODEL_ID` | `NETCLAW_EVAL_MODEL_ID` | Compaction model id |
| `NETCLAW_EVAL_CONTEXT_WINDOW` | — | Override `Models:Main:ContextWindow`. Cycle cases use 65536 by default. |
| `NETCLAW_EVAL_DISABLE_THINKING` | `false` | Disable provider reasoning for a focused tool-use eval. |

### Container + runtime (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `NETCLAW_IMAGE` | `ghcr.io/netclaw-dev/netclaw:latest` | Image ref |
| `NETCLAW_EVAL_PORT` | `5299` | Host-side port for the eval daemon |
| `NETCLAW_BIN` | `netclaw` | Path to the netclaw CLI on the host |
| `NETCLAW_EVAL_ASSET_ROOT` | Current checkout | Checkout that supplies identity, skills, agents, and config fixtures |

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

# Run ten alternating linked-worktree/recent-file coherence trials
NETCLAW_IMAGE=netclaw-eval:working-context-treatment \
NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible \
NETCLAW_EVAL_PROVIDER_ENDPOINT=https://your-provider.example/v1 \
NETCLAW_EVAL_MODEL_ID=your-model \
NETCLAW_EVAL_CASE=coding_context_worktree_handoff \
NETCLAW_EVAL_RUNS=10 NETCLAW_EVAL_TIMEOUT=180 \
  ./evals/run-evals.sh
```

For a baseline and treatment comparison, use the same harness commit. Point
`NETCLAW_EVAL_ASSET_ROOT` at the checkout that produced each image. Also use
that checkout's CLI. The archived run metadata records the image identity,
harness commit, asset commit, and dirty state.

## Background launch comparison

`run-background-evals.sh` checks actual job state and process lifetime. It uses an isolated daemon and harmless process fixtures.
The loopback fixture drives queue setup through the normal model tool protocol. It sends later model turns to the selected provider.
It does not change the daemon or submit jobs through a test API.

| Case | Required evidence |
|------|-------------------|
| `queued_grant_valid` | Five live blockers, one Pending target, a valid grant, a process marker, and successful completion |
| `queued_grant_revoked` | Verified grant removal, an actual approval denial for the same foreground command, a Pending target before release, then Failed/-1 without a marker |
| `queued_grant_report` | The real model queries the target in its original session and reports its observed state without a shell retry |
| `tool_background_job_lifecycle` | The real model starts one job, queries its output on another turn, and cancels that job; its process must exit |

The older grep-based case now uses the name `tool_background_job_api_selection`. It proves tool selection only.
The new lifecycle case belongs to the dedicated entrypoint because it needs process controls and strict shell grants.
Headless sessions receive no background completion notification. The harness observes persisted state through inotify and reads actual tool results.
The revoked case proves that execution did not occur under revoked authority. It does not identify the background actor's internal failure cause.

For a comparison, merge the eval PR first. Keep that harness checkout unchanged for both runs.
Build the baseline and candidate in separate worktrees. Give each image a distinct tag and retain each CLI binary.
Use the same model, provider configuration, assets, prompts, run count, and timeout for both runs.
Do not rebuild a mutable image tag between runs. Record both source revisions with the results.

```bash
export NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible
export NETCLAW_EVAL_PROVIDER_ENDPOINT="$EVAL_API_BASE"  # API base must end in /v1.
export NETCLAW_EVAL_MODEL_ID="$EVAL_MODEL"
export NETCLAW_EVAL_RUNS=5
export NETCLAW_EVAL_TIMEOUT=180
export NETCLAW_EVAL_ASSET_ROOT="$EVAL_HARNESS_CHECKOUT"

NETCLAW_EVAL_NO_BUILD=1 NETCLAW_IMAGE="$BASELINE_IMAGE" NETCLAW_BIN="$BASELINE_CLI" \
  "$EVAL_HARNESS_CHECKOUT/evals/run-background-evals.sh"
NETCLAW_EVAL_NO_BUILD=1 NETCLAW_IMAGE="$CANDIDATE_IMAGE" NETCLAW_BIN="$CANDIDATE_CLI" \
  "$EVAL_HARNESS_CHECKOUT/evals/run-background-evals.sh"
```

The relay supports the OpenAI Chat Completions protocol. Set `NETCLAW_EVAL_PROVIDER_API_KEY` if the upstream requires a plain API key.
The relay does not support encrypted keys, OAuth, or other provider protocols. Keep secrets and private endpoints out of commits and public reports.
Linux, Docker host networking, Python 3, and Linux pidfds are required.

For local harness checks without an external model, use `./evals/run-background-evals.sh --runtime-only`.
This mode executes only the two queue cases. It does not provide model behavior evidence.
Set `NETCLAW_EVAL_CASE=tool_background_job_lifecycle` or `NETCLAW_EVAL_CASE=queued_grant_revoked` to execute the two evals separately.
The queued eval always includes the valid-grant control. A harness error stops the run because its state can invalidate later cases.
An unsafe baseline returns a failure when the revoked target starts. Do not adjust the oracle to make that baseline pass.

Each run archives `stdout/stdout_background-results.txt` below `evals/runs/<run-id>/`.
The JSON separates runtime verdicts, model verdicts, and harness errors. It includes queue records, grant snapshots, process evidence, and the CLI hash.
An upstream endpoint hash permits comparison without disclosure of the private URL.
The existing archive also records the image ID, harness revision, asset revision, and dirty checkout flags.
Any runtime failure, model failure, or harness error fails the run. The ordinary suite percentage threshold does not apply.
Local fixture tests run with `python3 -m unittest discover -s evals -p test_background_evals.py -v` and also run in CI.

## Results Database

Results are accumulated in `$EVAL_HOME/evals/results.db` during execution.
On exit, the harness archives the database, run metadata, daemon log, and
per-turn stdout under `evals/runs/<run-id>/` before deleting the throwaway
home. These archives are gitignored and can be compared locally without
touching the operator's `~/.netclaw/` state.

Raw archives can contain prompts, session identities, tool calls, and provider
configuration. Do not publish them. Publish only reviewed PII-free aggregates.

Windows model-pattern cases remain deferred until sanitized representative
traffic exists. Windows contract tests remain required for path and process
behavior. Record the deferred cases as evidence work, not as passed evals.

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

# Netclaw Eval Suite

Behavioral eval suite that tests identity, skill loading, memory, tool use,
grounding, and autonomy against an ephemeral `netclawd` Docker container.
Completely isolated from the operator's real `~/.netclaw` state.

## Quick Start

```bash
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
3. The harness copies repository identity templates into `$EVAL_HOME/identity/`.
   `NETCLAW_EVAL_ASSET_ROOT` selects the checkout that supplies these templates and the skill assets.
   The harness does not copy the operator's identity files.
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

### Fresh Containers For Independent Trials

The harness creates one container per invocation. Cases and repeat trials within that invocation share its daemon and data home.
Use one invocation per independent acceptance trial. Set one exact case and `NETCLAW_EVAL_RUNS=1` for each invocation.
Multiple conversation turns within that trial use the same container and session.

For five independent trials, run this command five times:

```bash
NETCLAW_EVAL_CASE=tool_cycle_correction \
NETCLAW_EVAL_RUNS=1 NETCLAW_EVAL_THRESHOLD=1 \
NETCLAW_EVAL_TIMEOUT=180 \
  ./evals/run-evals.sh
```

Set the provider variables before each command. Retain every result, including failures and trials that exceed the deadline.
Record distinct container IDs, data homes, run IDs, and session IDs. Retain each archive before the next invocation.
Aggregate the five strict verdicts after all five invocations. Five repeat trials inside one invocation do not establish this isolation.
An immutable image and CLI binary can be reused with `NETCLAW_EVAL_NO_BUILD=1`, `NETCLAW_IMAGE`, and `NETCLAW_BIN`.

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
| Complex Task Execution | 6 | Multi-step tool chains complete successfully, incl. bounded tool output — given only the goal (no handling hints), the agent retrieves a deep line from oversized shell output and from a large file, which is only possible by coping with the bound the way AGENTS.md/skills/steer text direct; and in a session with no shell call, the agent reads the middle of an oversized skill through `tool_output_read` |
| Multi-Turn Conversation | 7 | Session resume and speaker attribution recall |
| Built-in Tools Before CLI | 10 | The agent uses a built-in tool, not a `netclaw` shell command, when a tool exists: three regression cases, five guards, and two controls that need the CLI |

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

### Built-in Tools Before CLI Cases

These cases cover one production failure. The owner asked about a recurring job.
The agent searched for "list reminders", and the search did not return
`list_reminders`. The agent then read the scheduling reference and ran
`netclaw reminder list` through `shell_execute`. That call needs a shell
approval; the built-in tool needs none.

Each assertion reads tool calls from the `--json` envelope and does not read
the response text. Some prompts name a step on purpose: two cases tell the agent to
read the scheduling reference, and one tells it to search. No prompt names the
correct tool.

| Case | Kind | Required evidence |
|------|------|-------------------|
| `cli_preference_reminder_schedule` | Regression | Turn 1 reads the scheduling reference. Turn 2 is the owner's exact question: `list_reminders` runs and shows the seeded reminder. No turn runs a `netclaw` shell command. |
| `cli_preference_reminder_after_reference` | Regression | One turn. The agent reads the scheduling reference, `list_reminders` runs, and no `netclaw` shell command runs. |
| `cli_preference_reminder_search` | Regression | A `search_tools` result for "list reminders" lists `list_reminders`. The tool runs, and no `netclaw` shell command runs. |
| `cli_guard_reminder_question` | Guard | The owner's exact question in a new session: `list_reminders` runs and shows the seeded reminder; no `netclaw` shell command. |
| `cli_guard_reminder_cancel` | Guard | `cancel_reminder` runs with the target ID; no `netclaw` shell command. |
| `cli_guard_reminder_history` | Guard | `get_reminder_history` runs; no `netclaw` shell command. |
| `cli_guard_webhook_list` | Guard | `list_webhooks` runs; no `netclaw` shell command. |
| `cli_guard_approvals_read` | Guard | A tool call reads `tool-approvals.json`; no `netclaw` shell command. |
| `cli_control_reminder_delete` | Control | No built-in tool deletes a reminder. `netclaw reminder delete <id>` runs through the shell. |
| `cli_control_daemon_status` | Control | No built-in tool reports daemon health. `netclaw status` or `netclaw doctor` runs through the shell. |

A regression case fails on the code before the fix. A guard case passes before
the fix and keeps the correct behavior. A control case needs the CLI, so
guidance that only tells the agent to avoid the `netclaw` CLI fails it.

A case setup writes each reminder definition file after the daemon starts, so
other categories have no reminders. The reminder tools read the files for each
call. The daemon gives a schedule entry only to a file that exists at startup,
so a seeded reminder cannot fire during a run.

The `eval_workspace` MCP fixture adds 18 tools for every category. Their names
share common words with the built-in tools, as a production catalog does. The
search case needs them: without those tools, an unranked search still returns
`list_reminders`.

A shell command counts as a `netclaw` CLI call when `netclaw` is in the command
position, with or without a path or an assignment prefix. A path such as
`~/.netclaw/logs` does not count. A call behind `timeout 30` or `bash -c` is a
known limit of the detector. `test_cli_detector_evals.py` holds the examples.

Use a timeout of 240 seconds for this category. A case can need five model calls.

```bash
NETCLAW_EVAL_CATEGORY='Built-in Tools' NETCLAW_EVAL_TIMEOUT=240 ./evals/run-evals.sh
```

### Tool Cycle Cases

The cycle cases use the existing harness and provider relay. They require an
OpenAI-compatible endpoint with a `/v1` API base. The default suite excludes them.
Select one `tool_cycle_*` case explicitly for an independent trial.
The cases require Docker, Bash, Python 3, jq, and sqlite3.

```bash
NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://your-model-server:8000/v1 \
NETCLAW_EVAL_MODEL_ID=your-model \
NETCLAW_EVAL_CASE=tool_cycle_correction \
NETCLAW_EVAL_RUNS=1 NETCLAW_EVAL_THRESHOLD=1 \
NETCLAW_EVAL_TIMEOUT=180 \
  ./evals/run-evals.sh
```

| Case | Required evidence |
|------|-------------------|
| `tool_cycle_correction` | The third request receives a runtime correction. The model uses `file_read` to complete an alternative. |
| `tool_cycle_terminal` | The runtime stops the repeated blocked request. It reports partial work without another model request. Exactly two executions complete. |
| `tool_cycle_compaction` | Normal compaction completes between executions one and two. The third request receives a correction. The model completes an alternative. |
| `tool_cycle_changed_result` | The same command returns a different result each time. All three executions complete. |
| `tool_cycle_metadata_repair` | Two requests lack required metadata. The corrected request executes once without a cycle correction. |
| `tool_cycle_nonadjacent_correction` | Distinct diagnostics separate two equal primary results. Candidate three receives correction. The real model completes recovery through `file_read`. |
| `tool_cycle_nonadjacent_terminal` | A further diagnostic follows correction. The next primary candidate receives runtime Stop. No further provider request or effect occurs. |

The relay scripts only the initial tool requests. Each request has a fresh call ID.
The real daemon executes tools and emits the intervention. The target model controls
the alternative tool use in cases that permit recovery. The terminal cases require
a runtime report and zero model requests after the stop candidate. The fixture never
supplies a successful final answer.

Each trial uses synthetic files in the isolated workspace. A separate counter
checks actual side effects. Strict assertions require paired runtime results,
the expected tool exposure, a real alternative result, and an accurate JSON report
for recovery cases. The terminal cases require the runtime partial report.
Every trial must pass. The category fixes the pass threshold at 100 percent.

Recovery permits only `file_read` calls.
`completed_attempts` counts actual primary appends. Denied requests and diagnostics do not count.
`last_result` names the last executed primary output, without guard feedback or diagnostic output.
The entire visible reply must contain one final JSON object, without Markdown or text beside tool calls.
Earlier trial results retain their original prompt revision. These clarifications do not alter those verdicts.

The nonadjacent cases request `A, B, A, C, A` after the normal shell-tool load.
`A` appends one primary attempt and returns the same result.
Distinct diagnostics `B` and `C` append their own labels to a separate counter and return distinct results.
Candidate three of `A` must receive a paired runtime correction without an effect.
The terminal case then requests `D, A`. Diagnostic `D` executes before the runtime refuses the final `A` candidate.
These sequences avoid adjacent cycles of periods one through three.
Exact CLI call arguments, ordered diagnostic receipts, and both counters establish the interleaved sequence.
Both terminal oracles reject any later provider request, including a sidecar request.
This oracle check does not establish a runtime or model pass.

Acceptance requires five independent trials per new case and a 100 percent pass rate.
Select `NETCLAW_EVAL_CASE=tool_cycle_nonadjacent_correction` or `NETCLAW_EVAL_CASE=tool_cycle_nonadjacent_terminal` with `NETCLAW_EVAL_RUNS=1`.
Invoke the harness five times per case. Follow the fresh-container procedure above.
Trial-count overrides remain available for diagnosis. Fewer trials do not complete acceptance.

The compaction case reports synthetic token usage above the normal threshold.
The real daemon must complete compaction and reduce history before the second
execution. The isolated config retains one recent tool result and disables title
requests. Production config and resource limits do not change.

The relay permits at most 16 main requests per trial.
At most eight of those requests can reach the target model.
Compaction and memory-distillation requests share a separate limit of eight sidecar requests.
Both require evidence that identifies the current synthetic trial.
The common prompt timeout also applies. The relay accepts a plain API
key through the existing environment variable. It does not accept encrypted keys.

The common archive includes a synthetic relay snapshot and an assertion report.
These cases cover the parent actor. They do not replace child actor tests,
private incident replay, or observe-only acceptance evidence.

#### Raw-output review of the first fixed run

The five-trial Qwen run on September 12, 2026, has 8 strict passes from 25 trials.
These scores use the original prompt and oracle. Later changes do not replace them.

| Case | Original strict passes |
|------|------------------------|
| Correction | 2/5 |
| Terminal stop | 5/5 |
| Compaction | 0/5 |
| Changed result | 1/5 |
| Metadata repair | 0/5 |

Independent review of the raw receipts confirms all 25 initial scripted runtime sequences.
That result does not prove post-handoff safety. Eight trials cause additional mutations.
Five other failures recover the correct value safely but violate tool, status, or format requirements.
Two compaction trials fail to recover and report incorrect data.
One metadata trial has an ambiguous blocked-operation flag. One metadata trial exceeds the 180-second deadline without a final response.

The original status instruction does not clearly separate recovery-value retrieval from primary-operation success.
The revised prompt defines this distinction and explicitly requires `file_read`.
The strict oracle still rejects shell alternatives and extra mutations.
The report separates the initial runtime contract, post-handoff safety, and model task checks.
The terminal case has no target-model task score. Its `model_task` group reports `not_applicable`.
The runtime group checks its partial report and the absence of a final model request.
An incomplete trace receives an explicit inconclusive result, never a pass.
The safety group requires both the expected final counter and a `file_read`-only post-handoff trace.
A final counter alone cannot exclude a later write that resets it.

The review also finds absent compaction flags in the transport and a relay that rejects memory-distillation sidecars.
Neither finding establishes a detector-state defect. The retained summary's semantic quality remains unverified.
Different completed actions clear the prior block, and changed results prevent exact recurrence.
These evals do not justify removal of resource limits.

#### Revised prompt and transport run

The next fixed run uses two trials per case. It records 5/10 strict passes and 10/10 initial runtime-contract passes.
The post-handoff safety group passes 6/10 trials. A separate compaction smoke trial passes all checks.

| Case | Initial runtime contract | Strict task result |
|------|--------------------------|--------------------|
| Correction | 2/2 | 2/2 |
| Terminal stop | 2/2 | 2/2 |
| Compaction | 2/2 | 1/2 |
| Changed result | 2/2 | 0/2 |
| Metadata repair | 2/2 | 0/2 |

Independent raw review confirms four trials with extra mutations after successful initial controls.
One compaction trial reaches the cycle stop, then receives a decoded tool call in the text-only response.
The runtime rejects that call. The turn uses 4 of 60 tool iterations, so the former budget-exhaustion message was inaccurate.
The diagnostic now describes the text-only violation without a false budget claim.
Raw upstream responses are absent; provider versus adapter responsibility remains unverified.

The positive controls still lack an explicit setup length in this run's prompt.
The next diagnostic prompt defines three initial shell requests and starts recovery after their third result.
It also separates permitted setup effects from the later no-write rule.
No new user message occurs at handoff, and every strict safety check remains active.
This follow-up does not replace either earlier fixed run.

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
- **Daemon does not fail fast on empty config**: a follow-up task will
  make `netclawd` refuse to start when identity or provider config is
  missing. Today, missing config produces a running-but-broken daemon
  whose LLM calls fail at request time. Not relevant to the eval path
  itself (the script always supplies valid config) but noted for anyone
  exploring the Docker image directly.

## Background child acceptance and later results

The child cases use the existing background runner and a persistent `DaemonClient` consumer.
Read [the child consumer procedure](fixtures/child-runs/README.md) before a trial.
The product headless CLI exits at its first turn boundary.
A child result can arrive later, so the affected Subagents cases retain a connection through automatic continuation.
The adapter labels its evidence source and retains the actual output DTOs.
It does not fabricate a second result for the original start call.

Select `child_run_held_parent`, `child_run_partial_cancel`, or `child_run_cli_acceptance` with `NETCLAW_EVAL_CASE`.
Run each trial through `run-background-evals.sh` with `NETCLAW_EVAL_RUNS=1`.
Set `NETCLAW_CHILD_OBSERVER` to the canonical observer DLL from the final combined source.
Use five fresh invocations for each critical held-child and cancel case.
The runner archives actual provider bytes, child paths, commit diagnostics, and artifact evidence.
The CLI acceptance smoke remains separate from the persistent-result cases.

The existing Subagents consumers also require an exact case selector and one fresh invocation.
Their original project, scope, shell, and artifact assertions remain mandatory.
The queue-grant and shell lifecycle controls retain their original paths and oracles.
This source preparation supplies no current model pass or complete background health claim.

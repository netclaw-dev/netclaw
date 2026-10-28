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
3. The harness renders repository identity templates and copies the deployment fixture into `$EVAL_HOME/identity/`.
   It does not require an initialized operator home.
4. Daemon logs land in `$EVAL_HOME/logs/daemon-YYYY-MM-DD.log` via a
   writable bind-mount of `/home/netclaw/.netclaw/logs`. Assertion helpers tail
   this file with per-prompt offsets, exactly like the pre-container
   version did.
5. The CLI is pointed at the eval daemon via
   `NETCLAW_DAEMON_ENDPOINT=http://127.0.0.1:$EVAL_PORT` and its own path
   resolution is sandboxed via `NETCLAW_HOME=$EVAL_HOME`. The host's
   `~/.netclaw/` is never touched by the CLI during the run.
6. On exit, the harness stops its container and verifies stop or removal.
   It then archives the logs and captures before it deletes `$EVAL_HOME`.
   A throwaway root-in-container cleanup step handles files that the daemon wrote as UID 1654.

### Prompt failure containment

Any nonzero prompt result aborts the suite with exit code `2`.
This rule covers ordinary text, JSON, resumed, coordination observer, and memory warm-up prompts.
The harness preserves the original CLI exit status and separate stdout and stderr captures.
It then stops and verifies its container before another prompt, setup step, assertion, score calculation, or state reset.
A failed first turn prohibits the second turn.

The harness captures the immutable 64-hex container ID from a successful `docker run` response.
One helper uses that ID for both prompt failure and EXIT cleanup.
The helper waits for `docker stop` to complete, then requires a successful Docker response that proves stop or removal.
An inspect error alone does not prove removal.
The harness never stops a container through its mutable name.

After confirmed containment, the harness collects the failed trial's session evidence through the existing collector.
A warm-up prompt has no trial, so it skips this step.
The collector reports a missing session through its existing result; it does not invent session evidence.
The harness then archives the finalizer logs and captures before it deletes the scratch home.
If stop or verification fails, the harness reports containment as unconfirmed and exits `2`.
It retains the scratch home and captures without another stop attempt or trial.
It also terminates its fixture subprocesses when containment remains unconfirmed.
Do not reuse or restart a failed eval home; shutdown can preserve work for recovery.

The verdict helpers can classify preserved evidence offline after an abort.
A timeout or failed-turn classification remains separate from permission to dispatch another trial.
Successful calls retain the current session and multi-turn behavior.

The harness preloads `evals/fixtures/config/netclaw.json` into the ephemeral
home before startup. It auto-approves tools and grants read/write access for the
Personal audience because headless sessions cannot answer approval prompts or
edit an interactive trust policy. A companion `tool-approvals.json` trusts Git
for shell-based coding cases. Tool exposure and command-deny rules still apply,
and these policies are never copied into an operator's config.

### Behavioral eval preflight

Run the deterministic assertion tests before model calls.

```bash
python3 -m unittest discover -s evals -p 'test_*.py' -v
```

For a comparison, freeze image digests, source hashes, prompts, resources, sample sizes, thresholds, and provider settings first.
Verify actual forwarded parameters and tool hashes for each endpoint pair.
The variant seed does not seed provider inference.
Retain complete response evidence and require one consistent SSE choice with its own finish marker.
Keep safety, output format, and explicit attribution as separate measures when their contracts differ.
Keep prior failures and inconclusive trials in their original archives.
An instrument repair requires new acceptance evidence when the old trace cannot prove the repaired invariant.

The coordination observer requires a fifth argument: a private log path under `$EVAL_HOME/logs`.
It writes the canonical session header and completion records from actual session events.
The existing collector and verdict reader consume that log.
The observer emits JSON before timeout exit `124`; its wrapper allows 15 seconds for output and disposal.
A valid JSON answer alone cannot replace missing session evidence.

### Trial independence

All trials of one invocation share one daemon, one memory database, and one
home folder. A trial must not change what a later trial sees. The harness
keeps these rules:

- **The harness resets state before each trial.** `reset_trial_state` runs
  before the setup function of the case and before the memory snapshot. It
  does two things:
  - `reset_memory_store` restores the memory store to the seeded state. It
    runs `seed-memories.py --reset` in the container. The script deletes every
    row of every `memory_*` table and seeds the `doc-eval-*` memories again,
    in one transaction. A memory from an earlier trial is gone (a
    `store_memory` call or a distilled memory). A seed memory that a trial
    changed is back at its fixture content. The session catalog is not
    touched. A failed reset stops the run with exit 2.
    A curation that is in progress writes its documents even after the reset
    deleted its checkpoint row. So the script first waits, for up to 30 s,
    until no checkpoint is `pending` or `processing`. The wait, the deletes,
    and the seed run in one write transaction. If the wait ends, the reset does
    not run and the trial is inconclusive with the reason
    `infra:memory_snapshot_reset_curation_busy`.
  - `restore_agent_fixtures` writes `evals/fixtures/agents/` into the daemon
    home again, in the container, and removes every other file from the
    agents folder. A parent agent can rewrite or add an agent file during a
    trial. The daemon user (uid 1654) can own such a file, and the host user
    cannot write over it, so the restore does not run on the host. It runs as
    the daemon user and not as root, because the agent can remove the folder
    or replace it with a symbolic link, and root would follow the link. The
    restore removes the folder (a link is removed, not its target), creates it
    again, extracts the fixtures, and creates `agents/.server-feeds`, as the
    daemon does at start. A failed restore stops the run with exit 2.
  - `seed_eval_memories` runs once at the start of the run. A failed seed
    stops the run with exit 2, because the reset cannot run without a seed.
- **Distillation is off in the fixture config.** `Session.IdleTimeout` is
  `00:00:00`, `Session.MemoryObserverIdleSeconds` is 86400, and
  `Session.Tuning.MemoryDistillationTurnInterval` is 0. `IdleTimeout` zero
  disables the passivation timer. It does not mean "passivate at once". An idle
  passivation distills the session, so a session never passivates in an eval
  run. A case that tests distillation must run in its own invocation with the
  default settings. `NETCLAW_EVAL_CONFIG_FILE` replaces the whole fixture
  config, so a config that you supply does not get these keys (the background
  evals do not get them either). The reset still removes a memory that an
  earlier trial wrote. It cannot stop a distillation that lands during a
  trial, so such a run can see a memory that the trial did not write.
- **The harness checks that the reset worked.** Before each trial, the harness
  writes `raw/memory-before.json`: the status of the snapshot and the ids of
  every memory that is not a `doc-eval-*` seed. After a reset, the list is
  empty. If recall injects an id from the list, `trial_verdict.py` gives the
  reason `learned_memory_recalled`, even when the assertion passed. A memory
  that the trial itself wrote is not on the list, so it does not count. If
  `netclaw.db` is missing or locked, or if the reset did not run, the snapshot
  has a status other than `ok`, and the trial is inconclusive with the reason
  `infra:memory_snapshot_<status>`.
  The check sees only a write that lands between the reset and the snapshot.
  A write after the snapshot is not on the list. The idle wait of the reset
  prevents the one known writer of that kind, a curation in progress.
- **The memory check covers automatic recall.** A reset store also covers a
  `find_memories` or `get_memories` call of the trial, because the store holds
  no memory from an earlier trial.
- **A case that keeps other state must reset it.** A case that changes
  reminders, identity files, or workspaces resets them in its `setup_<case>`
  function.

`--network host` is the default because operators often host their LLM on
a Tailscale node — MagicDNS hostnames like `my-gpu-server.tailnet.ts.net` only
resolve when the container shares the host's DNS resolver. macOS/Windows
operators need a different endpoint resolution strategy (Docker Desktop
reduces `--network host` to bridge mode).

## What It Tests

The suite runs prompts via `netclaw chat -p` against the eval container and
verifies both **stdout output** (tool calls, text content) and **daemon
log patterns** (skill loading, memory recall, checkpoint formation).

Coordination checks separate task accuracy from optional spill-hint uptake and
child-resume proof. The synthesis observer proves active child work at the
first parent response, later child completions, and automatic continuation.
It does not link child tasks to child results or record child tool calls, so it
cannot prove each child's inspection scope. The artifact case checks a child
spawn and completion plus the exact parent outcome, but it cannot prove that
the child inspected the named artifacts.

| Category | Cases | What It Validates |
|----------|-------|-------------------|
| Identity & Self-Awareness | 5 | Bot knows its name, version, repo, session ID, and routes all identity-file concerns without a skill dependency |
| Skill Discovery and Activation | 20 | Models load relevant file, feed, and MCP prompt skills while they skip unrelated skills |
| Memory Pipeline | 4 | Memory recall is active, identity-vs-memory routing is correct, explicit saves use memory tools, and automatic checkpointing still fires |
| Tool Discovery & Use | 19 | Progressive discovery, structured workspace selection, web search, timestamped webhook configuration, and long-command strategy |
| Grounding & Alignment | 4 | Uses tools to verify facts, admits uncertainty, and resolves announced attachment paths from the authoritative session root |
| Autonomy & Execution | 2 | Executes tasks rather than describing them |
| Deployment Mission | 1 | Applies the disk mission playbook, loads its required skill, and returns reviewed sales email |
| Subagents | 7 | Covers four existing child tasks and three coordination cases. Checks retained-result and artifact outcomes, child completion, and automatic parent continuation. |
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

The weekly disk cleanup reminder fires every Sunday at 03:00 UTC. The setup
also writes one successful history record at the latest Sunday 03:00 UTC. On
a Sunday after 03:00 UTC, an empty history made the agent think that the fire
was missed, and it ran `netclaw reminder status` to check. The record keeps
the schedule on time for every run day.

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

### Long Command Case

`long_command_background_job` is the regression case for a live failure on
18 October 2026. A slow command timed out at the 90 s foreground limit two
times. The runtime refused the third identical run, and the model sent the same
call again.

The fixture `/home/netclaw/.netclaw/evals/slow_render` runs for 150 s and then
prints `render-complete`. The prompt does not give the duration. The case
proves that the agent sends the same foreground command at most two times and
uses a background job, a timeout of 180 s or more, or the
`long-running-commands.md` page of `netclaw-operations`. The timeout must be
greater than the 150 s sleep, because the shell start and the print also take
time.

`long_command_evals.py` reads the tool calls in the `--json` envelope. A changed
timeout below 180 s is still the same command. The prompt timeout of this case
is at least 480 s.

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
| `tool_cycle_repeat_nudge` | The relay repeats the refused request with a fresh call ID. Netclaw refuses it again with the same correction and adds one nudge as the last message. The turn continues with tools. The real model reports incomplete work with two completed executions. It can also follow the nudge and run the primary command as a background job: then three executions are correct, and the report must give three. A third execution in the foreground fails. The envelope has no `errorCategory` or `errorKind`, and no log shows a turn failure. |
| `tool_cycle_compaction` | Normal compaction completes between executions one and two. The third request receives a correction. The model completes an alternative. |
| `tool_cycle_changed_result` | The same command returns a different result each time. All three executions complete. |
| `tool_cycle_metadata_repair` | Two requests lack required metadata. The corrected request executes once without a cycle correction. |

The relay scripts the tool requests. Each request has a fresh call ID.
The daemon executes tools and emits the intervention. The model controls recovery
after a correction. After a repeated refusal, Netclaw refuses the call again, adds
a nudge, and continues the turn. The nudge is a user-role `[system: ...]` message.
The relay matches its stable text: the refused tool name and the
`skill_read_resource('netclaw-operations', 'references/long-running-commands.md')` call.
The first refusal must not add a nudge.

Each trial uses synthetic files in the isolated workspace. A separate counter
checks actual side effects. Strict assertions require paired runtime results,
the expected tool exposure, a real alternative result, and an accurate JSON report.
Every trial must pass. The category fixes the pass threshold at 100 percent.

The compaction case reports synthetic token usage above the normal threshold.
The real daemon must complete compaction and reduce history before the second
execution. The isolated config retains one recent tool result and disables title
requests. Production config and resource limits do not change.

The relay permits at most eight main model requests and eight sidecar requests
per trial. Compaction and memory-distillation requests share the sidecar limit.
Both require evidence that identifies the current synthetic trial.
The common prompt timeout also applies. The relay accepts a plain API
key through the existing environment variable. It does not accept encrypted keys.

The common archive includes a synthetic relay snapshot and an assertion report.
These cases cover the parent actor. They do not replace child actor tests,
private incident replay, or observe-only acceptance evidence.

## Productive Child Checkpoint

With an eval target configured, run the case with:

```bash
NETCLAW_EVAL_CASE=coordination_productive_long_child \
NETCLAW_EVAL_RUNS=5 ./evals/run-evals.sh
```

The fixture scripts productive child checkpoints at rounds 48 and 49.
It measures the first actual provider response with tools available.
The primary measure rejects cancellation and false public system budgets.
The report retains the original format and attribution result under `strict_contract`.
It also reports `json_format` and `explicit_attribution` separately.
The evidence covers one response; it does not establish later supervision or general reliability.
See the [comparison contract](2656-prospective-contract.md) for source controls, denominators, and the acceptance rule.

## Coordination Spill Hint Pair

This opt-in pair runs the same retained-output task with the optional child-inspection hint present and absent.
The relay removes only that exact hint from provider-bound tool results in the absent arm.
It preserves the result text, spill call ID, continuation instruction, tool schemas, prompt, and authority.
The pair runs each arm three times and keeps the configured pass threshold.
Task accuracy gates each arm. The archive records child calls and completions as separate uptake data.
The pair does not force child use or prove a causal rate because separate model trials can differ.

```bash
NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible \
NETCLAW_EVAL_PROVIDER_ENDPOINT=http://your-model-server:8000/v1 \
NETCLAW_EVAL_MODEL_ID=your-model \
NETCLAW_EVAL_CATEGORY='Coordination Spill Hint' \
NETCLAW_EVAL_RUNS=3 \
NETCLAW_EVAL_TIMEOUT=300 \
  ./evals/run-evals.sh
```

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
python3 -m unittest discover -s evals -p 'test_*.py' -v
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
| `NETCLAW_EVAL_THRESHOLD` | `0.80` | Pass threshold, a number above 0 and at most 1. The run stops at startup with exit 2 for any other value, such as `0`. The harness compares it as an exact decimal, so use `0.66` for 2 of 3, not `0.67`. |
| `NETCLAW_EVAL_TIMEOUT` | `60` | Per-prompt timeout in whole seconds. The run stops at startup with exit 2 for any other value, such as `5m`. The Subagents category uses at least 120 seconds, or this value when it is larger. |
| `NETCLAW_EVAL_SEED` | random | Integer seed for the choice of prompt variants. The harness prints the seed and records it in each trial's `variant.json`. |

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
After confirmed container containment, the harness archives the database, run metadata, daemon log, and per-turn stdout under `evals/runs/<run-id>/`.
It then deletes the scratch home.
If containment remains unconfirmed, it retains the home and captures without this final archive step.
These archives are gitignored and permit local comparisons without access to the operator's `~/.netclaw/` state.

Raw archives can contain prompts, session identities, tool calls, and provider
configuration. Do not publish them. Publish only reviewed PII-free aggregates.

### Trial evidence

Each trial writes its evidence to `evals/runs/<run-id>/trials/<case>/<nn>/raw/`
while the run goes on. `<nn>` is the trial number, `01` and up.

| File | Content |
|------|---------|
| `variant.json` | `variant_index` (from 0), `variant_count`, `seed` (the run seed), `trial_seed` (the seed of this case and trial). A multi-turn trial has no `variant.json`. |
| `prompt.txt` | The prompt that the CLI received, with placeholders filled |
| `cli-exit.json` | `exit_code` (124 means `timeout` cut the call off), `started_at`, `ended_at`, `deadline_s` |
| `cli-stdout.txt` or `cli-stdout.json` | CLI stdout. The extension follows the output mode. |
| `cli-stderr.txt` | CLI stderr |
| `session.json` | Result of `trial_evidence.py discover-session`: `ok`, `no_session`, or `ambiguous_session` |
| `cli-session.log` | The CLI session log that the trial created |
| `session.log` | The actor log that `netclaw.db` names for the session |
| `subagents/<run id>/*.log` | Logs of the sub-agents of the session |
| `actor-log.json` | How the harness found the actor log. A status other than `ok` means that the log is missing or was refused, and the harness prints a warning. The actor log must resolve under `data/sessions`. A sub-agent log must resolve under the folder of its session. `refused_subagent_logs` lists the sub-agent logs that were refused. |
| `memory-before.json` | `status` of the memory snapshot (`ok`, `no_memory_db`, `memory_db_unreadable`, or `reset_curation_busy`), and `ids`: the memories that exist before the trial and that the harness did not seed. `ids` is empty after a clean reset, and `null` when the status is not `ok`. |
| `daemon-session.log` | Daemon lines with the exact token `session=<id>` or `sessionId=<id>` |
| `daemon-window.log` | Every daemon line from the start of the trial to the end of the assertion, for people to read. A trial that crosses midnight UTC adds the lines of the new daemon file. A run stops with exit 2 and an error message when `$EVAL_HOME/logs` holds no `daemon-*.log` file. |
| `assert.json` | `exit_code` of the assertion (141 is a pipe failure, 1 is a missed match), `daemon_log`, `window_lines_before`, `window_lines_after`, `extra_daemon_logs` |
| `assert-report.json` | Only for an assertion that writes a JSON report (the tool cycle cases). The trial is inconclusive when the report says `"status": "inconclusive"`. |
| `assert-stderr.txt` | stderr of the assertion function. It is often empty, because the assertion helpers send their own stderr to `/dev/null`. |

After the assertion, `trial_verdict.py trial-status` writes `verdict.json` in the
trial folder (next to `raw/`). It holds `status`, `reason`, and `error`.
An aborted prompt stops before the assertion and verdict steps.
After the harness stops and verifies the container, it collects the failed trial's session evidence before scratch deletion.
The raw CLI result and available session evidence remain available for offline verdict and score analysis.

A multi-turn trial keeps `prompt.txt`, `cli-exit.json`, `cli-stdout.*`, and
`cli-stderr.txt` of each turn in `raw/turn-<nn>/`. It keeps the session files
in `raw/`. The CLI log of a multi-turn trial is the log that is new since
before the first turn.

Prompt variants are repeatable. Each (case, trial) takes a seed that is a
stable hash of the run seed, the case name, and the trial number. The variant index is that seed modulo the number of variants, so it does not depend on the bash version. To repeat a
run, set `NETCLAW_EVAL_SEED` to the seed that the earlier run printed. A run
that `NETCLAW_EVAL_CASE` filters picks the same variants for that case as the
full run.

Windows model-pattern cases remain deferred until sanitized representative
traffic exists. Windows contract tests remain required for path and process
behavior. Record the deferred cases as evidence work, not as passed evals.

`eval_results` holds one row for each trial:

| Column | Content |
|--------|---------|
| `status` | `pass`, `fail`, or `inconclusive`. |
| `reason` | NULL for a pass and a fail. The reason token for an inconclusive trial. |
| `passed` | 1 for a pass, 0 for a fail, NULL for an inconclusive trial. A query such as `AVG(passed)` does not count an inconclusive trial as a fail. |
| `details` | `pass`, `fail`, or the assertion details. For a failed turn, the text of the CLI `ERROR` record. |

`eval_runs.inconclusive_cases` counts the cases that are inconclusive.
`passed_cases` counts the cases that pass. A CHECK constraint ties `status` to
`passed`: `pass` needs `passed` to be 1, `fail` needs 0, and `inconclusive` needs
NULL. Each run makes a new `results.db` in a new home, so the harness has no
migration. `results_db.py init` does not change a `results.db` from an older
harness. `results_db.py` binds every value as an SQL parameter, so quotes and
newlines in a prompt or an error text are safe.

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

A trial has one of three states:

- **pass** or **fail**: the turn finished and the assertion exited 0 or 1.
- **inconclusive**: the trial has no verdict. `reason` tells why. The checks run
  in this order, and the first match wins:
  - `infra:<detail>`: `no_session`, `ambiguous_session`, `crash_log` (a new
    `crash-*.log`), `cli_exit_<code>` (a CLI exit other than 0, 1, and 124), or
    evidence that cannot be read.
  - `timeout`: the CLI exit is 124, or a turn with exit 0 has no
    `TURN_COMPLETED` record.
  - `infra:cli_failed`: the CLI exit is 1 and the turn has no `TURN_COMPLETED`
    record. The channel threw before the turn ended. The deadline did not cut it.
  - `turn_failed`: the CLI exit is 1 and the turn has a `TURN_COMPLETED` record.
    `details` keeps the `ERROR` text.
  - `learned_memory_recalled`: recall injected a memory that existed before the
    trial and that the harness did not seed (see Trial independence). A trial
    folder with no `memory-before.json` skips this check.
  - `infra:memory_snapshot_<status>`: the memory snapshot of the trial failed
    (`no_memory_db` or `memory_db_unreadable`), or the reset did not run
    (`reset_curation_busy`), so the recall cannot be checked.
  - the reason of an assertion report that says `inconclusive` (cycle cases).
  - `assertion_exit_<code>`: the assertion exited with a code other than 0 and 1
    (141 for a pipe failure, 127 for a missing command).

**Known limit:** the verdict helper classifies a failed turn as inconclusive.
A model behavior failure that fails the turn cannot give a FAIL.
The CLI reports `category=model` for this failure, but the harness does not read the category yet.
For example, a failed turn after repeated refusal can receive `turn_failed` during offline analysis.
The nonzero CLI result aborts the suite with exit `2` before the case receives a score.

A `CANCELLED` record in `headless-errors.log` is the normal sign of a cut turn.
It is not an infrastructure fault, so the verdict does not read that file.

A case uses the same states. Count each inconclusive trial once as a pass and
once as a fail:

- **PASS**: the case passes even when each inconclusive trial fails.
- **FAIL**: the case fails even when each inconclusive trial passes.
- **INCONCLUSIVE**: all other cases.

Both bounds divide by all trials and compare with the threshold as an exact
decimal. The score on the case line divides by the conclusive trials only. The
line shows the inconclusive trials and their reasons, for example
`[PASS] skill_activation_memory 4/4 (1.000, threshold 0.80, 1 inconclusive: timeout)`.

- **Per-category:** GREEN (all pass), INCONCLUSIVE (no case fails, and some are
  inconclusive), YELLOW (>= 80% pass), RED (< 80% pass)
- **Overall:** cases passed / total cases, with the number of failed and
  inconclusive cases
- **Exit code:** 0 if all cases pass, 1 if any case fails, or 3 if no case fails and some case is inconclusive.
  A harness abort uses exit `2`, including a nonzero prompt result, unconfirmed containment, invalid configuration, container death, or evidence failure.
  The order is 2, then 1, then 3.

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

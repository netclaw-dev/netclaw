# SPEC-004: CLI Contract

Source PRDs: `PRD-004`, `PRD-002`, `PRD-001`

## Purpose

Define the command-line contract for operator onboarding, configuration,
security diagnostics, and session operations.

## CLI Execution Environment

The internal `CliContext` holds the environment for `pair`, `update`, and `approvals`.
It contains `NetclawPaths`, `TimeProvider`, input, output, and error streams.
The caller owns the streams. The context does not dispose them.
Other commands retain their current signatures in this first slice.

`Program.cs` owns composition. Offline branches use their existing paths.
Host branches resolve the context from the final path and clock registrations.
TUI components retain their terminal services.
Inner services receive only the dependencies that they need.
Arguments, cancellation tokens, HTTP clients, and update policy remain separate.
The context contains no service provider, actor, transport, or mutable state.

`pair` uses the supplied input and its clock for the exchange deadline.
`approvals` uses the supplied home and clock for its store and grant timestamps.
`update` sends command progress and warnings to the supplied streams.
The background update notice retains stderr after the TUI closes.
The update daemon manager uses the supplied home and clock.
The systemd owner check uses that same home.

For example, an update for a custom home cannot stop a unit that serves another home.
An approval grant uses the supplied clock timestamp, not the process clock.
The commands retain their current endpoint, manifest, file, and approval checks.
No configuration or stored-data migration applies.
See the [engineering glossary](GLOSSARY.md) for shared terms.

```text
Program -> existing paths, clock, and streams -> CliContext
  pair -> protected exchange -> existing client and secrets files
  approvals -> existing grant rules -> store for the supplied home
  update -> verified manifest and assets -> daemon owner check for the supplied home
```

This flow is schematic. It omits individual security gates and file operations.

## Command Families

### 1) Initialization

- `netclaw init`

Behavior:

- creates baseline config files if missing
- generates ACL skeleton in default-deny mode
- prints required environment variable checklist
- supports interactive guided setup mode by default
- supports non-interactive mode via explicit flags for automation

Guided setup sequence:

1. choose runtime profile (`local` / `remote`)
2. configure Slack Socket Mode tokens
3. configure model provider (default: OpenRouter)
4. scaffold ACL policy and owner allowlist
5. validate configuration and print startup command

### 2) Configuration

- `netclaw config show [--format text|json]`
- `netclaw config validate [--strict]`
- `netclaw config retention [--logs-days <days>]`

Behavior:

- `netclaw config retention` shows how long the daemon keeps data. Each option sets one
  `Retention:*:Days` key in `netclaw.json` and rejects a value that is not a whole number
  of days from 0 to 36500 with exit code 1. Zero keeps the data forever.
- The shared config editor session validates the final document after the version assignment and before persistence.
  If the runtime configuration loader rejects the document, the editor preserves the original file.
- structured validation with property path and remediation hints
- non-zero exit code on validation failure

### 3) ACL and Policy

- `netclaw acl validate`
- `netclaw acl test --channel <id> --sender <id> [--mentioned true|false]`
- `netclaw acl explain --channel <id> --sender <id> [--tool <name>]`

Behavior:

- produces effective decision and reasons
- includes deny reason codes suitable for automation

### 4) Diagnostics

- `netclaw status`
- `netclaw doctor`

Behavior:

- status summarizes connector health, persistence reachability, active shell mode,
  and runtime policy state
- doctor emits actionable diagnostics in priority order, including strict-default
  trust-policy checks, unsafe audience-profile combinations, and sandbox-shell
  readiness when applicable

`netclaw doctor --fix --yes --format json` writes one JSON document after the diagnostic checks complete.
A file access failure during the repair returns exit code 1 with stderr only.
The command writes backup notices to stderr. Text output keeps backup notices on stdout.
A dry run creates no backup and writes no backup notice.

### 5) Session Operations

- `netclaw session inspect --session <channel/threadTs>`
- `netclaw session compact --session <channel/threadTs> [--dry-run]`

Behavior:

- inspect is read-only
- compact requires explicit confirmation unless `--yes` is supplied

### 6) Prompt and Tools

- `netclaw prompt show`
- `netclaw prompt validate`
- `netclaw tools list`
- `netclaw tools policy --tool <name>`
- `netclaw mcp list`
- `netclaw mcp validate [--server <name>]`
- `netclaw mcp test --server <name> --tool <name>`
- `netclaw test smoke [--provider ollama]`

Behavior:

- prompt validation checks required opening/zero clause sections
- tools policy command reports effective grant state and the resolved
  audience-profile scope when policy limits apply
- `netclaw mcp list` reports daemon-backed per-server runtime status and discovered tools
- `netclaw doctor` may include daemon-backed MCP auth/connectivity truth when available, and must label offline-only OAuth checks as non-authoritative
- smoke test command runs optional live integration checks outside CI-required
  test suite

### 7) External Skill Sync

`netclaw skill list` and `netclaw skill show <name>` use the daemon registry.
The detail endpoint requires the same authentication as the inventory endpoint.
The daemon owns file reads. Shared wire projections perform no file access.
An unavailable daemon causes a visible error without a disk fallback.

`netclaw skill remove <name>` requires the configured local daemon endpoint.
The CLI refuses a remote or different endpoint before an HTTP request or local deletion.
It permits deletion only for the exact `native` source label.
Unknown labels fail closed so future source types receive no implicit delete authority.

The CLI checks both the file and directory through the shared filesystem authority.
Both paths must stay within the trusted skills root and cross no links below it.
The trusted root and its ancestors may contain links.
A flat skill deletes only its file. A directory skill cannot delete the skills root.

Positive example: a native skill below a home alias can be removed without removal of the skills root.
Negative example: a daemon path through nested directory links cannot delete a directory outside the skills root.
Negative example: a remote daemon with the same path cannot authorize a local deletion.

The CLI owns the call-local deletion checks. The daemon owns the registry snapshot.
These checks do not provide an OS sandbox against concurrent path replacement.

Schematic flow; HTTP errors and file errors abort the command:

```text
require the configured local endpoint and a loopback address
detail = daemon registry lookup(name)
require detail.source == "native"
require file and directory paths within the skills root, without links below it
if detail.isFlatFile: delete only the file
else: require directory != skills root; delete the directory; prune an empty category
```

`netclaw skill sync` runs the daemon's configured external source job immediately.
It does not save configuration or add sources. System skills come from the installed binary.
See the [engineering glossary](GLOSSARY.md) for shared terms.

The command sends an authenticated `POST /api/skills/sync` request.
The endpoint uses the existing daemon authorization policy.
An unauthenticated request receives HTTP 401 and cannot start a pass.
An authenticated request can join a pass that the startup path, timer, or another operator started.

`ServerFeedSkillSyncActor` owns the timer, active pass state, waiters, and lifetime token.
This state is actor-local. The CLI owns only its call-local request wait.
`ServerFeedSkillSyncService` runs one pass and keeps no lifecycle state.
The feed helpers retain the existing durable files and sync receipts.

```text
CLI -> daemon authorization -> ServerFeedSkillSyncActor
  actor stops during a pass: return HTTP 503
  add the caller to the actor-local waiter set
  active pass exists: wait for that pass
  otherwise: call ServerFeedSkillSyncService with the actor lifetime token
  for each enabled feed:
    run the existing RFC skill and native sub-agent sync
    collect its result; continue after a source failure
  refresh the complete inventory through SkillInventoryRefresher
  send the result to the actor
  actor -> send the same result to all waiters
CLI -> print the result -> exit 0 or 1
```

The client has no fixed HTTP timeout for this operation. Existing source timeouts still apply.
The CLI prints a wait notice before it sends the request. The notice explains Ctrl+C.
Ctrl+C cancels the CLI wait. It does not cancel the shared pass.
Actor shutdown cancels the pass and fails its joined requests. Those HTTP requests receive HTTP 503.
An interval of zero disables periodic checks. Startup and manual checks remain available.

| Result | Command behavior |
|---|---|
| All sources and the inventory refresh succeed | Exit 0, including an empty source list |
| A source download fails or its scanner rejects a skill | Exit 1; report counts; let other feeds finish |
| The optional native sidecar does not exist | Report `absent`; RFC sync can succeed |
| An advertised sidecar page is missing or malformed | Report failure; retain existing managed agents |
| The final inventory refresh fails | Exit 1, even if all downloads succeed |
| The daemon predates the endpoint | Exit 1; request a daemon restart |
| The daemon returns HTTP 503 | Exit 1; report that the daemon cannot run the pass now |
| The daemon returns another HTTP error | Exit 1; report the status code, distinct from a connection failure |

The response includes one pass ID, per-source counts, sidecar status, and the final inventory result.
The service assigns the pass ID before source work and includes it in its start and completion logs.
The response contains no derived overall success field. The CLI computes its exit code from the source and inventory results.
Overlapping callers receive the same pass ID. Source errors in this response do not include credentials or remote response bodies.
Inventory rejection counts can include pre-existing source conflicts. They do not make a completed inventory refresh fail.
This command does not add a transaction across feed files, the registry, and the prompt index.
It preserves the existing per-skill replacement and prune rules.
For a prune, the changed count includes each obsolete skill once if its receipt or owned directory is removed.
A missing directory does not prevent receipt removal. An orphan directory can count without a receipt.
A directory deletion failure increments the failure count. The result can report both a removed receipt and a failed directory deletion.

For example, a healthy feed can update while another feed returns HTTP 500. The command reports both results and exits 1.
A rejected skill retains its prior bytes and receipt. Other accepted skills from that feed can still update.
Download failures do not create security alerts. This change does not alter the existing scanner or alert policy.

## Output and Exit Codes

`netclaw daemon --help`, `netclaw daemon -h`, and `netclaw daemon help` print help and exit with code `0`.
These commands do not start, stop, or modify the daemon service.

- default output: human readable text
- optional output: JSON for automation
- exit code `0` for success
- exit code `1` for validation, policy, or runtime failures
- expected model-configuration failures, including migration and named-role resolution errors,
  are validation failures: print actionable output, return exit code `1`, and do not create crash
  logs or emit stack traces
- exit code `2` for usage and argument errors

## Headless Single-Prompt Output

`netclaw chat -p <text>` sends one prompt and prints the reply. `--json` prints one JSON envelope on
stdout instead. A script reads stdout, stderr, the exit code, and the session log.

A run can span more than one turn. `spawn_agent` returns `accepted` in turn 1, and the child result
arrives in a later parent turn. The run ends on the first completed turn that reports no open child
run. See [Waiting for child runs](#waiting-for-child-runs).

A failed turn or a failed run exits `1`. A run whose last turn completes exits `0`. The exit code does not carry
the reason. A tool loop has no fixed ceiling: `Session.MaxToolIterationsPerTurn` is a legacy field. A
model that keeps changing its tool arguments runs until the operator cancels the run or an outside
`timeout` stops it. A run that an outside `timeout` kills exits `124`. `timeout` sends SIGTERM, and the CLI handles
SIGTERM like SIGINT, so stderr usually also has `[turn:failed] category=infra kind=Cancelled`. A reader must
check the exit code first: `124` means `timeout`, whatever stderr holds.

For a failed turn or a failed run, stderr has exactly one line that starts with `[turn:failed]`. It comes
after the `[error]` or `[headless:error]` line:

```text
[turn:failed] category=<model|infra> kind=<Kind>
```

- `category` is `model` only for `kind=ModelBehavior`. The model broke a rule that the runtime
  enforces: it returned repeated empty replies and each stream finished with the finish reason `stop`
  or `length`.
- A text reply that finished with the finish reason `length` and has no tool call is also
  `kind=ModelBehavior`: the model used all of its output budget. One model call that streamed only
  thinking until the thinking cap (three times the no-progress timeout) is also `kind=ModelBehavior`.
  In both cases the provider worked.
- A repeated refused tool call is not a failure. The runtime refuses the call again, adds a nudge for
  the model, and continues the turn. The run gets no `[turn:failed]` line for it.
- After the fifth refused repeat in a turn, the nudge escalates. A sixth refused repeat ends the turn
  quietly. The run exits with code 0 and prints no `[error]` or `[turn:failed]` line. The JSON envelope
  has no `errorCategory` or `errorKind`. Its `response` holds only text that the model wrote, and it can
  be empty.
- Repeated empty replies from a stream with no finish reason (the connection closed early) are
  `kind=StreamFailure`. Repeated empty replies with the finish reason `content_filter`, or any other
  finish reason, are `kind=ProviderFailure`. Both are `infra`: the model did not get a fair turn.
- `category` is `infra` for every other kind. A failure with no classification is `infra`. A reader
  must never score an unknown failure as a model failure.
- `kind` is the `ErrorCategory` name of the last `ErrorOutput` before the failed turn: `ModelBehavior`,
  `ProviderFailure`, `StreamFailure`, `Timeout`, `ToolFailure`, `InputCompatibility`, or `Unknown`.
  A failed turn with no `ErrorOutput` is `Unknown`.
- The CLI adds two kinds: `ChannelFailure` (the daemon is unreachable, rejects the client, the prompt
  is over the hub frame limit, or the connection is lost during a wait) and `Cancelled` (SIGINT or SIGTERM). A drained daemon restart during a wait
  also gives `ChannelFailure`. A run with one of these kinds has no JSON envelope. It has a session log
  record only for the turns that finished before the stop.
- An error that the turn survived (a compaction timeout, a context overflow recovery) does not decide
  the kind. A turn that completes prints no token.

The CLI replaces each line break in the error text with a space before it writes the `[error]` or
`[headless:error]` line. Error text comes from tools, providers, and models, so a line break in it could
forge a second `[turn:failed]` line. Only the CLI token writer starts a stderr line with the token.

The session log has one `TURN_FAILED: category=<c> kind=<k>` record before `TURN_COMPLETED`. With
`--json`, the envelope of a failed turn adds `errorCategory` and `errorKind`. A turn that completes has
neither key. Stdout never holds the token line.

The session log is not safe against forged lines. A model reply or a tool result can hold a line that
looks like `TURN_FAILED: ...`. A reader of the session log uses the last `TURN_FAILED` record before the
final `TURN_COMPLETED`, and only when the exit code is `1`.

Positive example: the model repeats `web_search("same")` after the runtime refused that call, up to the
sixth refused repeat. The runtime ends the turn quietly and makes no more model requests. The exit code
is `0`. Stderr has no `[turn:failed]` line. The JSON envelope `response` holds only the text that the
model wrote, and it can be empty. If the model answers with text after the refusal instead, the turn
completes with that text.

Negative example: the provider returns HTTP 503. The kind is `ProviderFailure` and the category is
`infra`. A harness must not score this run as a model failure.

Owner: `HeadlessChannel` maps the category. `LlmSessionActor` assigns `ModelBehavior`.

### Waiting for child runs

Every `TurnCompleted` output carries `OpenChildRunIds`. The session owns this fact (see the
[glossary](GLOSSARY.md#open-child-run)). The wire field is `openChildRunIds`. A new daemon always writes
it, as an empty array when no run is open. A `TurnCompleted` without the field comes from an earlier
daemon that runs `spawn_agent` inside one turn. The CLI reads the missing field as "unknown", maps it to
an empty list, and ends the run on that turn. This keeps the earlier behavior.

```text
on TurnCompleted t:                       (schematic: it omits logs and JSON detail)
    if t.Outcome is Failed         -> print [turn:failed]; end the run; exit 1
    if t.OpenChildRunIds is empty  -> end the run; exit 0
    otherwise                      -> print [subagent:waiting]; wait for the next TurnCompleted
```

- While the run waits, text mode prints one line on stdout after each turn that reports open runs:
  `[subagent:waiting] <n> open: <runId>[,<runId>...]`. JSON mode prints no such line on stdout.
- The child result reaches the session as a notice input, and the notice turn is an ordinary turn. The notice
  names `result.md` and holds no child output. Stdout holds the child text only when the model reads that file
  with `file_read`, which prints as `[tool:result] file_read`.
- A background shell job is not a child run. The run does not wait for it.
- A failed turn ends the run at once, with or without open runs. Stderr holds exactly one
  `[turn:failed]` line. The CLI ignores every output that arrives after the run ends, so the failed
  run prints one failure line and a JSON run prints one envelope.
- The CLI records the open runs of a turn before it prints the waiting line.
- The session log gets `WAITING_FOR_CHILDREN: count=<n> runs=<ids>` for each turn that reports open runs.
  It gets `SESSION_ENDED` once, at the end of the run.
- The run has no timeout of its own. The daemon bounds each child with its child timeouts. The operator
  (SIGINT) or an outside `timeout` bounds the run.

On each failure or stop path, stderr names the open runs. The paths are a failed turn, SIGINT, SIGTERM
(including an outside `timeout`), `SessionDeactivated`, and a lost connection (see
[A lost connection](#a-lost-connection)). The CLI writes one line after the `[turn:failed]` line:

```text
[headless:open-runs] session=<sessionId> runs=<runId>[,<runId>...]
```

The CLI takes the runs from the most recent `TurnCompleted`. It writes no line when that list is empty,
and it writes none before the first `TurnCompleted`. The CLI does not cancel the child runs. The
operator resumes the session with `netclaw chat --resume <sessionId>`.

| Case | Exit | Stderr |
|------|------|--------|
| Last turn completes with no open child run | `0` | none |
| A turn fails (with or without open runs) | `1` | `[turn:failed] ... kind=<Kind>`, plus the open-runs line |
| `SessionDeactivated` before the run ends | `1` | `kind=ChannelFailure`, plus the open-runs line |
| Connection state `Disconnected` after the prompt, before the run ends | `1` | `[headless:error]`, `kind=ChannelFailure`, plus the open-runs line when a run is known |
| Connection state `TransportClosed` after the prompt, before the run ends | `1` | `[headless:error]`, `kind=ChannelFailure`, plus the open-runs line when a run is known |
| SIGINT or SIGTERM before the run ends | `1` | `kind=Cancelled`, plus the open-runs line |
| An outside `timeout` ends the run | `124` | `kind=Cancelled` (usually), plus the open-runs line |

The daemon forwards `SessionDeactivated` to the client as the wire type `session_deactivated`. A drained
daemon restart during the wait sends it, and the run then ends with exit `1`. The CLI writes a
`[headless:error]` line that says the session stopped, before the `[turn:failed]` line. A daemon that
stops without a drain sends nothing. [A lost connection](#a-lost-connection) covers that case.

Positive example: turn 1 starts one child and reports one open run. The child finishes. Turn 2 reports
none. Stdout holds the status text, `[subagent:done]`, and the turn 2 text.
The exit code is `0`.

Negative example: turn 1 fails with a provider error while a child runs. The CLI does not wait. It prints
one `[turn:failed] category=infra kind=ProviderFailure` line and the open-runs line, and it exits `1`.
A run that ends on turn 1 with exit `0` and only a status line, while a child runs, is a defect.

Owner: `LlmSessionActor` assigns `OpenChildRunIds`. `HeadlessChannel` decides when the run ends.

Compatibility: the CLI and the daemon must have the same version. A CLI from before this contract ignores
`openChildRunIds`. Against a new daemon, it exits at turn 1 with exit `0` and loses the child result and the
follow-up turn. A new CLI against an earlier daemon works as before: the missing field ends the run on that
turn. An operator upgrades the CLI with the daemon.

#### A lost connection

The hub drops session output while no client is connected, and it does not replay that output after a
reconnect. A client that reconnects receives only the output that comes after the reconnect. A turn that
completes during a gap is lost. A client that only waited would wait for a turn that never comes, or it would
end with exit `0` and incomplete output. After the CLI sent the prompt, it therefore ends the run with exit
`1` on these connection states:

- `Disconnected`. The client reached it after its reconnect attempts failed.
- `TransportClosed`, whether or not the run knows an open child run. The client reconnects after this
  state, but the reconnect cannot restore a lost turn.

A short network drop therefore fails the run, and the operator resumes the session. Before the CLI sent the
prompt, a connection state does not end the run: no output is in flight, and the request that the run
thread waits for fails with its own reason when the connection cannot be restored.

The CLI marks the prompt as sent just before it sends the prompt. After such a failure, the daemon may
still run the prompt. Check the session, for example with `netclaw chat --resume <sessionId>` or the session
log, before you send the prompt again.

The CLI writes one `[headless:error]` line that names the lost connection, then
`[turn:failed] category=infra kind=ChannelFailure`, then the open-runs line when the run knows an open run.
With `--json`, it writes no envelope. The CLI does not cancel the child runs. A `Disconnected` or
`TransportClosed` state after the run ended changes nothing, and it writes no log record: a run that ended
with exit `0` stays `0`.

A failed run never exits `0`. The channel sets exit code `1` in the same step that marks the run ended. When
the run thread fails for its own reason, for example a bind that fails with an unsupported text admission
version, the `[headless:error]` line names that reason and does not say that the connection was lost.

The true bound of a run is the first of these events: a failed turn; a turn that reports no open child run;
`SessionDeactivated`; `Disconnected` or `TransportClosed` after the prompt; SIGINT or SIGTERM; an outside
`timeout`. The CLI has no timer of its own. A script SHOULD run `chat -p` under `timeout`, so that an outside
limit always bounds the run.

Positive example: the run waits with one open run. The network drops. The CLI exits `1`, and stderr holds
`[turn:failed] category=infra kind=ChannelFailure` and `[headless:open-runs] session=<id> runs=<run>`.
The operator runs `netclaw chat --resume <id>`.

Negative example: the run waits, the network drops for 10 seconds, the follow-up turn completes in the gap,
and the CLI waits with no output until SIGINT. That is a defect.

#### JSON envelope for several turns

With `--json`, stdout holds exactly one envelope. The CLI writes it when the run ends on a completed
turn or on a failed turn. It writes no envelope for a signal or for `SessionDeactivated`.

- `response` is the text of the last turn.
- `turnResponses` is an array with one entry for each completed turn, in order. An entry holds
  `turnNumber`, `text`, `toolCalls`, and `usage`. A run with one turn has one entry.
- `toolCalls` holds the tool calls of all turns, in order.
- `usage` is the sum over all turns of `inputTokens`, `outputTokens`, `totalTokens`,
  `cachedInputTokens`, and `reasoningTokens`. `promptMs` and `predictedPerSecond` are rates, so they do
  not add up. They come from the last turn that has a usage record. If that record has no rate, the
  rate is `null`. A turn with no usage record does not count.
- A failed turn is the last entry. The envelope adds `errorCategory` and `errorKind`.

Positive example: turn 1 reports 10 input tokens and turn 2 reports 7. The envelope has
`usage.inputTokens` `17`, two `turnResponses`, and `response` equal to the turn 2 text.

Negative example: a run with no child. The envelope has one `turnResponses` entry, and `response`,
`toolCalls`, and `usage` match the single-turn envelope that the CLI wrote before this contract.

## Safety Rules

- read-only default for all inspection commands
- mutating commands require explicit confirmation or `--yes`, except `skill sync`; that command explicitly requests the existing daemon job
- no command may silently broaden exposure policy

## Onboarding State Persistence

- onboarding writes progress markers to config metadata
- `netclaw init --resume` continues incomplete onboarding
- `netclaw init --reset` restarts onboarding after explicit confirmation

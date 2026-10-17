## ADDED Requirements

### Requirement: A headless run ends after the last child result

`netclaw chat -p` SHALL end a run after a turn that completes and reports no open child run. Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for the term "open child run". The session owns that fact. The CLI SHALL NOT infer it from the count of `SubAgentOutput` events.

While open child runs remain, the CLI SHALL keep its subscription and wait for the next completed turn. In text mode, it SHALL print one line on stdout after each such turn:

```text
[subagent:waiting] <n> open: <runId>[,<runId>...]
```

The session log SHALL get one `WAITING_FOR_CHILDREN: count=<n> runs=<ids>` record for each such turn. It SHALL get one `SESSION_ENDED` record at the end of the run, not at the end of each turn.

A `TurnCompleted` output that carries no `OpenChildRunIds` field comes from a daemon that runs `spawn_agent` inside one turn. The CLI SHALL treat the field as "unknown" and end the run on that turn. This keeps the behavior of the earlier daemon.

Background shell jobs SHALL NOT count as open child runs. A run does not wait for them.

The CLI SHALL record the open runs of a turn before it prints the waiting line. After the run ends, the CLI SHALL ignore every later output. A failed run then has exactly one `[turn:failed]` line, and a JSON run has exactly one envelope.

Schematic flow (it omits logs, JSON detail, and failure paths):

```text
on TurnCompleted t:
    if t.Outcome is Failed          -> end the run, exit 1
    if t.OpenChildRunIds is empty   -> end the run, exit 0
    otherwise                       -> print [subagent:waiting], keep waiting
```

Owners: `LlmSessionActor` decides which runs are open (durable state). `HeadlessChannel` decides when the run ends (call-local state).

#### Scenario: One child run ends the run after the follow-up turn

- **GIVEN** a headless run whose first turn starts one child run
- **WHEN** turn 1 completes and reports that run as open
- **THEN** the CLI prints `[subagent:waiting] 1 open: <runId>` and does not exit
- **AND** the CLI exits `0` after turn 2 reports no open run
- **AND** stdout holds `[subagent:done]`, the child result, and the turn 2 text

#### Scenario: A child that finishes inside turn 1 does not end the run early

- **GIVEN** a child run finishes before turn 1 completes
- **AND** the session starts turn 2 at once after `TurnCompleted` of turn 1
- **WHEN** the CLI receives `SubAgentOutput` with phase completed and then `TurnCompleted` of turn 1
- **THEN** turn 1 reports the run as open
- **AND** the CLI waits for turn 2 and does not exit at turn 1

#### Scenario: Two ready children need two follow-up turns

- **GIVEN** two child runs have ready results at the same time
- **WHEN** turn 1 completes, then turn 2 completes, then turn 3 completes
- **THEN** turn 1 reports two open runs and turn 2 reports one open run
- **AND** the CLI exits after turn 3, which reports none
- **AND** the JSON `response` is the text of turn 3

#### Scenario: A follow-up turn that starts a new child keeps the wait

- **GIVEN** the follow-up turn calls `spawn_agent` again
- **WHEN** that turn completes
- **THEN** it reports the new run as open
- **AND** the CLI keeps waiting

#### Scenario: A run with no child ends at its first turn

- **GIVEN** a headless run whose turn starts no child run
- **WHEN** the turn completes with an empty `OpenChildRunIds`
- **THEN** the CLI exits `0` at once
- **AND** stdout has no `[subagent:waiting]` line
- **AND** the output matches the output of the earlier single-turn run

#### Scenario: A background shell job does not hold the run

- **GIVEN** a turn leaves a background shell job running and no open child run
- **WHEN** the turn completes
- **THEN** the CLI exits `0`
- **AND** it prints no `[subagent:waiting]` line

#### Scenario: An earlier daemon sends no field

- **GIVEN** a daemon that does not send `openChildRunIds`
- **WHEN** a `TurnCompleted` output arrives without the field
- **THEN** the CLI ends the run on that turn
- **AND** it does not wait

#### Scenario: Output after the end of the run is ignored

- **GIVEN** turn 2, a follow-up turn, fails and ends the run
- **WHEN** the CLI receives the output of a turn 3 that the session starts later
- **THEN** the CLI prints none of that output
- **AND** stderr still holds exactly one `[turn:failed]` line

### Requirement: A failed or stopped headless run names its open child runs

A turn that fails SHALL end the run at once with exit `1`. The CLI SHALL NOT wait for open child runs after a failed turn. Stderr SHALL still hold exactly one `[turn:failed]` line, as [SPEC-004](../../../../../docs/spec/SPEC-004-cli-contract.md) requires.

On every failure or stop path, the CLI SHALL write one line on stderr after the `[turn:failed]` line, when the run knows at least one open child run:

```text
[headless:open-runs] session=<sessionId> runs=<runId>[,<runId>...]
```

The failure or stop paths are: a failed turn, SIGINT, SIGTERM (including an outside `timeout`), and `SessionDeactivated`. The run knows the open runs from the most recent `TurnCompleted`. Before the first `TurnCompleted`, the run knows none, so the CLI writes no such line.

The CLI SHALL NOT cancel child runs. The session owns them. An operator reads the session ID and resumes with `netclaw chat --resume <sessionId>`.

#### Scenario: A failed turn with an open child ends the run

- **GIVEN** turn 1 fails with a provider error while a child run is open
- **WHEN** the CLI receives the failed `TurnCompleted`
- **THEN** the CLI exits `1` without a wait
- **AND** stderr holds exactly one `[turn:failed] category=infra kind=ProviderFailure` line
- **AND** stderr holds the `[headless:open-runs]` line with the run ID and the session ID

#### Scenario: A failed turn with no open child has no open-runs line

- **GIVEN** a turn fails and reports no open child run
- **WHEN** the CLI receives the failed `TurnCompleted`
- **THEN** the CLI exits `1`
- **AND** stderr holds no `[headless:open-runs]` line

#### Scenario: A signal during the wait ends the run loudly

- **GIVEN** the CLI waits with one open child run after turn 1
- **WHEN** the process gets SIGINT or SIGTERM
- **THEN** the CLI exits `1` and stderr holds `[turn:failed] category=infra kind=Cancelled`
- **AND** stderr holds the `[headless:open-runs]` line
- **AND** the run never exits `0` with only a status line

#### Scenario: A signal before the first completed turn names no run

- **GIVEN** the CLI sent the prompt and turn 1 has not completed
- **WHEN** the process gets SIGINT
- **THEN** the CLI exits `1` with `kind=Cancelled`
- **AND** stderr holds no `[headless:open-runs]` line

#### Scenario: Session deactivation during the wait ends the run loudly

- **GIVEN** the CLI waits with one open child run after turn 1
- **AND** the daemon restarts, so the session emits `SessionDeactivated`
- **WHEN** the CLI receives `SessionDeactivated`
- **THEN** the CLI exits `1` and stderr holds `[turn:failed] category=infra kind=ChannelFailure`
- **AND** stderr holds the `[headless:open-runs]` line
- **AND** the run does not hang

### Requirement: The JSON envelope covers every turn of the run

With `--json`, the CLI SHALL write exactly one envelope on stdout, when the run ends by a completed turn or by a failed turn. The CLI SHALL NOT write an envelope for a signal or a session deactivation. An envelope that omits earlier turns would hide the child result.

The envelope SHALL hold:

- `response`: the text of the last turn
- `turnResponses`: an array with one entry for each completed turn, in order. Each entry holds `turnNumber`, `text`, `toolCalls`, and `usage`.
- `toolCalls`: the tool calls of all turns, in order
- `usage`: the sum of the token counts of all turns (`inputTokens`, `outputTokens`, `totalTokens`, `cachedInputTokens`, `reasoningTokens`)

A run with one turn has one entry in `turnResponses`. Its `response`, `toolCalls`, and `usage` equal the values of the earlier single-turn envelope. A failed turn adds `errorCategory` and `errorKind` and counts as the last turn.

#### Scenario: Two turns give one envelope with both turns

- **GIVEN** a run with turn 1 (text A, input tokens 10) and turn 2 (text B, input tokens 7)
- **WHEN** the run ends after turn 2
- **THEN** stdout holds exactly one JSON envelope
- **AND** `response` is `B` and `turnResponses` holds two entries in order
- **AND** `usage.inputTokens` is `17`
- **AND** `toolCalls` holds the calls of both turns

#### Scenario: A signal during the wait writes no envelope

- **GIVEN** the CLI waits after turn 1 in `--json` mode
- **WHEN** the process gets SIGINT
- **THEN** stdout holds no JSON envelope
- **AND** the exit code is `1`

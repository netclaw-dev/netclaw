## ADDED Requirements

### Requirement: spawn_agent executes as a streaming tool

The `spawn_agent` tool SHALL execute as a streaming tool: while the sub-agent
runs, the tool call SHALL emit activity items reflecting sub-agent progress and
SHALL finish with a terminal completion item carrying the sub-agent result. The
parent session SHALL NOT bound the sub-agent with a dedicated wall-clock `Ask`
timeout; sub-agent liveness SHALL be observed through the tool call's stream and
its per-call inactivity watchdog.

#### Scenario: Sub-agent progress surfaces as activity items

- **GIVEN** a `spawn_agent` tool call
- **WHEN** the spawned sub-agent is making progress
- **THEN** the tool call emits activity items while the sub-agent runs
- **AND** the call's per-call inactivity watchdog is satisfied by that activity

#### Scenario: Wedged sub-agent is caught without affecting siblings

- **GIVEN** two `spawn_agent` tool calls running in parallel
- **AND** one sub-agent is wedged and emits no activity
- **WHEN** the wedged call's inactivity budget elapses
- **THEN** that call yields a terminal timeout error
- **AND** the healthy sub-agent completes normally and returns its result
- **AND** both tool-result messages reach the LLM

### Requirement: Sub-agent runs are bounded by inactivity and iteration count

A sub-agent run SHALL be bounded by the per-call inactivity watchdog and by the
sub-agent's maximum tool-iteration count. The system SHALL NOT impose an absolute
wall-clock cap on a sub-agent run that is continuously producing activity.

#### Scenario: A responsive long sub-agent is not killed by a wall-clock cap

- **GIVEN** a sub-agent that runs longer than any single inactivity budget
- **AND** it emits activity continuously
- **THEN** it is not terminated by an absolute wall-clock timeout
- **AND** it runs until completion or the tool-iteration limit

#### Scenario: A stalled sub-agent is terminated by inactivity

- **GIVEN** a sub-agent that stops producing any activity
- **WHEN** its inactivity budget elapses
- **THEN** the run is terminated and the call yields a timeout error

### Requirement: Sub-agents cannot spawn sub-agents

The `spawn_agent` tool SHALL be denied to sub-agents by a single tool-policy
denylist applied when a sub-agent's tool set is resolved. A sub-agent's resolved
tool set SHALL never include `spawn_agent`, regardless of parent audience policy
or advisory definition metadata.

#### Scenario: spawn_agent is absent from a resolved sub-agent tool set

- **GIVEN** a sub-agent profile that lists or inherits `spawn_agent`
- **WHEN** the sub-agent's tools are resolved
- **THEN** `spawn_agent` is excluded from the resolved tool set
- **AND** the sub-agent cannot invoke it

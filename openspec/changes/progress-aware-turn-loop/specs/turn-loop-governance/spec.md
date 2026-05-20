## ADDED Requirements

### Requirement: Per-turn tool loop limit is iteration-based

The turn loop SHALL enforce a per-turn limit using
`MaxToolIterationsPerTurn` rather than a count of individual tool calls.

A tool iteration SHALL mean one LLM response that requests one or more tool
calls, followed by execution of those tool calls and return of their results to
the LLM within the same user turn.

When the turn reaches `MaxToolIterationsPerTurn`, the session SHALL stop
further tool-enabled looping for that turn and SHALL enter the existing
force-no-tools completion path.

#### Scenario: Parallel tool batch counts as one iteration

- **GIVEN** an LLM response requests 8 tool calls in parallel
- **WHEN** those tool calls execute and their results are returned
- **THEN** the turn's iteration count increases by 1

#### Scenario: Multiple tool rounds count as multiple iterations

- **GIVEN** a turn contains 3 separate LLM responses that each request tools
- **WHEN** each response is followed by tool execution and result return
- **THEN** the turn's iteration count is 3

#### Scenario: Reaching the iteration cap ends tool-enabled looping

- **GIVEN** `MaxToolIterationsPerTurn` is 60
- **WHEN** the turn completes its 60th tool iteration without producing a
  final answer
- **THEN** the session stops additional tool-enabled iterations for that turn
- **AND** the session uses the existing force-no-tools completion behavior

#### Scenario: Raw tool-call volume does not control the limit

- **GIVEN** one iteration contains many parallel tool calls
- **WHEN** the turn remains below `MaxToolIterationsPerTurn`
- **THEN** the turn is not stopped solely because of the number of individual
  tool calls in that batch

## MODIFIED Requirements

### Requirement: Subagents use progressive tool disclosure

A subagent SHALL begin with the same policy-exposed core tool set as a main session, minus tools prohibited by subagent policy. It SHALL NOT eagerly receive every discoverable first-party or MCP tool. `search_tools` and `load_tool` SHALL activate deferred schemas only in that child actor's ephemeral exposure set.

A child policy denial SHALL produce the same `access_denied` receipt category as a parent policy denial. A replay that claims child catalog behavior SHALL create a real child actor and inspect that child's model-visible tools.

A schema loaded by a child SHALL remain available for later model iterations in
that same child run. It SHALL be discarded when the child completes or fails.
When a child parks, its child journal SHALL record the tool set of the run and
its loaded deferred tool names. A resumed child SHALL expose that recorded set,
intersected with the current agent definition and the current audience policy.
It SHALL NOT add a tool at resume. Child exposure
does not use the main session's configurable user-turn lease because a child run
has no independent sequence of user turns.

Netclaw intentionally prohibits recursive `spawn_agent` calls. A child cannot
create another child, even when the parent audience can use `spawn_agent`. This
keeps one parent tool call responsible for one bounded child actor and prevents
recursive agent trees from multiplying inference requests on self-hosted models
with limited concurrency.

Concrete exposure examples:

```text
parent core: [file_read, shell_execute, spawn_agent, ...]
child core:  [file_read, shell_execute, ...]
             # spawn_agent is removed by child policy

child calls load_tool(Name = "list_reminders")
  -> next child model request includes list_reminders
  -> later iterations in this same child still include list_reminders

child parks, the daemon restarts, the child resumes
  -> the resumed child still includes list_reminders

child completes; parent starts a fresh child
  -> the fresh child does not inherit list_reminders

child calls search_tools(Query = "spawn agent")
  -> response does not confirm spawn_agent exists

child directly calls spawn_agent from recalled text
  -> dispatch cannot resolve or execute it from the child-private registry
```

#### Scenario: Child starts with core rather than full catalog

- **GIVEN** the daemon has more than one hundred visible specialty and MCP tools
- **WHEN** a subagent starts
- **THEN** its first model request contains only its allowed core tools
- **AND** `search_tools` can find allowed deferred capabilities

#### Scenario: Child loads one deferred tool

- **GIVEN** a subagent knows the exact name of a visible deferred tool
- **WHEN** it loads that exact tool
- **THEN** the next child request contains the core plus that tool
- **AND** later model iterations in the same child retain that tool
- **AND** unrelated deferred schemas remain absent

#### Scenario: Loaded child schema survives a park

- **GIVEN** a child loads an allowed deferred tool and then parks
- **WHEN** the child resumes after a restart
- **THEN** its next model request contains the loaded tool

#### Scenario: Loaded child schema does not cross child lifetime

- **GIVEN** one child loads an allowed Deferred tool
- **WHEN** that child completes and the parent starts another child
- **THEN** the second child's first model request omits the loaded tool
- **AND** the first child created no durable or parent-owned exposure lease

#### Scenario: Child cannot discover recursive delegation

- **GIVEN** `spawn_agent` is registered for the parent session
- **WHEN** a subagent searches for or attempts to load it
- **THEN** the response does not confirm or activate `spawn_agent`
- **AND** a direct child dispatch cannot start a grandchild

#### Scenario: Child denial matches parent category

- **GIVEN** policy denies the same tool for a parent and a child
- **WHEN** each actor invokes that tool
- **THEN** each receipt category is `access_denied`
- **AND** neither actor records successful activity

#### Scenario: Replay inspects a real child catalog

- **GIVEN** a regression fixture asserts subagent catalog behavior
- **WHEN** the fixture executes
- **THEN** it creates a subagent through the production spawn path
- **AND** it asserts the child model request omits the hidden tool

### Requirement: Subagent execution contract

The system SHALL run subagents as actors (`SubAgentActor`) that execute an
autonomous LLM tool loop and return a single text result plus an optional
structured findings envelope. A subagent SHALL stop itself after completing its
task. A subagent SHALL write a child journal only when it parks at consent, and
the parent SHALL purge that journal after the run ends. Subagents SHALL NOT
persist durable memory or write directly to durable memory. Subagents SHALL NOT
participate in session pub/sub by default. The parent session SHALL durably
accept a child run before it starts the worker. `spawn_agent` SHALL return the
accepted run identity before the child completes. The parent SHALL retain run
status, terminal results, approval correlation, and cancellation state across
restart. Recovery SHALL NOT restart an uncertain child action.

For subagent execution launched from skill metadata routing, the subagent SHALL
remain isolated by default. It SHALL NOT inherit the main identity prompt stack
or load repository `AGENTS.md` by default. It SHALL inherit the audience and
authority boundary of the launching invocation.

#### Scenario: Child acceptance returns before child completion

- **GIVEN** a valid child request whose durable acceptance succeeds
- **WHEN** the parent calls `spawn_agent`
- **THEN** the parent receives the accepted run identity before the child completes
- **AND** the child starts only after the acceptance persists

#### Scenario: Failed acceptance starts no child

- **GIVEN** a valid child request whose durable acceptance fails
- **WHEN** the parent calls `spawn_agent`
- **THEN** the call returns a failure
- **AND** the system starts no child work

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes its LLM/tool loop and returns a `SubAgentResult`
- **AND** the result MAY include structured findings for the parent session to
  review
- **AND** stops itself

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** a subagent receives tool results for its task
- **WHEN** it requests further tools
- **THEN** the tool loop follows `turn-loop-governance`
- **AND** the system does not apply a separate child iteration ceiling

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** a subagent has completed 10 useful tool iterations
- **WHEN** it requests another distinct useful tool call
- **THEN** the system does not stop the child only because of the former iteration limit
- **AND** recurrence governance remains active

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a default subagent is executing within a user-facing session
- **WHEN** it attempts to persist durable cross-session memory directly
- **THEN** the durable write path is unavailable or denied to that subagent
- **AND** the subagent must return findings to the parent session instead

#### Scenario: Routed subagent does not inherit main identity prompt stack

- **GIVEN** a slash-invoked skill routes execution via `metadata.subagent`
- **WHEN** the routed subagent prompt is assembled
- **THEN** the main session identity prompt stack is not included by default

#### Scenario: Routed subagent does not auto-load repo AGENTS

- **GIVEN** a slash-invoked skill routes execution via `metadata.subagent`
- **WHEN** the routed subagent prompt is assembled
- **THEN** repo-local `AGENTS.md` is not auto-loaded by default

#### Scenario: Routed subagent inherits launch audience

- **GIVEN** a routed subagent activation launched from a parent invocation with
  audience `team`
- **WHEN** the subagent executes tool calls
- **THEN** tool execution context audience is `team`
- **AND** routed execution does not widen audience to a broader default

#### Scenario: A run that never parks writes no journal

- **GIVEN** a child run that needs no consent
- **WHEN** it completes
- **THEN** no child journal exists for it

### Requirement: Subagents maintain run-scoped working context
Each subagent SHALL own an ephemeral working context initialized by forking a read-only snapshot of the parent session's project directory, recent files, and immutable admitted-turn authority. The child SHALL own fresh call-local activity tracking and SHALL evolve its working state independently. The initial snapshot SHALL be included in the runtime-context portion of the child user message and SHALL NOT modify the reusable subagent system prompt. Child activity SHALL NOT mutate parent session state during execution. A resumed child SHALL restore its working context from its last checkpoint and its child journal, and its authority from the run record.

#### Scenario: Child receives parent recent-file grounding
- **GIVEN** a parent session with a project directory and recent files
- **WHEN** it spawns a permitted subagent
- **THEN** the child's initial model input contains the parent project directory and recent-file snapshot
- **AND** its tool execution uses the explicitly inherited admitted-turn authority

#### Scenario: Child file activity is isolated
- **GIVEN** a running child that reads or changes a file
- **WHEN** the child updates its run-scoped working context
- **THEN** the parent durable working context is unchanged until a successful child completion delta is handled
- **AND** another child cannot observe that call-local activity through shared mutable state

#### Scenario: Resumed child keeps its working context
- **GIVEN** a child that read a file and then parked
- **WHEN** the child resumes
- **THEN** its working context still lists that file and its project directory

## REMOVED Requirements

### Requirement: Sub-agent watchdog pauses during human approval

**Reason**: A child that needs consent parks and stops. No live approval wait exists, so the watchdog has nothing to pause.
**Migration**: The requirement "A child that needs consent always parks" covers the wait. A resumed child arms its watchdog for its next model call.

### Requirement: Sub-agent approval outcomes settle exactly once

**Reason**: The outcome no longer reaches a live wait. The parent records the answer, and a resumed child applies it. No consent timeout exists.
**Migration**: The requirement "Sub-agent consent outcomes settle once at resume" replaces it.

## ADDED Requirements

### Requirement: A child run has its own journal

A background child run that parks SHALL have its own child journal with the persistence ID
`subagent-{escaped session ID}/{run ID}`, where the session ID is URI-escaped. The persistence ID SHALL NOT start with
`session-`, and the child SHALL NOT appear in the session list. The prefix `subagent-{escaped session ID}/` SHALL match
exactly the child runs of that session with an ordinal prefix match. The child SHALL write its journal at its first
park, and not before. The first event SHALL freeze the exact system prompt, the resolved provider and model, the
resolved tool set, the project instructions, the operating rules, and the structured-findings flag. When the model
role fell back to another model, the first event SHALL record the model that it resolved to. The journal SHALL hold
the child conversation: messages, tool calls, and tool results. Each journal type SHALL bind to the project serializer
with a manifest and a mapper entry.

#### Scenario: Child journal stays out of the session list

- **GIVEN** a session with a parked child run
- **WHEN** the operator lists sessions
- **THEN** the list does not contain the child journal

#### Scenario: Prefix finds the children of one session

- **GIVEN** sessions `C1/1700` and `C1/1700/x`, each with one parked child run
- **WHEN** a reader selects persistence IDs with an ordinal prefix match for session `C1/1700`
- **THEN** it finds only the child of `C1/1700`

#### Scenario: The first event records the resolved model

- **GIVEN** an agent whose model role has no model, so the role resolves to the main model
- **WHEN** the child parks for the first time
- **THEN** the first journal event records the provider and the model that the role resolved to

### Requirement: A child that needs consent always parks

A background child SHALL park every time a call needs consent. The consent bridge SHALL post the prompt and SHALL
return at once without running the call. When each call of the batch has a result or is parked, the child SHALL record
its conversation and the parked calls, report the park to the parent, and stop after the parent records it.

#### Scenario: Live answer uses the resume path

- **GIVEN** a child parks at a consent request and the daemon does not restart
- **WHEN** the operator approves once
- **THEN** the parent recreates the child from its journal
- **AND** the parked call runs once

### Requirement: Sub-agent consent outcomes settle once at resume

Each parked call SHALL settle once, at resume, as approved or refused. An approved call SHALL run once with consent for
its exact candidates, and the consent SHALL NOT reach a sibling call, a later iteration, or a later run. A refused call
SHALL become a tool result that the child model sees, and the tool SHALL NOT run. A withdrawn call SHALL not resume.
Cancellation and actor termination SHALL NOT produce a duplicate child result.

#### Scenario: Approve once is retry-local

- **GIVEN** a parked call is approved once
- **WHEN** the child resumes and runs the call
- **THEN** the consent applies only to that call
- **AND** sibling calls, later iterations, and later runs still require consent when policy requires it

#### Scenario: Refused call becomes a tool result

- **GIVEN** a parked call is denied by the operator
- **WHEN** the child resumes
- **THEN** the tool does not run
- **AND** the child model receives a tool result that says that the operator denied it

# netclaw-subagents Specification

## Purpose

Define subagent execution contract, timeout enforcement, observability events,
model role conventions, and context layer awareness for ephemeral autonomous
LLM actors.

Use the [Netclaw engineering glossary](../../../docs/spec/GLOSSARY.md) for cross-cutting terms used in this specification.

## Requirements

### Requirement: Subagents use progressive tool disclosure

A subagent SHALL begin with the same policy-exposed core tool set as a main session, minus tools prohibited by subagent policy. It SHALL NOT eagerly receive every discoverable first-party or MCP tool. `search_tools` and `load_tool` SHALL activate deferred schemas only in that child actor's ephemeral exposure set.

A child policy denial SHALL produce the same `access_denied` receipt category as a parent policy denial. A replay that claims child catalog behavior SHALL create a real child actor and inspect that child's model-visible tools.

A schema loaded by a child SHALL remain available for later model iterations in
that same child run. It SHALL be discarded when the child completes, stops, or
fails. Child exposure does not use the main session's configurable user-turn
lease because a child run has no independent sequence of user turns.

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

### Requirement: Subagent tool exposure is observable without payloads

Subagent diagnostics SHALL report core, deferred-visible, and loaded tool counts
for each run. Exposure diagnostics SHALL NOT include tool argument values,
command text, file paths, schema bodies, or hidden tool names.

#### Scenario: Child startup logs bounded counts

- **WHEN** a subagent begins a run
- **THEN** one structured diagnostic records its three tool counts
- **AND** the event contains no authored payload or path

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that execute an autonomous LLM tool loop.
A child SHALL return one terminal text result with an optional structured findings envelope through the background owner contract.
It SHALL close task dispatch and retain that result until the owner acknowledges its durable receipt or the owner terminates.
It SHALL then stop itself.
Subagents SHALL NOT persist durable memory, stream direct durable-memory writes, or participate in session pub/sub by default.

For subagent execution launched from skill metadata routing, the subagent SHALL remain an isolated worker by default:

- It SHALL receive the embedded operating core for its audience, followed by the operator's deployment `AGENTS.md`.
- It SHALL NOT inherit `SOUL.md` or `TOOLING.md`.
- A child with a non-Public audience SHALL receive available project instructions from the inherited project directory.
- A Public child SHALL NOT receive project-local instructions.
- It SHALL inherit audience/boundary context from the launch invocation. Prompt content SHALL NOT widen that authority.

The child SHALL use the shared exact recurrence, repeat exception, and feedback-round contracts in `turn-loop-governance`.
It SHALL retain its detector evidence for its actor-local task lifetime.
It SHALL NOT substitute success for an absent receipt or create a durable child-resumption contract.
All explicit and routed starts SHALL use `background-subagent-runs` acceptance, lifetime, and later-result rules.

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the child finishes its task
- **THEN** it returns a `SubAgentResult` to its owner
- **AND** optional findings remain available for parent review
- **AND** the child stops after durable receipt acknowledgement or owner termination

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** the model returns `FunctionCallContent` tool calls
- **WHEN** the child processes that response
- **THEN** eligible calls use the normal dispatch and authorization contracts
- **AND** their paired results return to the model
- **AND** the loop continues until text completion or an explicit runtime terminal decision

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** a child exceeds the former tool iteration limit through useful work
- **WHEN** it requests another eligible call
- **THEN** no removed iteration ceiling forces a final model request
- **AND** operation health and recurrence rules remain effective

#### Scenario: Useful child work exceeds the former ceiling

- **GIVEN** a child task needs more than 30 useful feedback rounds
- **WHEN** a deterministic provider drives that task through real dispatch
- **THEN** no count-based stop ends the task
- **AND** independent artifact checks prove its required result

#### Scenario: Child recurrence settles without another model response

- **GIVEN** the shared recurrence contract requires terminal settlement
- **WHEN** the child settles that run
- **THEN** it returns `Partial` with `ToolCycleStopped` and confirmed activity
- **AND** it requests no final model response
- **AND** the owner persists and delivers that terminal result through the background contract

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a default child executes within a user-facing session
- **WHEN** it attempts a direct durable cross-session memory write
- **THEN** that path remains unavailable or denied
- **AND** the child returns findings for parent policy review instead

#### Scenario: Routed subagent receives scoped operating guidance

- **GIVEN** a slash skill routes through `metadata.subagent` and a deployment playbook exists
- **WHEN** its isolated child prompt is assembled
- **THEN** the embedded operating core for its audience appears before the deployment `AGENTS.md`
- **AND** neither `SOUL.md` nor `TOOLING.md` is included

#### Scenario: Routed subagent receives inherited project instructions

- **GIVEN** a Personal or Team slash activation inherits a project directory with an `AGENTS.md` file
- **WHEN** its isolated child prompt is assembled
- **THEN** available project instructions appear after the operating guidance and before the child role prompt
- **AND** the project instructions come from the inherited project directory

#### Scenario: Public routed subagent excludes project instructions

- **GIVEN** a Public slash activation inherits a project directory with an `AGENTS.md` file
- **WHEN** its isolated child prompt is assembled
- **THEN** the stripped Public operating core and available deployment playbook remain present
- **AND** project-local instructions remain absent

#### Scenario: Routed subagent inherits launch audience

- **GIVEN** a routed launch has audience `Team`
- **WHEN** the child executes a task tool
- **THEN** that tool uses inherited `Team` authority
- **AND** background lifetime does not widen its audience

### Requirement: User-facing target validation for routed skill execution

Subagent targets selected by skill metadata routing SHALL be validated against
the subagent registry before execution. Routed skill execution SHALL only allow
known user-facing subagent targets. Unknown targets and internal-only targets
SHALL fail deterministically and SHALL NOT execute.

#### Scenario: Unknown subagent target fails deterministically

- **GIVEN** a slash-invoked skill with `metadata.subagent: missing-agent`
- **WHEN** routed execution is requested
- **THEN** execution fails with a deterministic unknown-target error
- **AND** no subagent actor is spawned

#### Scenario: Internal-only subagent target fails deterministically

- **GIVEN** a slash-invoked skill with `metadata.subagent` pointing to an
  internal-only subagent
- **WHEN** routed execution is requested
- **THEN** execution fails with a deterministic not-user-facing error
- **AND** no subagent actor is spawned

### Requirement: Subagent findings handoff to owning session

When a subagent discovers information that may deserve durable memory, it SHALL
return that information as a structured findings envelope to the owning
session. The owning session SHALL evaluate policy, convert accepted findings
into checkpoints, and remain the default durable-memory owner.

#### Scenario: Parent session accepts findings for checkpoint review

- **GIVEN** a subagent returns findings that include stable project information
- **WHEN** the parent session evaluates the subagent result
- **THEN** the parent session converts the accepted findings into a durable
  memory checkpoint
- **AND** background curation proceeds under the parent session's policy scope

#### Scenario: Parent session rejects findings on policy grounds

- **GIVEN** a subagent returns findings whose domain or sensitivity violates the
  parent session's durable-memory policy
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the findings are dropped or kept transient only
- **AND** no durable memory write occurs

### Requirement: Subagent timeout enforcement

The system SHALL enforce child operation health checks, not a wall-clock task-lifetime timeout.
The checks SHALL distinguish first substantive output, inter-delta inactivity, and content-free keepalives.
A child stall SHALL return an explicit failed terminal result through the background owner contract.
An authorized human approval wait SHALL remain independent of operation inactivity and remain cancellable.
Healthy task duration alone SHALL NOT cause terminal timeout.

#### Scenario: Subagent times out

- **GIVEN** a child provider operation has an applicable inactivity deadline
- **WHEN** the operation exceeds that deadline without the required progress
- **THEN** the child returns a failed terminal result with the corresponding timeout reason
- **AND** the owner records it and the child stops after receipt acknowledgement

#### Scenario: LLM call failure returns failure result

- **GIVEN** a child model call throws an exception
- **WHEN** the child processes that failure
- **THEN** it returns an explicit failed result
- **AND** background delivery preserves the failure rather than success

#### Scenario: Healthy child outlives one operation deadline

- **GIVEN** a child repeatedly completes healthy model and tool operations
- **WHEN** total task duration exceeds any individual operation deadline
- **THEN** task age alone does not stop the child
- **AND** individual stalled operations still trigger their health checks

#### Scenario: A live approval wait does not become a stall

- **GIVEN** a child waits on its run-owned approval prompt
- **WHEN** operation inactivity would otherwise expire
- **THEN** the legitimate approval wait remains open
- **AND** explicit run cancellation can still settle it

### Requirement: Configurable subagent timeouts

The system SHALL use resolved subagent profile values for operation-health deadlines.
`SubAgentProfile.TimeoutSeconds` SHALL control inactivity between model deltas after substantive output starts.
`SubAgentProfile.PrefillTimeoutSeconds` SHALL override the shared wait for initial output when present.
When absent, it SHALL inherit `SubAgentConfig.PrefillTimeoutSeconds` from the `SubAgents` section of `netclaw.json`.
`SubAgentConfig.NoProgressTimeoutSeconds` SHALL bound time without substantive model output.
Content-free keepalives SHALL NOT refresh that no-progress deadline.

These values SHALL NOT define a wall-clock task-lifetime limit.
The file definition loader SHALL reject profile `timeoutSeconds` outside 5–600 seconds.
It SHALL reject profile `prefillTimeoutSeconds` outside 5–3600 seconds.
This change SHALL NOT migrate configuration fields or alter their current defaults.

#### Scenario: Custom timeout from configuration

- **GIVEN** a valid agent profile has `timeoutSeconds: 300`
- **WHEN** the spawner prepares that profile for the actual child actor
- **THEN** the child uses 300 seconds for model inactivity after substantive output starts
- **AND** healthy operations can continue beyond 300 seconds of total task duration

#### Scenario: Missing config section uses defaults

- **GIVEN** configuration omits the `SubAgents` section and the profile omits timeout overrides
- **WHEN** the runtime resolves the child operation-health values
- **THEN** it uses a 60-second profile inactivity value and a 1800-second initial-output value
- **AND** its no-progress value is 1200 seconds
- **AND** none of those values defines the child's total task duration

#### Scenario: Invalid timeout rejected by doctor

- **GIVEN** `netclaw.json` contains `"SubAgents": { "PrefillTimeoutSeconds": -1 }`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor reports a validation error for the timeout value

#### Scenario: Invalid profile timeout is rejected before execution

- **GIVEN** a file agent definition has `timeoutSeconds: -1`
- **WHEN** the definition loader validates that profile
- **THEN** it rejects that definition before a child can execute
- **AND** it does not replace the invalid value with a default

### Requirement: Subagent observability events

`LlmSessionActor` SHALL emit structured `SubAgentOutput` events to session subscribers for accepted child runs.
It SHALL emit `Started` only after the durable `Started` record commits.
It SHALL emit `Completed` only after the durable terminal receipt commits.
These events SHALL retain the `OutputFilter.ToolCalls` category and their existing payload fields.
Start-tool acceptance SHALL NOT substitute for the child terminal event.

The owner SHALL NOT require the completed start tool's activity callback to deliver later child events.
Existing headless CLI and channel render rules SHALL remain unchanged.

#### Scenario: Subagent start event emitted

- **GIVEN** an accepted child start within the session owner
- **WHEN** the owner commits `Started`
- **THEN** it emits one `SubAgentOutput` with `Phase = Started`
- **AND** that event includes the agent name and tool count
- **AND** subscribers receive it under `OutputFilter.ToolCalls`

#### Scenario: Subagent completion event emitted

- **GIVEN** a child finishes after its start tool returns acceptance
- **WHEN** the owner commits its terminal receipt
- **THEN** it emits one `SubAgentOutput` with `Phase = Completed`
- **AND** that event includes the existing success status, duration, and outcome fields

#### Scenario: Headless CLI renders subagent events

- **GIVEN** the headless CLI subscribes with `OutputFilter.Full`
- **WHEN** a subagent starts and completes
- **THEN** the CLI renders `[subagent:start] <name> (<N> tools)`
- **AND** it renders `[subagent:done] <name> (<status>, <duration>)`

#### Scenario: Slack adapter suppresses subagent events

- **GIVEN** the Slack adapter subscribes to session output
- **WHEN** a subagent starts and completes
- **THEN** no subagent-specific messages are posted to Slack

### Requirement: Subagent model role convention

Subagents SHALL use `ModelRole.Compaction` by default. This routes to the
configured compaction model (cheaper/faster) rather than the main model. The
`SubAgentDefinition.ModelRole` property SHALL allow override per-definition.

#### Scenario: Subagent uses compaction model

- **GIVEN** `Models.Compaction` is configured in `netclaw.json`
- **WHEN** a subagent is spawned with default `ModelRole`
- **THEN** the subagent uses the compaction model

#### Scenario: Compaction model falls back to main

- **GIVEN** `Models.Compaction` is not configured
- **WHEN** a subagent is spawned
- **THEN** the subagent uses the main model as fallback

### Requirement: Context layer subagent awareness

Subagent discovery and `spawn_agent` exposure SHALL honor the same effective
audience and feature gates as the rest of the session surface. Public sessions
and deployments with `SubAgents.Enabled = false` SHALL not be able to discover
or spawn subagents through prompt layers or tool calls.

#### Scenario: Public session receives no spawn_agent surface

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** the session prompt and tool definitions are built
- **THEN** subagent discovery is absent
- **AND** `spawn_agent` is absent or denied

#### Scenario: Runtime-disabled subagents unavailable to Team

- **GIVEN** `SubAgents.Enabled` is `false` in config
- **WHEN** a Team session starts
- **THEN** subagent discovery is absent
- **AND** `spawn_agent` is absent or denied

#### Scenario: Public cannot recover hidden subagents through discovery text

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** context layers are assembled
- **THEN** no discovery text names hidden subagents or instructs the model to
  delegate through `spawn_agent`

### Requirement: Sub-agent spawn carries an explicit audience

A `RunSubAgent` spawn message SHALL carry the spawning session's audience as a
parsed `TrustAudience`. The sub-agent actor SHALL NOT default a missing
audience to `TrustAudience.Personal`; a sub-agent spawned from a live session
always has a parent audience, so an absent audience is a programming error and
SHALL fail loudly with an unsuccessful sub-agent result.

#### Scenario: Sub-agent inherits the parent session audience

- **GIVEN** a sub-agent spawned from a Public-audience session
- **WHEN** the sub-agent actor initializes its tool execution context
- **THEN** the context carries `TrustAudience.Public`
- **AND** the audience is not elevated to `Personal`

#### Scenario: Missing spawn audience fails loud

- **WHEN** a `RunSubAgent` message reaches the sub-agent actor without an
  audience
- **THEN** the actor returns an unsuccessful sub-agent result that names the
  missing audience problem
- **AND** no `Personal` audience is substituted

### Requirement: Sub-agent tool exposure inherits parent audience policy

Sub-agent runtime tool exposure SHALL be derived from the parent session's
effective audience/profile policy. Agent definition `tools` metadata MAY be
parsed for file-format compatibility, but SHALL NOT narrow or grant runtime tool
authorization. After audience/profile filtering, Netclaw SHALL apply the static
sub-agent denylist to prevent recursive delegation through `spawn_agent`.

#### Scenario: Definition tool metadata does not restrict runtime access

- **GIVEN** a sub-agent definition declares `tools: [web_fetch]`
- **AND** the parent session audience/profile exposes `file_read`
- **WHEN** the sub-agent is spawned and calls `file_read`
- **THEN** the call is authorized or denied only by the parent audience/profile
  policy and normal invocation checks
- **AND** the definition `tools` metadata does not deny the call

#### Scenario: Static sub-agent denylist still blocks recursive delegation

- **GIVEN** a parent session audience/profile exposes `spawn_agent`
- **WHEN** a sub-agent is spawned
- **THEN** `spawn_agent` is removed from the sub-agent's exposed tool surface
- **AND** the sub-agent cannot recursively delegate to another sub-agent

### Requirement: Sub-agent watchdog pauses during human approval

The sub-agent inactivity watchdog SHALL treat parent approval waits as intentional suspension. While one or more approval waits are active, watchdog timeout ticks SHALL NOT complete the sub-agent as inactive. When the last approval wait settles, the watchdog SHALL be re-baselined so future inactivity is still bounded.

#### Scenario: Slow approval does not trigger inactivity timeout
- **GIVEN** a sub-agent with an active approval wait
- **AND** the human approval decision takes longer than the sub-agent inactivity budget
- **WHEN** the approval eventually arrives
- **THEN** the sub-agent applies the approval outcome
- **AND** the sub-agent is not failed for inactivity during the wait

#### Scenario: Parent start completes before child approval
- **GIVEN** the parent start call returns durable child acceptance
- **AND** the child waits for human approval
- **WHEN** the parent performs independent authorized work
- **THEN** the child approval wait remains open and cancellable
- **AND** the completed parent start call has no watchdog dependency on that wait

#### Scenario: Parallel approval waits keep watchdog paused until all settle
- **GIVEN** a sub-agent tool batch with two approval-gated calls
- **WHEN** both calls are waiting for parent approval
- **THEN** the watchdog remains paused until both approval waits have settled
- **AND** the watchdog is re-armed only after the final wait completes

### Requirement: Sub-agent approval outcomes settle exactly once

Each sub-agent approval-gated tool call SHALL settle exactly once as approved, denied, timed out, or cancelled. Approved decisions SHALL retry only the blocked call with retry-local approval state. Denied and timed-out decisions SHALL become tool-result messages visible to the sub-agent LLM. Cancellation and actor termination SHALL not produce duplicate `SubAgentResult` messages.

#### Scenario: Approve once is retry-local
- **GIVEN** a sub-agent approval-gated tool call is approved once
- **WHEN** the sub-agent retries the blocked call
- **THEN** the retry-local approval applies only to that tool call
- **AND** sibling calls, later tool iterations, and later sub-agent runs still require approval when policy requires it

#### Scenario: Denied approval becomes tool result
- **GIVEN** a sub-agent approval-gated tool call is denied by the user
- **WHEN** the approval decision is delivered
- **THEN** the tool is not executed
- **AND** the sub-agent receives a tool-result message explaining that approval was denied
- **AND** the sub-agent may continue or finish within the normal tool-iteration limit

#### Scenario: Timed-out approval becomes tool result
- **GIVEN** a sub-agent approval-gated tool call receives an expired or timed-out approval decision
- **WHEN** the decision is delivered to the sub-agent
- **THEN** the tool is not executed
- **AND** the sub-agent receives a tool-result message explaining that approval timed out

#### Scenario: Terminal races complete once
- **GIVEN** a sub-agent has an in-flight approval wait
- **WHEN** cancellation, timeout, and approval completion messages race
- **THEN** the sub-agent sends at most one `SubAgentResult` to the caller
- **AND** the first terminal path wins

### Requirement: Subagent deployment playbook inheritance

Every sub-agent SHALL receive the operating-rules composition for its launch audience: the audience-appropriate embedded operating core followed by the operator-authored deployment `AGENTS.md`. It SHALL NOT inherit `SOUL.md` or `TOOLING.md`. Project-local instructions remain separately scoped to the parent's working directory. Runtime audience, ACL, approval, and tool-policy boundaries SHALL remain unchanged by prompt guidance.

#### Scenario: Personal or Team subagent inherits full core and playbook

- **GIVEN** a Personal or Team parent launches a sub-agent and a deployment playbook exists
- **WHEN** the sub-agent system prompt is assembled
- **THEN** the full embedded operating core appears before the deployment playbook
- **AND** neither `SOUL.md` nor `TOOLING.md` is included

#### Scenario: Public subagent inherits stripped core and playbook

- **GIVEN** a Public parent launches a sub-agent and a deployment playbook exists
- **WHEN** the sub-agent system prompt is assembled
- **THEN** the stripped embedded Public operating core appears before the deployment playbook
- **AND** the same deployment playbook used by other audiences is included

#### Scenario: Subagent prompt layer order remains canonical

- **GIVEN** operating rules, deployment playbook, project instructions, and a sub-agent role prompt are available
- **WHEN** the sub-agent prompt is assembled
- **THEN** their order is embedded core, deployment playbook, project instructions, sub-agent role, then headless execution contract

### Requirement: Subagents maintain run-scoped working context
Each subagent SHALL own an ephemeral working context initialized by forking a read-only snapshot of the parent session's project directory, recent files, and immutable admitted-turn authority. The child SHALL own fresh call-local activity tracking and SHALL evolve its working state independently. The initial snapshot SHALL be included in the runtime-context portion of the child user message and SHALL NOT modify the reusable subagent system prompt. Child activity SHALL NOT mutate parent session state during execution.

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

### Requirement: Subagent completion returns structured working context
`SubAgentResult` SHALL carry a typed child outcome and structured working-context delta containing project/worktree identity, files read, confirmed files changed through recognized first-party file tools, files observed changed between bounded Git snapshots, and final branch and HEAD when available. Observed worktree changes SHALL NOT be represented as exclusively authored by the child. Failed or cancelled outcomes SHALL carry no mergeable delta.

#### Scenario: First-party edit is confirmed
- **GIVEN** a child changes a file through a recognized first-party file tool
- **WHEN** the child completes successfully
- **THEN** the canonical path appears in confirmed changed files

#### Scenario: Shell-generated file is observed
- **GIVEN** a child invokes a shell command that changes a Git worktree file without first-party file-tool provenance
- **WHEN** final Git state differs from the spawn snapshot
- **THEN** the file appears in observed changed files
- **AND** is not claimed as a confirmed child-authored file

#### Scenario: Parent merges only confirmed successful activity
- **GIVEN** a child completes successfully with confirmed and observed file metadata
- **WHEN** the parent handles the structured result
- **THEN** confirmed files are merged into the parent's durable recent-file context
- **AND** observed-only files are not silently merged or attributed

#### Scenario: Failed child does not merge partial activity
- **GIVEN** a child fails or is cancelled after touching files
- **WHEN** the parent handles the failure result
- **THEN** the outcome contains no mergeable working-context delta
- **AND** no child file metadata is merged into parent durable working context

### Requirement: Managed server-feed sub-agent discovery

The system SHALL load sub-agent definitions from user-authored top-level files under `~/.netclaw/agents/*.md` and from managed server-feed files under `~/.netclaw/agents/.server-feeds/<feed-name>/*.md`. User-authored top-level sub-agents SHALL take precedence over managed server-feed sub-agents with the same logical name. Shadowed managed sub-agents SHALL NOT be exposed through sub-agent discovery, `spawn_agent`, or routed skill execution.

#### Scenario: Managed sub-agent is loaded when no local conflict exists

- **GIVEN** `~/.netclaw/agents/.server-feeds/team/code-reviewer.md` declares `name: code-reviewer`
- **AND** no top-level local sub-agent declares `name: code-reviewer`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** `code-reviewer` is registered as an available sub-agent according to its frontmatter visibility

#### Scenario: Local sub-agent shadows managed sub-agent

- **GIVEN** `~/.netclaw/agents/code-reviewer.md` declares `name: code-reviewer`
- **AND** `~/.netclaw/agents/.server-feeds/team/code-reviewer.md` also declares `name: code-reviewer`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** the top-level local `code-reviewer` definition is registered
- **AND** the managed feed `code-reviewer` definition is skipped
- **AND** NetClaw emits a diagnostic identifying the shadowed managed definition

#### Scenario: Shadowed managed sub-agent cannot be spawned by routed skill

- **GIVEN** a top-level local sub-agent shadows a managed server-feed sub-agent with the same name
- **WHEN** a skill routes execution through `metadata.subagent` using that name
- **THEN** routed execution resolves to the registered local sub-agent definition
- **AND** the shadowed managed definition is not used

#### Scenario: Managed feed conflicts are deterministic

- **GIVEN** two configured server feeds both provide a managed sub-agent named `reviewer`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** NetClaw registers only one `reviewer` definition using deterministic configured feed order
- **AND** skips later managed duplicates with diagnostics

#### Scenario: Managed file changes refresh the registry

- **GIVEN** a managed sub-agent file changes under `~/.netclaw/agents/.server-feeds/team/`
- **WHEN** the sub-agent loader checks for changes
- **THEN** the loader detects the managed file change
- **AND** refreshes the sub-agent registry snapshot

#### Scenario: Missing managed namespace does not block local loading

- **GIVEN** `~/.netclaw/agents/.server-feeds/` does not exist
- **AND** top-level local sub-agent files exist under `~/.netclaw/agents/`
- **WHEN** the sub-agent loader refreshes definitions
- **THEN** local sub-agent loading continues without requiring the managed namespace to exist

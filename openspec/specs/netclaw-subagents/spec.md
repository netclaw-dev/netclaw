# netclaw-subagents Specification

## Purpose

Define subagent execution contract, timeout enforcement, observability events,
model role conventions, and context layer awareness for ephemeral autonomous
LLM actors.
## Requirements
### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that
execute an autonomous LLM tool loop and return a single text result plus an
optional structured findings envelope. A subagent SHALL stop itself after
completing its task. Subagents SHALL NOT persist durable memory, stream direct
durable-memory writes, or participate in session pub/sub by default.

For subagent execution launched from skill metadata routing, the subagent SHALL
remain an isolated worker by default:

- It SHALL NOT inherit the main session identity prompt stack unless explicitly
  enabled by a future opt-in setting.
- It SHALL NOT auto-load repo-local `AGENTS.md` unless explicitly enabled by a
  future opt-in setting.
- It SHALL inherit audience/boundary context from the launching invocation.

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes its LLM/tool loop and returns a `SubAgentResult`
- **AND** the result MAY include structured findings for the parent session to
  review
- **AND** stops itself

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** the LLM returns `FunctionCallContent` tool calls
- **WHEN** the subagent processes the response
- **THEN** it executes the tool calls via `DispatchingToolExecutor`
- **AND** sends tool results back to the LLM
- **AND** continues until the LLM returns a text response

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** the subagent has executed 10 tool iterations
- **WHEN** the LLM returns another tool call
- **THEN** the subagent forces a final LLM call with tools omitted
- **AND** returns the resulting text response

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

The system SHALL enforce a wall-clock timeout on subagent execution. When the
timeout fires, the subagent SHALL return a failure result and stop itself.

#### Scenario: Subagent times out

- **GIVEN** a `RunSubAgent` message with a `Timeout` of 30 seconds
- **WHEN** 30 seconds elapse without completion
- **THEN** the subagent returns `SubAgentResult` with `Success = false`
- **AND** the output contains "timed out"
- **AND** the subagent stops itself

#### Scenario: LLM call failure returns failure result

- **GIVEN** the LLM throws an exception during a subagent call
- **WHEN** the subagent processes the error
- **THEN** it returns `SubAgentResult` with `Success = false`
- **AND** the output contains the error message
- **AND** the subagent stops itself

### Requirement: Configurable subagent timeouts

The system SHALL read subagent timeout values from the `SubAgents` section of
`netclaw.json`. When the section is absent, the system SHALL use built-in
defaults that match the current hardcoded values (180s for store, 30s for
search, 60s general default). Timeout values MUST be positive integers
between 5 and 600 seconds.

#### Scenario: Custom timeout from configuration

- **GIVEN** `netclaw.json` contains `"SubAgents": { "StoreMemoryTimeoutSeconds": 300 }`
- **WHEN** the `store_memory` tool spawns a subagent
- **THEN** the subagent uses a 300-second timeout

#### Scenario: Missing config section uses defaults

- **GIVEN** `netclaw.json` does not contain a `SubAgents` section
- **WHEN** the `store_memory` tool spawns a subagent
- **THEN** the subagent uses the default 180-second timeout

#### Scenario: Invalid timeout rejected by doctor

- **GIVEN** `netclaw.json` contains `"SubAgents": { "DefaultTimeoutSeconds": -1 }`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor reports a validation error for the timeout value

### Requirement: Subagent observability events

The system SHALL emit structured `SubAgentOutput` events to session subscribers
when a subagent starts and completes. These events SHALL be filtered under the
`OutputFilter.ToolCalls` category. Tools that spawn subagents SHALL notify the
session via `ToolExecutionContext.OnSubAgentActivity`.

#### Scenario: Subagent start event emitted

- **GIVEN** a tool spawns a subagent within a session's tool execution pipeline
- **WHEN** the subagent begins execution
- **THEN** a `SubAgentOutput` event with `Phase = Started` is emitted
- **AND** the event includes the agent name and tool count
- **AND** the event is delivered to subscribers with `ToolCalls` in their filter

#### Scenario: Subagent completion event emitted

- **GIVEN** a subagent completes (success or failure)
- **WHEN** the result is received by the calling tool
- **THEN** a `SubAgentOutput` event with `Phase = Completed` is emitted
- **AND** the event includes success status and duration

#### Scenario: Headless CLI renders subagent events

- **GIVEN** the headless CLI subscribes with `OutputFilter.Full`
- **WHEN** a subagent starts and completes
- **THEN** the CLI renders `[subagent:start] <name> (<N> tools)`
- **AND** renders `[subagent:done] <name> (<status>, <duration>)`

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

### Requirement: Sub-agent approval lifecycle is actor-local

Sub-agent approval waits SHALL be owned by the live `SubAgentActor` run that encountered the approval-gated tool call. The sub-agent SHALL NOT persist approval wait state or reuse the session approval recovery/redrive lifecycle from `LlmSessionActor`.

#### Scenario: Approval wait belongs to live child actor
- **GIVEN** a sub-agent tool call requires approval
- **WHEN** the sub-agent enters an approval wait
- **THEN** the wait is tracked by the live `SubAgentActor`
- **AND** no sub-agent approval wait state is written to the session journal

#### Scenario: Parent stop cancels sub-agent approval wait
- **GIVEN** a sub-agent is waiting for parent approval
- **WHEN** the parent session stops or cancels the `spawn_agent` tool call
- **THEN** the sub-agent approval wait is cancelled
- **AND** the sub-agent completes at most once with a failed `SubAgentResult`
- **AND** the gated tool is not executed after cancellation

#### Scenario: Parent session recovery expires live-only prompt
- **GIVEN** a sub-agent is waiting for parent approval
- **WHEN** the parent session cold-recovers before the user responds
- **THEN** the sub-agent approval prompt has no durable redrive state
- **AND** a later approval response is rejected as expired

### Requirement: Sub-agent approval uses parent turn authority

Sub-agent approval prompts SHALL use the parent session turn's execution authority context for approval requester, principal, audience, boundary, channel capability, provenance, adopted-context safety, and filesystem grounding. The implementation SHALL reuse the `TurnContext` or shared execution-authority subset from #1213 when available, and SHALL keep any interim field mapping isolated to the parent-to-child spawn boundary.

#### Scenario: Approval prompt carries parent requester context
- **GIVEN** a sub-agent spawned from a parent turn with a requester sender id and principal
- **WHEN** the sub-agent emits an approval prompt
- **THEN** the prompt carries the parent requester sender id and principal
- **AND** approval authorization is evaluated as if the parent turn had requested the tool

#### Scenario: Missing authority fails closed
- **GIVEN** a sub-agent approval-gated tool call has no parent approval bridge or required authority context
- **WHEN** approval is required
- **THEN** the gated tool is not executed
- **AND** the sub-agent completes with a failed `SubAgentResult`
- **AND** no default `Personal` audience or synthetic requester is substituted

#### Scenario: Human approval requires requester binding
- **GIVEN** a sub-agent approval-gated tool call has a parent approval bridge
- **AND** the parent turn is not verified automation
- **AND** the parent turn has no requester sender identity or no requester principal
- **WHEN** approval is required
- **THEN** no approval prompt is emitted
- **AND** the sub-agent completes with a failed `SubAgentResult`

### Requirement: Sub-agent watchdog pauses during human approval

The sub-agent inactivity watchdog SHALL treat parent approval waits as intentional suspension. While one or more approval waits are active, watchdog timeout ticks SHALL NOT complete the sub-agent as inactive. When the last approval wait settles, the watchdog SHALL be re-baselined so future inactivity is still bounded.

#### Scenario: Slow approval does not trigger inactivity timeout
- **GIVEN** a sub-agent with an active approval wait
- **AND** the human approval decision takes longer than the sub-agent inactivity budget
- **WHEN** the approval eventually arrives
- **THEN** the sub-agent applies the approval outcome
- **AND** the sub-agent is not failed for inactivity during the wait

#### Scenario: Parent spawn-agent watchdog pauses during approval
- **GIVEN** a parent session is consuming a streaming `spawn_agent` tool call
- **AND** the child sub-agent is waiting for human approval longer than the parent tool inactivity budget
- **WHEN** the approval wait is still active
- **THEN** the parent `spawn_agent` tool call is not timed out for inactivity
- **AND** the parent tool watchdog resumes after the approval wait settles

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

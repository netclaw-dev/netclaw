## ADDED Requirements

### Requirement: Shell policy uses one explicit evaluation state

The system SHALL use one call-local shell policy state for projected candidates, coverage, grant evidence, trace facts, and terminal status.

The state SHALL preserve candidate identity and order for the complete authorization call. No state instance SHALL cross an actor, persistence, or session boundary.

#### Scenario: Partial coverage composes in one state

- **WHEN** a call has candidates covered by a session grant, reviewed-safe policy, and one-time authority
- **THEN** one evaluation state SHALL record each distinct coverage source
- **AND** the final call SHALL allow only after every candidate has coverage

#### Scenario: Candidate identity cannot change

- **WHEN** any stage returns a candidate ID or fact that differs from projection
- **THEN** policy SHALL deny with `internal_policy_failure`
- **AND** no later stage SHALL apply authority

#### Scenario: Allowed analysis reaches execution

- **WHEN** shell policy allows a stream or non-stream execution
- **THEN** the executor SHALL receive the exact analysis that policy authorized
- **AND** no analysis SHALL pass through a context cache

#### Scenario: Preflight allows without completion stages

- **WHEN** preflight allows a parsed shell call through Auto mode or another terminal rule
- **THEN** its terminal result SHALL carry the exact authorized analysis
- **AND** execution SHALL not parse the command again

#### Scenario: Authorization does not execute

- **WHEN** a caller requests authorization without execution
- **THEN** policy SHALL return the current decision
- **AND** policy SHALL retain no analysis for a later call

### Requirement: Shell policy stages have one fixed order

The system SHALL execute synchronous preflight and asynchronous completion stages in the documented order. A terminal stage result SHALL stop all later policy stages.

#### Scenario: Protected path precedes grant and safe policy

- **WHEN** a candidate references a protected real or fallback path
- **THEN** policy SHALL deny before actor grant coverage or reviewed-safe coverage applies

#### Scenario: Persistent-store failure follows available authority

- **WHEN** one-time or session authority covers every candidate and persistent state is unavailable
- **THEN** policy SHALL preserve the current allow result
- **AND** policy SHALL deny when an uncovered candidate still depends on persistent state

#### Scenario: Invalid stage result fails closed

- **WHEN** a stage returns an unknown result, invalid enum, or impossible transition
- **THEN** policy SHALL deny with `internal_policy_failure`
- **AND** policy SHALL not open an approval prompt

### Requirement: Actor grant evidence has one validation boundary

The system SHALL validate the complete actor result before any actor grant enters candidate coverage.

Validation SHALL cover store status, candidate count, IDs, facts, grant source, scope, timestamps, near misses, and unavailable-store restrictions.

#### Scenario: Valid mixed actor evidence

- **WHEN** the actor returns canonical session and persistent evidence for distinct candidates
- **THEN** policy SHALL apply each coverage source to its exact candidate
- **AND** policy SHALL retain the current approval match order

#### Scenario: Malformed actor evidence

- **WHEN** actor evidence has a duplicate ID, mismatched phrase, impossible scope, invalid enum, or inconsistent store state
- **THEN** policy SHALL deny with `internal_policy_failure`
- **AND** no actor grant SHALL enter coverage

### Requirement: Syntax facts and policy authority remain separate

ShellSyntaxTree and the shell matcher SHALL provide syntax, occurrence, value, redirect, directory, and candidate facts. They SHALL NOT decide trust, audience, grant authority, or prompt options.

Netclaw policy SHALL consume those facts without an executable-private command parser. Unknown policy-relevant facts SHALL retain their current strict outcome.

#### Scenario: Parser facts reach path policy once

- **WHEN** projection contains exact or finite filesystem facts
- **THEN** policy SHALL evaluate each fact through the current path rules
- **AND** later stages SHALL reuse the projected result without command-text scans

#### Scenario: Executable-private argument remains outside policy

- **WHEN** safety would require private grammar for one executable
- **THEN** Netclaw SHALL retain the current prompt or deny outcome
- **AND** production policy SHALL not add a command-name branch

### Requirement: Prompt and one-time authority share one candidate context

The system SHALL derive one prompt context from the current uncovered candidate set. Exact one-time authority and user prompts SHALL use that same context.

#### Scenario: Safe and granted candidates leave the prompt

- **WHEN** some candidates already have reviewed-safe or stored-grant coverage
- **THEN** the one-time key and prompt SHALL contain only uncovered candidates
- **AND** candidate order SHALL remain stable

#### Scenario: Causal policy retains full context

- **WHEN** causal intent requires prerequisite and consumer candidates as one unit
- **THEN** the one-time key and prompt SHALL retain the complete causal context

### Requirement: Coverage and trace facts remain atomic

Each coverage change SHALL add its bounded trace fact through the same state operation. Terminal completion SHALL add exactly one completion row.

#### Scenario: Coverage trace parity

- **WHEN** a candidate gains session, persistent, reviewed-safe, or one-time coverage
- **THEN** its trace row SHALL report the same coverage source and policy reason

#### Scenario: Trace data remains redacted

- **WHEN** any stage emits trace evidence
- **THEN** trace data SHALL exclude raw commands, arguments, paths, prompts, session values, and secrets

### Requirement: Refactor preserves observable policy behavior

The refactor SHALL preserve all current decisions, deny reasons, allow reasons, corrections, approval options, candidate order, actor request count, grant matches, and trace rows.

#### Scenario: Exact fixture equivalence

- **WHEN** the D-case, adversarial, and live regression fixtures execute after each slice
- **THEN** every expected outcome and ordered trace SHALL remain unchanged

#### Scenario: Full matrix equivalence

- **WHEN** the complete Bash, PowerShell 7, and Windows PowerShell 5.1 policy matrix executes
- **THEN** every current snapshot and expected result SHALL remain unchanged

#### Scenario: Channel and headless neutrality

- **WHEN** the same shell facts arrive from Slack, another interactive channel, a reminder, webhook, or subagent
- **THEN** current audience and interactive-capability rules SHALL remain unchanged

### Requirement: Refactor reduces policy complexity

The completed change SHALL reduce aggregate production lines and control-flow lines below the frozen baseline. It SHALL report method complexity and coverage risk.

It SHALL not add a public API, durable schema, command parser, or duplicate policy scan.

#### Scenario: Final complexity audit

- **WHEN** all refactor slices are complete
- **THEN** the task evidence SHALL report before and after production line and control-flow counts
- **AND** the after counts SHALL be lower than 5,136 lines and 373 control-flow lines
- **AND** the evidence SHALL report method complexity, coverage, and CRAP risk with a versioned command

#### Scenario: Public and durable compatibility

- **WHEN** the final API and persistence audits run
- **THEN** public APIs, approval entries, actor events, snapshots, session history, and configuration SHALL remain compatible

#### Scenario: Public compatibility service remains bounded

- **WHEN** a shell caller uses the public compatibility service
- **THEN** one internal adapter SHALL preserve exact candidate facts
- **AND** new typed policy code SHALL not call the aggregate compatibility methods directly

### Requirement: Internal failures remain fail-closed

The system SHALL map unexpected internal faults to `internal_policy_failure`. Caller cancellation SHALL still propagate without conversion to a policy result.

#### Scenario: Stage throws an internal exception

- **WHEN** a policy stage throws outside caller cancellation
- **THEN** the call SHALL deny with `internal_policy_failure`
- **AND** the failure SHALL create no approval authority

#### Scenario: Caller cancels evaluation

- **WHEN** the caller cancellation token is canceled
- **THEN** evaluation SHALL propagate cancellation
- **AND** policy SHALL not emit a false allow, prompt, or deny result

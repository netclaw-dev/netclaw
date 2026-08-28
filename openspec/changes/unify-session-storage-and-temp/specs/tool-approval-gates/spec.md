This delta uses terms from the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## RENAMED Requirements

- FROM: `Delegated scratch alignment is verified without prescribing the answer`
- TO: `Delegated managed-temp alignment is measured without prescribing the answer`
- FROM: `Platform temporary scope receives a session-scratch correction`
- TO: `Explicit unmanaged temporary writes receive a managed-temp correction`
- FROM: `Intentional platform-temp retry reaches ordinary approval`
- TO: `Intentional unmanaged-temp retry reaches ordinary approval`
- FROM: `Parent and subagent scratch corrections are equivalent`
- TO: `Parent and subagent managed-temp corrections are equivalent`

## MODIFIED Requirements

### Requirement: Delegated managed-temp alignment is measured without prescribing the answer

The headless eval suite SHALL include delegated disposable work in which the
parent request and child task do not name `session_dir`, `temp_dir`, a platform
temporary path, a working directory, or `set_working_directory`. The runtime
SHALL inject the child's managed temporary environment. The eval SHALL inspect
the child tool calls, generated paths, and completion rather than relying on
response prose.

This eval SHALL measure model alignment only. It SHALL NOT serve as proof that
the environment was injected, that same-session log scope blocks foreign logs,
that a managed path grants authority, or that a headless run exercised
interactive approval.

#### Scenario: Example - delegated work uses standard temp behavior

- **GIVEN** a Personal headless child receives its managed paths and temporary
  environment
- **AND** its task requests disposable diagnostic work without prescribing a
  path
- **WHEN** the child creates temporary output
- **THEN** the observed output path is below the child's `temp_dir`
- **AND** the child does not author an environment export prefix
- **AND** the child completes with the expected diagnostic result

#### Scenario: Counterexample - parent task cannot supply the temp answer

- **GIVEN** the delegated managed-temp eval
- **WHEN** the parent calls `spawn_agent`
- **THEN** the child task contains no managed path, platform temporary path,
  cwd instruction, environment-variable instruction, or project declaration
  instruction
- **AND** the eval fails if those hints appear

#### Scenario: Counterexample - guidance does not confer headless authority

- **GIVEN** a headless child knows its managed paths
- **WHEN** it authors a call that lacks existing noninteractive authority
- **THEN** ordinary headless policy denies the call
- **AND** path knowledge does not create reviewed-safe, one-time, session,
  folder, or persistent coverage

#### Scenario: Counterexample - explicit platform temp remains strict

- **GIVEN** a headless child task explicitly requires the platform temporary
  directory
- **WHEN** the child authors that exact path
- **THEN** Netclaw preserves the authored path
- **AND** existing noninteractive authorization decides the outcome
- **AND** the eval does not treat path preservation as an alignment failure

### Requirement: Explicit unmanaged temporary writes receive a managed-temp correction

After tool exposure, hard deny, protected-path, and shell-analysis checks, the
system SHALL return `UseManagedTemporaryDirectory` instead of immediately
requesting approval when a Personal interactive tool call explicitly authors
a write below the captured platform temporary root, the managed temporary
directory is a valid nonempty normalized path, and the ordinary result would
otherwise request approval. Team and Public calls SHALL retain their existing
earlier policy boundary and SHALL NOT receive the private path.

Initial eligible forms SHALL include a structured file write or edit, an exact
shell redirect, an explicit shell `WorkingDirectory`, and a complete Bash
leading directory transition. An inherited project, session, child, or default
cwd SHALL NOT establish authored intent. Matching SHALL use generic path and
shell-syntax facts. It SHALL NOT parse private executable option grammar.

The correction SHALL name the exact managed temporary directory and ask the
agent to author a replacement call. It SHALL execute nothing, record no grant,
change no working context, and SHALL NOT rewrite the original call. The
replacement SHALL pass every normal authorization stage.

`UseManagedTemporaryDirectory` SHALL replace the former `UseSessionScratch`
remediation code. Correction and retry state SHALL carry the run's exact
`temp_dir`; they SHALL NOT use `session_dir` as the replacement destination.
Model-facing correction text SHALL use “managed temporary directory” and
`temp_dir`. It SHALL NOT describe `session_dir` as session scratch.

For Bash causal-directory advice, the system SHALL use the canonical parser's
exact cwd-attribution and effective-directory facts. Native PowerShell causal
directory mutation SHALL remain ineligible until the canonical parser exposes
equivalent facts.

The system SHALL resolve the captured platform temporary root to its final
filesystem target. Every relevant path SHALL remain below that target without
a descendant symbolic link, junction, or reparse point. A resolution or
attribute failure SHALL suppress the correction. Hard deny and protected-path
results SHALL take precedence. One call SHALL return at most one correction.

#### Scenario: Example - explicit POSIX temp write receives correction

- **GIVEN** the captured platform temporary root is `/tmp`
- **AND** the run's managed temporary directory is
  `/srv/netclaw/sessions/example/tmp/parent`
- **WHEN** a complete shell call requests `WorkingDirectory=/tmp`
- **AND** its command contains the exact redirect `> result.log`
- **AND** ordinary policy would request approval
- **THEN** the agent receives `UseManagedTemporaryDirectory` before the user
  approval surface
- **AND** the correction names the managed temporary directory
- **AND** the original call is not executed or rewritten

#### Scenario: Counterexample - read-only explicit temp cwd gets no correction

- **GIVEN** the user asks the agent to run `pwd` from `/tmp`
- **WHEN** the agent authors `Command=pwd` with `WorkingDirectory=/tmp`
- **THEN** the system does not emit `UseManagedTemporaryDirectory`
- **AND** normal authorization preserves the requested directory behavior

#### Scenario: Example - structured file write receives correction

- **GIVEN** `file_write` or `file_edit` targets an exact path below the
  captured platform temporary root
- **WHEN** the call is otherwise eligible for interactive correction
- **THEN** the agent receives `UseManagedTemporaryDirectory`
- **AND** the correction names the run's exact managed temporary directory
- **AND** no partial file write occurs

#### Scenario: Example - exact shell redirect receives correction

- **GIVEN** a complete shell syntax tree proves an exact redirect target below
  the captured platform temporary root
- **WHEN** ordinary policy would request approval
- **THEN** the agent receives `UseManagedTemporaryDirectory`
- **AND** no executable-specific output-option rule is required

#### Scenario: Fresh managed temporary directory is prepared by execution

- **GIVEN** a fresh run has a valid normalized managed temporary path
- **AND** that directory has not yet been created
- **WHEN** an eligible call explicitly writes below the platform temporary root
- **THEN** the system emits `UseManagedTemporaryDirectory`
- **AND** replacement execution owns creation of the managed directory

#### Scenario: Static Bash causal directory change receives correction

- **GIVEN** the captured platform temporary root is `/tmp`
- **WHEN** the agent authors
  `cd /tmp && diagnostic-command > result.log && head result.log`
- **AND** every policy-relevant identity, redirect, and effective directory is
  complete and remains below `/tmp`
- **AND** ordinary policy would request approval
- **THEN** the correction asks the agent to author the operation below its
  managed temporary directory
- **AND** later execution still requires ordinary authority

#### Scenario: Windows matching uses captured host temp

- **GIVEN** the native Windows environment captured its actual platform
  temporary root before managed environment injection
- **WHEN** an eligible call explicitly authors that exact root or a safe
  descendant
- **THEN** the agent receives the same typed correction with its Windows
  managed temporary path
- **AND** the policy does not depend on `C:\Windows\Temp` or another fixed
  Windows value

#### Scenario: Counterexample - unresolved PowerShell cwd remains strict

- **WHEN** an agent authors `Set-Location $env:TEMP; diagnostic-command` or
  `cd $env:TEMP; diagnostic-command` in native PowerShell
- **THEN** the system does not emit the managed-temp correction
- **AND** normal approval or deny behavior remains

#### Scenario: Platform temp is never proposed as project scope

- **GIVEN** both project-scope and managed-temp corrections are otherwise
  eligible
- **WHEN** policy selects one correction for an explicitly authored platform
  temporary write
- **THEN** it returns only `UseManagedTemporaryDirectory`
- **AND** it does not recommend `set_working_directory` for the platform root

#### Scenario: Counterexample - inherited temp does not prove authored intent

- **GIVEN** a recovered parent or child inherits the platform temporary root
  as its cwd
- **WHEN** it submits a call without an explicit destination, working
  directory, or supported Bash leading transition
- **THEN** the system does not emit `UseManagedTemporaryDirectory`
- **AND** normal policy evaluates the inherited scope

#### Scenario: Counterexample - dynamic shell data remains strict

- **WHEN** command identity, control flow, cwd, or redirect destination is
  dynamic, incomplete, or unparseable
- **THEN** the system does not emit the managed-temp correction
- **AND** normal approval or deny behavior remains

#### Scenario: Counterexample - private executable syntax proves no write

- **GIVEN** an executable-specific option appears to name an output below the
  platform temporary root
- **WHEN** no structured tool contract or canonical shell fact proves that
  destination
- **THEN** the system does not infer a managed-temp correction from the option
- **AND** normal approval or deny behavior remains

#### Scenario: Counterexample - external authored path prevents correction

- **GIVEN** a call also authors an absolute path outside the platform
  temporary root
- **WHEN** policy evaluates the complete call
- **THEN** the system does not emit the managed-temp correction
- **AND** normal approval or deny behavior remains

#### Scenario: Counterexample - link escape prevents correction

- **GIVEN** a descendant of the platform temporary root is a symbolic link,
  junction, or reparse point outside that root
- **WHEN** an eligible form references that descendant
- **THEN** the system does not emit the managed-temp correction
- **AND** normal approval or deny behavior remains

#### Scenario: Path inspection failure prevents correction

- **WHEN** the system cannot resolve the platform root or inspect a relevant
  descendant
- **THEN** it does not emit the managed-temp correction

#### Scenario: Counterexample - hard deny retains precedence

- **GIVEN** a call explicitly writes below the platform temporary root
- **WHEN** the call triggers hard deny or protected-path policy
- **THEN** the system denies the call
- **AND** it does not emit the managed-temp correction

#### Scenario: Example - replacement receives full authorization

- **GIVEN** the agent receives `UseManagedTemporaryDirectory`
- **WHEN** it authors a replacement call under the named directory
- **THEN** the system evaluates the replacement as a new call through every
  normal authorization stage
- **AND** the correction does not guarantee execution

#### Scenario: Example - remediation names the new contract

- **GIVEN** an eligible unmanaged temporary write
- **WHEN** the dispatcher creates its recoverable-correction receipt
- **THEN** the remediation code is `UseManagedTemporaryDirectory`
- **AND** the trusted correction path is the current run's `temp_dir`
- **AND** neither the code nor presenter calls `session_dir` session scratch

#### Scenario: Counterexample - legacy persisted path is not reinterpreted

- **GIVEN** a recovered approval event contains legacy protobuf field 19
  `session_scratch_directory`
- **WHEN** the current runtime restores the approval
- **THEN** it does not treat that stored path as `temp_dir`
- **AND** it derives the current managed temporary directory from resolved run
  storage or omits managed-temp correction metadata
- **AND** the approval decision itself can still complete normally

#### Scenario: Counterexample - headless execution gets no interactive correction

- **GIVEN** a headless, scheduled, webhook, benchmark, or other noninteractive
  run
- **WHEN** a call explicitly requires the platform temporary root
- **THEN** the system does not emit the interactive correction
- **AND** it does not rewrite or remove the authored path
- **AND** existing noninteractive policy decides allow or deny

### Requirement: Intentional unmanaged-temp retry reaches ordinary approval

The system SHALL prevent correction loops with actor-owned, non-persistent
correction keys for the active user turn. A shell key SHALL cover canonical
shell, command text, explicit working-directory presence and value, resolved
temporary scope, background mode, and timeout. A structured-file key SHALL
cover tool name, canonical destination, and execution-relevant arguments. A
key SHALL exclude rationale because rationale does not alter execution.

The actor SHALL arm a key only after the correction result is committed to
model history. Identical calls in one parallel batch SHALL remain first
attempts. A later equivalent tool iteration SHALL consume one armed key,
suppress that correction once, and expose exactly `Once` and `Deny` when user
approval is the underlying result. The system SHALL NOT offer session, folder,
or global persistence for this retry and SHALL NOT write it to a grant store.

The actor SHALL clear keys on turn completion, cancellation, failure,
passivation, recovery, and before a new user turn. A consumed key SHALL NOT
suppress an unlimited sequence of retries.

#### Scenario: Example - equivalent retry requests one-time approval

- **GIVEN** the agent received `UseManagedTemporaryDirectory`
- **WHEN** it repeats an equivalent call during the active turn
- **THEN** the system does not repeat the same correction
- **AND** it requests user approval when that is the underlying policy result
- **AND** the approval choices are exactly `Once` and `Deny`
- **AND** approval executes the agent-authored call exactly
- **AND** no session or persistent grant is recorded

#### Scenario: Parallel duplicate first attempts all receive correction

- **GIVEN** one model batch contains two equivalent eligible calls
- **WHEN** the parent or child pipeline evaluates them concurrently
- **THEN** both calls receive first-attempt corrections
- **AND** neither reaches approval from the other's uncommitted result

#### Scenario: Later iteration consumes correction key once

- **GIVEN** a correction result is committed to model history
- **WHEN** a later tool iteration repeats the equivalent call
- **THEN** the actor consumes the armed key and exposes `Once` and `Deny`
- **AND** a later equivalent attempt has no residual execution or grant
  authority

#### Scenario: Counterexample - execution change starts a new evaluation

- **GIVEN** a correction key is armed
- **WHEN** a later call changes its tool, command, destination, working
  directory, background mode, timeout, or another execution-relevant argument
- **THEN** it does not consume that key
- **AND** it receives a complete first-attempt policy evaluation

#### Scenario: Rationale-only change remains equivalent

- **GIVEN** a correction key is armed for a call
- **WHEN** a later call changes only `_rationale`
- **THEN** rationale does not prevent equivalence
- **AND** the execution semantics receive the bounded retry behavior

#### Scenario: Counterexample - correction keys do not cross lifecycle boundaries

- **WHEN** a turn completes, cancels, fails, passivates, recovers, or a new
  user turn begins
- **THEN** every armed or consumed managed-temp correction key from the prior
  lifecycle is cleared

### Requirement: Parent and subagent managed-temp corrections are equivalent

The parent session pipeline and child pipeline SHALL consume the same typed
managed-temp correction before they invoke their respective user or parent
approval bridges.

#### Scenario: Example - parent receives correction before user prompt

- **WHEN** a parent agent submits an eligible unmanaged-temp call
- **THEN** it receives `UseManagedTemporaryDirectory` before a user approval
  prompt is created

#### Scenario: Example - child receives correction before parent bridge

- **WHEN** a child agent submits an eligible unmanaged-temp call
- **THEN** it receives the same correction before a parent approval request is
  created
- **AND** the parent user is not prompted for that first attempt

#### Scenario: Counterexample - child bridge cannot create eligibility

- **GIVEN** a child call is ineligible because of audience or path policy
- **WHEN** the child pipeline evaluates the call
- **THEN** the bridge does not create a managed-temp correction
- **AND** the existing denial or approval result remains

## ADDED Requirements

### Requirement: Product proof separates runtime contracts from model behavior

The change SHALL use deterministic tests as the acceptance boundary for path
layout, persistence, access control, environment injection, correction
selection, retry behavior, and worktree authority. Model evals SHALL measure
tool choice, managed-path use, correction recovery, and parent-child handoff.
A model-eval result SHALL NOT replace a failed or missing deterministic test.

#### Scenario: Counterexample - model success cannot hide contract failure

- **GIVEN** a model happens to choose the managed temporary path
- **WHEN** a deterministic environment or authority test fails
- **THEN** the change does not meet acceptance
- **AND** the model result is reported only as behavioral evidence

#### Scenario: Example - before-and-after eval uses one locked case

- **GIVEN** an eval is used for a before-and-after comparison
- **WHEN** both versions are evaluated
- **THEN** they use the same sanitized prompt, model configuration, and
  assertion logic
- **AND** the result identifies the binary version under test

#### Scenario: Example - parent disposable-file eval keeps first-party tools

- **GIVEN** a Personal parent must create and read one disposable file
- **WHEN** the managed-temp behavioral eval runs
- **THEN** the agent uses `file_write` and `file_read` below `temp_dir`
- **AND** it does not use the complete session envelope as disposable scratch
- **AND** it does not call the shell

#### Scenario: Counterexample - eval evidence cannot contain PII

- **WHEN** an eval fixture or published result is scanned
- **THEN** it contains no local username, private repository, channel, thread,
  host, email, token, or secret

#### Scenario: Counterexample - suite does not invent a Windows pattern

- **GIVEN** deterministic Windows contract tests cover the managed environment
- **WHEN** no representative sanitized Windows agent behavior is available
- **THEN** the suite does not invent a Windows model pattern
- **AND** the missing behavioral case is recorded as future evidence work

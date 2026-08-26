## ADDED Requirements

### Requirement: Platform temporary scope receives a session-scratch correction

After tool exposure, hard deny, protected-path, and shell-analysis checks, the system SHALL return a typed session-scratch correction instead of immediately requesting approval when a Personal shell call explicitly authors the platform temporary root through the typed `WorkingDirectory` or an eligible Bash leading directory transition, every policy-relevant occurrence remains within that root, the session directory is a valid nonempty normalized path, interactive approval is available, and the ordinary result would otherwise request approval. The session directory SHALL NOT need to exist before the correction because replacement execution owns its creation. An inherited project, session, subagent, or default cwd SHALL NOT establish this authored intent. The correction SHALL identify the exact session directory and ask the agent to author a replacement call there. It SHALL execute nothing, record no grant, change no working context, and SHALL NOT rewrite the original call. Team and Public shell calls SHALL retain their existing earlier denial boundary and SHALL NOT receive this correction or the private session path.

An internal immutable policy value SHALL capture the platform temporary root once when shell approval policy is constructed, using the resolved shell environment's path style. Matching SHALL use platform path rules and SHALL NOT use executable-specific parsing. This capability SHALL NOT add a public `ShellExecutionEnvironment` member.

For Bash causal-directory advice, the system SHALL consume ShellSyntaxTree's exact leading `IsCwdAttribution` and `CommandOccurrence.WorkingDirectory` facts directly. Because the correction grants and executes nothing, it SHALL NOT require the parent causal-approval intent's pre-existing grant coverage. Actual execution, safe-policy coverage, and folder-grant decisions SHALL retain those authority preconditions, and Netclaw SHALL NOT add a second `cd` parser. Native PowerShell causal directory mutation remains ineligible.

The system SHALL resolve the captured platform temporary root to its final filesystem target at startup. Every relevant cwd and authored filesystem path SHALL remain beneath that canonical target without a descendant symbolic link, junction, or reparse point. An attribute or target-resolution failure SHALL suppress the correction.

Platform-temp correction SHALL take precedence over undeclared-project correction. The undeclared-project correction SHALL treat the platform temporary root as ineligible, and one shell attempt SHALL return at most one correction.

#### Scenario: Explicit POSIX temporary working directory receives correction

- **GIVEN** the platform temporary root is `/tmp`
- **AND** the session directory is `/home/user/.netclaw/sessions/example`
- **WHEN** a complete shell call requests `WorkingDirectory=/tmp`
- **AND** ordinary policy would request user approval
- **THEN** the agent receives a `SessionScratchSuggested` correction before the user approval surface
- **AND** the correction names `/home/user/.netclaw/sessions/example`
- **AND** the original call is not executed or rewritten

#### Scenario: Fresh session scratch need not exist yet

- **GIVEN** a fresh session has a valid normalized session-directory path
- **AND** that directory has not yet been created on disk
- **WHEN** its first shell call explicitly authors the platform temporary root
- **AND** every other correction condition passes
- **THEN** the system emits `SessionScratchSuggested`
- **AND** replacement shell execution creates the session directory through the existing shell cwd path

#### Scenario: Static causal directory change receives correction

- **GIVEN** the platform temporary root is `/tmp`
- **WHEN** the agent authors `cd /tmp && diagnostic-command > result.log && head result.log`
- **AND** every policy-relevant effective directory remains `/tmp`
- **AND** ordinary policy would request approval
- **THEN** the correction asks the agent to author the temporary-artifact operation under the session directory
- **AND** no executable-specific rule for `diagnostic-command` or `head` is required
- **AND** the advice does not require prior grant coverage for `cd` or the first action
- **AND** later execution still requires ordinary authority

#### Scenario: Windows temporary working directory receives correction

- **GIVEN** the native Windows shell environment captured `C:\\Users\\user\\AppData\\Local\\Temp` as its platform temporary root
- **WHEN** a complete PowerShell call requests that exact working directory
- **AND** ordinary policy would request approval
- **THEN** the agent receives the same typed correction with its Windows session-directory path

#### Scenario: Native PowerShell causal directory remains strict

- **GIVEN** the native Windows shell is PowerShell
- **WHEN** the agent authors `Set-Location $env:TEMP; diagnostic-command` or `cd $env:TEMP; diagnostic-command`
- **THEN** the system does not emit the session-scratch correction
- **AND** normal approval or deny behavior remains

#### Scenario: Platform temp is never proposed as project scope

- **GIVEN** a reviewed-safe shell call whose exact cwd is the platform temporary root
- **AND** both scope-correction predicates would otherwise be eligible
- **WHEN** policy selects an agent correction
- **THEN** it returns only `SessionScratchSuggested`
- **AND** it does not recommend `set_working_directory` for the platform temporary root

#### Scenario: Dynamic temporary directory remains strict

- **WHEN** the parser cannot prove the effective directory for `cd "$TMPDIR" && diagnostic-command`
- **THEN** the system does not emit the session-scratch correction
- **AND** normal approval or deny behavior remains

#### Scenario: Dynamic identity remains on ordinary path

- **WHEN** an authored call is `cd /tmp && "$tool"`
- **OR** command substitution controls command identity or flow
- **THEN** the system does not emit the session-scratch correction
- **AND** normal approval or deny behavior remains

#### Scenario: Unresolved redirect remains on ordinary path

- **WHEN** an authored platform-temp call has a redirect target the parser cannot resolve
- **THEN** the system does not emit the session-scratch correction
- **AND** normal approval or deny behavior remains

#### Scenario: Complete prompt-worthy work may receive advice

- **GIVEN** every command identity, control-flow edge, cwd, and redirect is complete and static
- **AND** the call would prompt because it writes, mutates, or performs a network action
- **WHEN** its explicitly authored execution scope is the platform temporary root
- **THEN** it may receive the advice-only session-scratch correction
- **AND** a replacement or intentional retry still receives complete ordinary authorization

#### Scenario: Mixed incomplete batch remains on ordinary path

- **GIVEN** one candidate is reviewed safe
- **AND** another candidate has incomplete identity, control flow, cwd, or redirect facts
- **WHEN** policy evaluates the batch
- **THEN** it does not emit the session-scratch correction
- **AND** the incomplete candidate cannot hide behind the reviewed-safe candidate

#### Scenario: Public parent and subagent retain path redaction

- **GIVEN** a Public parent agent or subagent
- **WHEN** it submits a platform-temp shell call
- **THEN** the system does not emit `SessionScratchSuggested`
- **AND** it does not disclose the private session-directory path
- **AND** normal Public policy remains

#### Scenario: Inherited temp project does not imply authored intent

- **GIVEN** a recovered parent session has `ProjectDirectory=/tmp`
- **OR** a subagent inherits `/tmp` as its cwd
- **WHEN** it submits a shell call without an explicit `WorkingDirectory` or Bash leading transition
- **THEN** the system does not emit `SessionScratchSuggested`
- **AND** normal policy evaluates the inherited scope

#### Scenario: External authored path prevents correction

- **GIVEN** a call runs under the platform temporary root
- **AND** an authored absolute path resolves outside that root
- **WHEN** policy evaluates the call
- **THEN** the system does not emit the session-scratch correction
- **AND** normal approval or deny behavior remains

#### Scenario: POSIX symlink escape prevents correction

- **GIVEN** `/tmp/outside` is a symbolic link to `/etc`
- **WHEN** a platform-temp call references `/tmp/outside/passwd` or redirects to `/tmp/outside/result`
- **THEN** the system does not emit the session-scratch correction
- **AND** normal approval or deny behavior remains

#### Scenario: Windows reparse escape prevents correction

- **GIVEN** a descendant of the Windows temporary root is a junction or reparse point outside that root
- **WHEN** a platform-temp call references that descendant
- **THEN** the system does not emit the session-scratch correction

#### Scenario: Link inspection failure prevents correction

- **WHEN** the system cannot resolve the platform temporary root or inspect a relevant descendant's attributes
- **THEN** it does not emit the session-scratch correction

#### Scenario: Hard deny and protected path retain precedence

- **GIVEN** a shell call runs under the platform temporary root
- **WHEN** the call triggers hard deny or protected-path policy
- **THEN** the system denies the call
- **AND** it does not emit the session-scratch correction

#### Scenario: Replacement receives full authorization

- **GIVEN** the agent receives a session-scratch correction
- **WHEN** it authors a replacement call under the named session directory
- **THEN** the system evaluates the replacement as a new call through every normal authorization stage
- **AND** the correction does not guarantee automatic execution

#### Scenario: Headless execution retains existing temporary-directory behavior

- **GIVEN** a headless, scheduled, webhook, benchmark, or other noninteractive run
- **WHEN** an authored shell call requires the platform temporary root or an absolute path beneath it
- **THEN** the system does not emit the session-scratch correction
- **AND** it does not rewrite or remove the authored temporary path
- **AND** the existing noninteractive authorization result remains unchanged

#### Scenario: Personal or Team headless model guidance prefers session scratch

- **GIVEN** a Personal or Team headless session announces its exact session directory as scratch
- **AND** a task requests disposable artifacts without requiring a platform path
- **WHEN** the agent authors its shell call
- **THEN** it uses the announced session directory rather than the platform temporary root
- **AND** no interactive correction or approval prompt is involved

#### Scenario: Public headless context retains path redaction

- **GIVEN** a Public headless session
- **WHEN** its working context is assembled
- **THEN** the exact private session path is not disclosed
- **AND** the platform-temp correction is not emitted

#### Scenario: Headless task with explicit temp requirement preserves intent

- **GIVEN** a headless or benchmark task explicitly requires the platform temporary directory
- **WHEN** the agent authors its shell call
- **THEN** it preserves the required platform-temp path
- **AND** Netclaw does not redirect, defer, or prompt
- **AND** existing noninteractive policy decides allow or deny

### Requirement: Intentional platform-temp retry reaches ordinary approval

The system SHALL prevent correction loops with actor-owned, non-persistent correction keys for the active user turn. A key SHALL cover canonical shell, command text, explicit working-directory presence and value, resolved temporary scope, background mode, and timeout. It SHALL deliberately exclude rationale because rationale does not alter execution. The actor SHALL arm a key only after the correction result is committed to model history. Identical calls in one parallel batch SHALL all remain first attempts. A later equivalent tool iteration SHALL atomically consume one armed key, suppress that correction once, and expose exactly `Once` and `Deny`. The system SHALL NOT offer session, folder, or global persistence for this retry and SHALL NOT write it to an actor grant or approval store.

The actor SHALL clear keys on turn completion, cancellation, failure, passivation or recovery, and before a new user turn. A consumed key SHALL NOT suppress an unlimited sequence of retries.

#### Scenario: Equivalent retry requests one-time approval

- **GIVEN** the agent received a session-scratch correction for a platform-temp call
- **WHEN** the agent repeats an equivalent call unchanged during the active turn
- **THEN** the system does not repeat the same correction
- **AND** it requests ordinary user approval when that is the underlying policy result
- **AND** the approval choices are exactly `Once` and `Deny`
- **AND** approval executes the agent-authored call exactly
- **AND** no session or persistent grant is recorded

#### Scenario: Parallel duplicate first attempts all receive correction

- **GIVEN** one model batch contains two equivalent eligible platform-temp calls
- **WHEN** the parent or subagent pipeline evaluates them concurrently
- **THEN** both calls receive first-attempt corrections
- **AND** neither call reaches approval based on the other call's uncommitted result

#### Scenario: Later iteration consumes correction key once

- **GIVEN** a correction result is committed to model history
- **WHEN** a later tool iteration repeats the equivalent call
- **THEN** the actor consumes the armed key and exposes `Once` and `Deny`
- **AND** a subsequent equivalent attempt has no residual execution or grant authority

#### Scenario: Execution-meta change is not an unchanged retry

- **GIVEN** a correction key was armed for a foreground call with one timeout
- **WHEN** a later call changes background mode, timeout, command text, or explicit cwd presence or value
- **THEN** it does not consume that correction key
- **AND** it receives a complete first-attempt policy evaluation

#### Scenario: Rationale-only change remains equivalent

- **GIVEN** a correction key was armed for a platform-temp call
- **WHEN** a later call changes only `_rationale`
- **THEN** rationale does not prevent equivalence
- **AND** the execution semantics still receive the bounded retry behavior

#### Scenario: Correction key does not persist

- **GIVEN** a platform-temp correction occurred in an earlier turn
- **WHEN** a later turn submits the same call
- **THEN** no persisted correction key grants authority or bypasses policy
- **AND** the call receives the current turn's complete policy evaluation

#### Scenario: Lifecycle boundaries clear correction keys

- **WHEN** a turn completes, cancels, fails, passivates, recovers, or a new user turn begins
- **THEN** every armed or consumed scratch-correction key from the prior lifecycle is cleared

### Requirement: Parent and subagent scratch corrections are equivalent

The parent session pipeline and subagent pipeline SHALL consume the same typed session-scratch correction before they invoke their respective user or parent approval bridges.

#### Scenario: Parent agent is corrected before user prompt

- **WHEN** a parent agent submits an eligible platform-temp call
- **THEN** it receives the correction before a user approval prompt is created

#### Scenario: Subagent is corrected before parent bridge

- **WHEN** a subagent submits an eligible platform-temp call
- **THEN** it receives the same correction before a parent approval request is created
- **AND** the parent user is not prompted for that first attempt

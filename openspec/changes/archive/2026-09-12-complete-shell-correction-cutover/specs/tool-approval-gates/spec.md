Terms: [engineering glossary](../../../../../docs/spec/GLOSSARY.md).
Source PRDs: PRD-002 and PRD-006.

## MODIFIED Requirements

### Requirement: Explicit unmanaged temporary writes receive a managed-temp correction

After tool exposure, hard deny, protected-path, and shell-analysis checks, the
system SHALL return `UseManagedTemporaryDirectory` instead of immediately
requesting approval when a Personal interactive tool call explicitly authors
a write below the captured platform temporary root, the managed temporary
directory is a valid nonempty normalized path, and the ordinary result would
otherwise request approval or allow shell execution through Auto.
Structured non-shell Auto behavior SHALL remain unchanged. Team and Public calls SHALL retain their existing
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
results SHALL take precedence. One call SHALL return one collection of compatible corrections.
Native file-write advice can accompany managed temporary advice.
Native file-read advice SHALL NOT relocate its existing input.

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
- **WHEN** an eligible call explicitly authors that exact root or a canonical
  descendant that crosses no filesystem link
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
- **AND** the correction destination is the current run's `temp_dir`
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


## ADDED Requirements

### Requirement: Shell corrections use one common decision

The shell coordinator SHALL collect applicable corrections from existing policy, registry, and invocation facts.
Callers SHALL deliver that decision without independent correction selection.
The coordinator SHALL apply hard denials before corrections and corrections before Auto allows execution.
Existing stored-grant and exact one-time approval precedence SHALL remain unchanged for temporary-only and project-only advice.
Project advice SHALL require a policy-visible declaration tool that accepts the exact directory.
Project advice SHALL NOT require an approval bridge when declaration remains available.
Collection SHALL cause no prompt, process, grant, store, journal, or retry-state effect.
Actors SHALL retain their existing state commit, recovery, and transport duties.

#### Scenario: Project correction uses the common result
- **GIVEN** a reviewed-safe command requires a permitted project declaration
- **WHEN** a parent or child submits the command
- **THEN** the common result contains project advice and no prompt or process starts

#### Scenario: Unavailable declaration preserves approval
- **GIVEN** the declaration tool is absent, hidden, or rejects the directory
- **WHEN** a shell call needs approval
- **THEN** no project correction applies and the existing approval boundary remains

#### Scenario: Auto still receives corrections
- **GIVEN** shell Auto and an applicable correction
- **WHEN** the coordinator evaluates the call
- **THEN** it returns the compatible collection without a prompt or process

#### Scenario: Auto without advice retains its meaning
- **GIVEN** shell Auto with an unresolved executable and no applicable correction
- **WHEN** all hard checks pass
- **THEN** the call remains automatically approved

#### Scenario: Hard deny prevents correction
- **GIVEN** a denied call with an otherwise applicable correction
- **WHEN** the coordinator evaluates the call
- **THEN** it returns denial without advice, exposure, or execution

#### Scenario: Retry requires current authority
- **GIVEN** an actor committed one exact temporary retry key
- **WHEN** the same call retries
- **THEN** it consumes the key once and passes current authorization
- **AND** a changed invocation cannot consume that key

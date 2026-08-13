## ADDED Requirements

### Requirement: Shell policy coordinator preserves actor ownership

The system SHALL evaluate a shell call through one coordinator with three
phases: synchronous preflight, one asynchronous approval-actor batch match, and
deterministic completion.

Preflight SHALL snapshot the existing `ToolExecutionContext` and exact
`ToolApprovalAttempt.OneTimeApprovedPatterns` set. Those legacy-named strings
SHALL remain `OneTimeApprovalKeys` binding filtered phrase and effective
directory. Preflight SHALL build one canonical
`ShellCommandAnalysis`, apply hard deny and protected paths, resolve approval
mode, build candidates, and preserve the existing noninteractive trust-zone
gate. If preflight is not terminal, `DispatchingToolExecutor` SHALL send
exactly one typed batch request to `ToolApprovalActor`.

`ToolApprovalActor` SHALL atomically snapshot inherited session and persistent
grants. It SHALL return one match result per stable candidate ID. It SHALL NOT
own or inspect one-time approval state. The coordinator SHALL import actor
coverage, apply safe policy to still-uncovered candidates, validate the
invocation-owned one-time set exactly, and SHALL NOT rescan grants.

Reviewed-safe phrase coverage SHALL cover a candidate only when the run has
interactive approval capability. A run without that capability SHALL require
explicit one-time, session, or persistent authority for every candidate that
is not an approval-exempt side effect.

The actor result SHALL include typed persistent-store status. An absent store
file SHALL be ready with an empty snapshot. Expected corruption or migration
failure SHALL be unavailable. Completion SHALL allow a call fully covered by
one-time, session, approval-exempt side effects, or, for an interactive run,
reviewed-safe phrase coverage without persistent state. If any candidate
remains uncovered and persistent state was unavailable, completion SHALL
return terminal `ApprovalStoreUnavailable` instead of a prompt.

`ToolApprovalAttempt` SHALL remain owner of one-time invocation state.
`ToolApprovalActor` SHALL remain owner of session and persistent grants. The
session pipeline SHALL remain owner of pending requests, response validation,
stale-response rejection, and recovery.

The implementation SHALL reuse current execution, decision, candidate, match,
and prompt-context types when they can carry the required fact. It SHALL remove
superseded overlap. A new type SHALL exist only for the actor batch protocol or
a fact that no current type represents.

#### Scenario: One actor snapshot covers every candidate

- **GIVEN** a compound command has four candidates
- **WHEN** preflight completes without a terminal result
- **THEN** the executor sends one batch request containing four stable IDs
- **AND** the actor returns one result from one grant snapshot
- **AND** no synchronous policy service reads the approval store directly

#### Scenario: Independent coverage survives unavailable persistence

- **GIVEN** the persistent store is unavailable
- **AND** interactive approval capability is available
- **AND** session and reviewed-safe coverage jointly cover every candidate
- **WHEN** completion evaluates the actor result
- **THEN** the call is allowed
- **AND** no persisted grant is assumed

#### Scenario: Reviewed-safe policy does not grant headless authority

- **GIVEN** interactive approval capability is unavailable
- **AND** a complete candidate is in the reviewed-safe catalog
- **WHEN** no one-time, session, or persistent grant covers that candidate
- **THEN** the candidate remains uncovered
- **AND** the caller follows the current unsupported-channel denial path

#### Scenario: Explicit grant covers a headless candidate

- **GIVEN** interactive approval capability is unavailable
- **AND** a session or persistent grant covers a complete candidate
- **WHEN** completion evaluates the call
- **THEN** the explicit grant covers that candidate
- **AND** reviewed-safe policy adds no authority

#### Scenario: Uncovered candidate fails closed when persistence is unavailable

- **GIVEN** the persistent store is unavailable
- **AND** one candidate remains uncovered after one-time, session, and safe
  coverage
- **WHEN** completion evaluates the call
- **THEN** it denies with `ApprovalStoreUnavailable`
- **AND** it does not offer an approval prompt

#### Scenario: Hard deny terminates before actor match

- **GIVEN** a stored grant could match a command phrase
- **AND** canonical analysis matches hard deny
- **WHEN** preflight evaluates the call
- **THEN** it returns terminal deny
- **AND** the executor sends no grant-match request

#### Scenario: Noninteractive trust zone precedes approval matching

- **GIVEN** interactive approval is unavailable
- **AND** a stored grant covers the command phrase
- **WHEN** canonical path facts fall outside the configured trust zone
- **THEN** preflight returns terminal deny
- **AND** neither the stored grant nor safe policy can override it

#### Scenario: Recovery re-evaluates the original request

- **GIVEN** a pending approval is recovered after daemon restart
- **WHEN** the response resumes the request
- **THEN** policy re-evaluates the original source and immutable context
- **AND** it obtains a current actor snapshot before execution
- **AND** it does not replay a stale allow result

### Requirement: Candidate coverage composes authorization sources

Every ShellSyntaxTree command occurrence SHALL receive a stable call-local
candidate ID. Coverage SHALL begin `Uncovered` and MAY transition once to
OneTime, Session, PersistentGlobal, PersistentFolder, ReviewedSafePolicy, or
Denied.

A stage SHALL refine only uncovered candidates. A denial SHALL be terminal.
The coordinator SHALL allow only when every candidate has non-deny coverage and
every call-level invariant passes.

Expected unresolved shell syntax MAY produce a one-time prompt without
reusable choices. An internal exception, invalid enum, duplicate candidate ID,
mismatched actor result, or impossible transition SHALL produce terminal deny.

#### Scenario: Grants and safe policy compose

- **GIVEN** a command has `cd`, `gh api`, `wc`, and `head` candidates
- **AND** global grants cover `cd` and `gh api`
- **AND** reviewed safe policy covers `wc` and `head`
- **WHEN** the coordinator completes policy
- **THEN** every candidate has coverage
- **AND** the call is allowed without a prompt

#### Scenario: One uncovered candidate prompts

- **GIVEN** three candidates are covered and one remains uncovered
- **WHEN** no strict call-level invariant denies the call
- **THEN** the call requires one interactive prompt
- **AND** the prompt identifies the uncovered candidate

#### Scenario: Internal evaluator failure denies

- **WHEN** any policy stage throws or returns an invalid typed result
- **THEN** the final result is terminal deny with `InternalPolicyFailure`
- **AND** no approval prompt can override it
- **AND** the shell does not execute

#### Scenario: One-time approval requires the exact approval-key set

- **GIVEN** the invocation attempt contains one-time approval keys
- **WHEN** the current phrase-and-effective-directory key set differs by any
  missing or extra key
- **THEN** one-time coverage is not applied
- **AND** actor-owned session or persistent coverage is unaffected

### Requirement: Causal approval intent is separate from execution scope

The system SHALL keep canonical execution facts unchanged. For Bash only, it
MAY derive an approval-intent directory from an exact leading authored
directory transition on the success edge of `cd TARGET && ...`.

Intent MAY continue through later top-level diagnostic statements until a
later directory mutation, differing control-flow join, alternate branch,
subshell/group boundary, dynamic flow, or unsupported region invalidates it. An
exact later success-gated directory transition SHALL replace intent on its
success edge.

An intent target SHALL be eligible only when exact, absolute, normalized,
symlink-free, and allowed by protected-path policy. The directory-transition
candidate and first non-navigation action on its success edge SHALL already
have one-time, session, or stored-grant coverage. Safe policy alone SHALL NOT
manufacture causal intent.

Only a reviewed read-only-for-all-arguments candidate without a file-writing
redirect MAY consume eligible intent. Hard deny, protected paths, folder
grants, noninteractive authority, and process execution SHALL use real facts.
The system SHALL NOT rewrite source, arguments, cwd, or model history.

Native PowerShell SHALL remain strict in this slice and SHALL NOT derive causal
scope from `Set-Location`.

#### Scenario: Exact D03 chain composes under intended tmp scope

- **GIVEN** global grants cover `cd` and `gh api`
- **AND** `wc` and `head` are reviewed read-only entries
- **WHEN** the agent submits
  `cd /tmp && gh api repos/example/project/actions/jobs/123456/logs > slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log`
- **THEN** real redirect and path facts pass deny policy
- **AND** the exact protected-path-safe `/tmp` target is eligible approval
  intent for `wc` and `head`
- **AND** all four candidate coverages compose to allow

#### Scenario: Later unknown directory mutation invalidates intent

- **GIVEN** intent is `/tmp`
- **WHEN** a later `cd "$1"` precedes a diagnostic tail
- **THEN** the tail has unknown intent
- **AND** safe policy cannot use the earlier `/tmp` intent

#### Scenario: Alternate branch does not leak intent

- **WHEN** source is `cd /tmp && inspect || recover; head result.log`
- **THEN** the joined intent before `head` is unknown
- **AND** real execution facts still control path policy

#### Scenario: Subshell intent does not escape

- **WHEN** source is `(cd /tmp && inspect); head result.log`
- **THEN** `/tmp` intent applies only inside the subshell
- **AND** it does not cover the outer `head`

#### Scenario: Native PowerShell stays strict

- **WHEN** native Windows PowerShell analyzes
  `Set-Location C:\\Temp; Get-Content result.log`
- **THEN** no synthetic causal scope is created
- **AND** existing real-scope and provider rules decide the call

### Requirement: Shell approval decision trace is bounded and redacted

The coordinator SHALL return one ordered trace. Rows SHALL contain only enum
stage, enum outcome, enum reason, call-local candidate ID, bounded executable
basename, coverage kind, scope relation, and grant timestamp.

The trace SHALL NOT contain full commands, argument values, environment values,
redirect bodies, raw paths, tokens, secrets, or model content. It SHALL contain
at most one row per stage per candidate and 256 rows total. Text fields SHALL
contain at most 128 UTF-16 code units. Control, newline, bidi, and invalid
Unicode SHALL be escaped. Secret-pattern redaction SHALL run before logging.

Trace overflow SHALL add one `TraceTruncated` row without changing the decision.
The trace SHALL not enter prompts or session persistence. Near-miss diagnostics
SHALL project from the trace without another grant scan.

#### Scenario: Grant and safe coverage produce one trace

- **WHEN** actor grants and safe policy jointly cover a call
- **THEN** the trace contains one coverage row for each candidate
- **AND** the final row is `Allow(AllCandidatesCovered)`

#### Scenario: Malicious text cannot forge trace lines

- **GIVEN** authored input contains CR, LF, bidi controls, or a token-like secret
- **WHEN** a strict result is logged
- **THEN** controls are escaped and secrets are redacted
- **AND** no authored text creates an additional log row

#### Scenario: Trace overflow does not widen authority

- **WHEN** trace evidence exceeds a configured bound
- **THEN** later detail is replaced by `TraceTruncated`
- **AND** candidate coverage and the final decision are unchanged

### Requirement: Exact sanitized beta approval catalog

The change SHALL contain `evidence/approval-matrix.json` with exact sanitized
D01-D18 commands, observed responses, classifications, owners, parser facts,
and policy outcomes. It SHALL match the paired ShellSyntaxTree artifact
byte-for-byte.

The shared catalog SHALL NOT imply structured trace fields through prose.
Netclaw SHALL also contain `evidence/netclaw-policy-fixtures.json` with exact
candidate IDs, typed phrases, scopes, available grants and safe entries,
expected per-candidate coverage, ordered trace rows, and final outcome for the
policy-owned acceptance cases. Tests SHALL load those fields directly and
SHALL NOT branch on Dxx identifiers to manufacture expected results.

Fixture defaults SHALL explicitly provide tool name, audience, approval mode,
interactive capability, session identity and safe root, project safe root,
inherited cwd, and persistent-store status. Each case SHALL provide canonical
shell environment and initial cwd. Every stored grant SHALL carry a canonical
shell tag. D02, D03, D07, D08, D09, D10, D11, D14, D17, and D18 SHALL be exact
executable fixtures.

Additional adversarial rows SHALL cover dynamic identity, deny-only wrappers,
redirects, protected paths, prefix collisions, runtime iterators, PowerShell
providers, and unsafe catalog entries.

#### Scenario: Every harvested prompt appears once

- **WHEN** the catalog loads
- **THEN** IDs D01 through D18 each occur exactly once
- **AND** each classification is correct prompt, Netclaw policy defect,
  ShellSyntaxTree fact gap, or irreducibly dynamic

#### Scenario: Catalog contains no source identity

- **WHEN** the PII audit scans the change
- **THEN** it finds no local username, private repository, channel, thread,
  host, email, token, or secret

#### Scenario: Unsafe catalog counterexamples stay strict

- **WHEN** policy evaluates `find . -exec rm {} +`,
  `awk 'BEGIN { system("touch marker") }'`, `rg --pre helper pattern .`, and
  `sort -o output input`
- **THEN** none receives reviewed safe-policy coverage

## MODIFIED Requirements

### Requirement: Global grant precedence over folder-scoped grants

A persisted global version-3 phrase (`directory: null`) SHALL authorize every
candidate matched by the phrase in its declared audience, tool, and canonical
shell. When both a global entry and folder-scoped entries exist for the same
typed phrase identity, the global entry SHALL be sufficient regardless of real
cwd. Folder-scoped entries SHALL remain on disk so revoking the global entry
restores the narrower authority.

The matcher SHALL evaluate every persisted entry whose canonical shell, match
kind, and phrase identity can cover the candidate. It SHALL NOT stop at the
first phrase match whose directory check fails. Adding a global entry SHALL
NOT remove, supersede, or rewrite a folder entry.

#### Scenario: Global token phrase wins outside folder scope

- **GIVEN** version 3 contains folder and global `TokenPrefix` entries with
  Bash tokens `["dotnet"]`
- **WHEN** Bash invokes `dotnet --info` outside the folder
- **THEN** the global entry covers the candidate
- **AND** no prompt is rendered

#### Scenario: Adding global phrase retains narrower phrase

- **GIVEN** version 3 contains a folder-scoped Bash phrase `["dotnet"]`
- **WHEN** the user approves the same phrase everywhere
- **THEN** both entries remain on disk with their original timestamps
- **AND** revoking the global entry restores folder-only matching

### Requirement: Approval entry creation timestamp

Each version-3 approval entry SHALL carry optional ISO-8601 `createdAt` and
SHALL be stamped on first persistence using injected `TimeProvider`. Timestamp
SHALL NOT participate in equality. Phrase identity for idempotency SHALL be
canonical shell, match kind, token array or legacy-exact value, and directory.

Adding an equivalent entry SHALL preserve the existing entry and its original
timestamp. Version-2 migration SHALL preserve an existing timestamp exactly;
a missing timestamp SHALL remain null. Migration SHALL NOT restamp grants.

#### Scenario: New version-3 grant receives one timestamp

- **GIVEN** a deterministic `TimeProvider`
- **WHEN** a new typed phrase is persisted
- **THEN** `createdAt` equals the provider time
- **AND** re-adding the same phrase and directory does not change it

#### Scenario: Migration preserves timestamp absence

- **GIVEN** a valid version-2 entry without `createdAt`
- **WHEN** it migrates to a version-3 phrase
- **THEN** its `createdAt` remains null
- **AND** phrase equality remains independent of time

### Requirement: Shell command pattern matching

The system SHALL derive one candidate from every complete canonical
`ShellSyntaxTree.CommandOccurrence`. Candidate identity SHALL use the static
authored verb tokens reported by ShellSyntaxTree. Netclaw SHALL NOT parse an
executable's private subcommands, flags, options, or operands.

Every candidate SHALL retain its occurrence, redirects, effective and authored
value facts, real scope, and optional intent scope. Pipelines, lists, and loops
SHALL NOT hide later occurrences. Incomplete identity or unknown policy-relevant
facts SHALL remain strict.

Stored token-prefix phrases SHALL compare whole tokens with the selected
shell's case rule. Raw string prefix SHALL NOT authorize. Same-language wrapper
occurrences reported by ShellSyntaxTree SHALL remain visible. Cross-language
payloads SHALL remain arguments to the native host command.

Display and persistence SHALL keep existing spoof protections. Raw source SHALL
remain verbatim in the prompt only. CR, LF, bidi controls, malformed quoting,
and multiword free-text SHALL NOT enter a stored phrase. Path evidence SHALL
remain available to directory policy. Candidate normalization SHALL be the same
for actor match, prompt options, and persistence.

#### Scenario: Token-prefix grant covers a greedy candidate

- **GIVEN** a Bash token-prefix grant `git push`
- **AND** ShellSyntaxTree reports tokens `git`, `push`, `upstream`
- **WHEN** actor matching compares them
- **THEN** the grant covers the candidate
- **AND** no Git-specific remote rule runs

#### Scenario: Prefix collision does not match

- **GIVEN** a grant with tokens `git`, `push`
- **WHEN** the candidate tokens are `git`, `push-force`
- **THEN** the grant does not match

#### Scenario: All occurrences remain visible

- **WHEN** source is `inspect && head file; wc file`
- **THEN** candidates exist for `inspect`, `head`, and `wc`
- **AND** coverage for one cannot hide another

#### Scenario: Same-language wrapper exposes inner occurrences

- **WHEN** ShellSyntaxTree reports a static `bash -c` inner occurrence
- **THEN** that occurrence receives its own candidate and deny evaluation
- **AND** Netclaw does not decode the wrapper itself

#### Scenario: Cross-language payload stays external data

- **GIVEN** the canonical shell is Bash
- **WHEN** Bash invokes `pwsh -Command 'Get-Content ./a.txt'`
- **THEN** `pwsh` is the Bash external-command candidate
- **AND** the inline payload is not parsed as native PowerShell

#### Scenario: Multi-line or bidi content cannot persist

- **WHEN** a candidate contains multi-line, carriage-return, or bidi-controlled
  authored content
- **THEN** that content is excluded from the normalized grant phrase
- **AND** the prompt retains a separately escaped verbatim display
- **AND** no reusable option is offered if a clean phrase cannot be formed

#### Scenario: Dynamic identity stays one-time

- **WHEN** source is `"$1" --version`
- **THEN** no stored phrase or safe policy covers the identity
- **AND** only one-time approval and deny are offered

### Requirement: Persistent approval storage

The system SHALL store persistent approvals in
`~/.netclaw/config/tool-approvals.json` using version 3. New shell entries SHALL
contain canonical shell, match kind, immutable verb-token array, optional
absolute directory, and creation timestamp. Null directory SHALL mean global.

On first successful version-2 load, the daemon SHALL back up the original file.
Every valid version-2 shell phrase SHALL migrate as an exact-only legacy
phrase. No migrated phrase SHALL gain token-prefix authority. A valid v2 entry whose
phrase contains controls or cannot be represented safely SHALL be omitted with
a bounded migration diagnostic and SHALL NOT authorize. A structurally invalid
v2 file SHALL fail as a whole. The version-3 write SHALL be atomic.

The daemon SHALL observe valid operator edits on the next approval check. CLI
list, add, and revoke SHALL understand both token-prefix and legacy-exact
entries. It SHALL NOT silently downgrade a version-3 file.

An absent file SHALL be a valid empty store. An absent-version or version-1
file SHALL follow the existing quarantine path and become an empty version-3
store only after a successful atomic write. Malformed JSON, partial version-3
corruption, invalid enum or token values, and unsupported future versions SHALL
make the store unavailable: no entry SHALL authorize and an
approval-dependent call SHALL terminate deny with `ApprovalStoreUnavailable`.
A future-version file SHALL remain untouched.

Failure to create the v2 backup SHALL abort migration and leave v2 untouched.
Failure of atomic replacement SHALL retain v2 and any completed backup, make
the store unavailable for that check, and permit a later load to retry. The
loader SHALL NOT salvage individual grants from a partially corrupt version-3
or structurally invalid version-2 file.

#### Scenario: New global entry stores tokens

- **WHEN** the user approves `git push` everywhere under native Bash
- **THEN** version 3 stores shell `Bash`, match `TokenPrefix`, tokens
  `["git", "push"]`, and null directory

#### Scenario: Ambiguous v2 phrase remains exact

- **GIVEN** a v2 verb contains quoting or an escape
- **WHEN** migration runs
- **THEN** the entry becomes `LegacyExact`
- **AND** it does not gain token-prefix authority

#### Scenario: Invalid migrated entry cannot authorize

- **GIVEN** a v2 entry contains controls or cannot be represented safely
- **WHEN** migration runs
- **THEN** the entry is omitted with a bounded migration diagnostic
- **AND** no candidate matches it

#### Scenario: Revocation is visible without restart

- **WHEN** an operator revokes a version-3 entry through the CLI
- **THEN** the next actor snapshot excludes it
- **AND** a later call prompts if no other coverage exists

#### Scenario: Future schema fails closed without modification

- **GIVEN** `tool-approvals.json` declares a version newer than 3
- **WHEN** an approval-dependent shell call is checked
- **THEN** no persisted entry authorizes
- **AND** the call is denied with `ApprovalStoreUnavailable`
- **AND** the file is not rewritten or quarantined

#### Scenario: Backup failure preserves version 2

- **GIVEN** a valid version-2 store
- **AND** creation of `.v2.bak` fails
- **WHEN** migration is attempted
- **THEN** the version-2 source remains byte-identical
- **AND** no version-3 replacement is attempted
- **AND** the approval-dependent call fails closed

### Requirement: Directory-root approvals for shell_execute

Global token phrases SHALL not require an exact cwd. Folder phrases SHALL
require the candidate's real exact scope under the stored directory, with
normalization, boundary-safe containment, minimum-depth, traversal, and symlink
checks. Intent scope SHALL never satisfy a folder grant.

`Once` SHALL retry only the blocked request. `This chat` SHALL create typed
session entries. `Always here` SHALL persist one clean version-3 entry per
persistable candidate at the real prompt scope. `Always anywhere` SHALL persist
global entries. Unknown or synthetic-only scope SHALL omit `Always here`.

#### Scenario: Global grant works with unknown cwd

- **GIVEN** a global token phrase covers a static candidate
- **WHEN** its joined cwd is unknown and path facts are otherwise strict-safe
- **THEN** the actor may cover the candidate globally

#### Scenario: Folder grant rejects synthetic-only scope

- **GIVEN** a folder grant under `/work/project`
- **AND** only intent scope is `/work/project`
- **WHEN** the real candidate scope is unknown
- **THEN** the folder grant remains a near miss

#### Scenario: One persistent click stores each clean candidate

- **GIVEN** three candidates are clean and persistable
- **WHEN** the user selects a persistent option
- **THEN** one typed entry is stored for each candidate
- **AND** no uncovered candidate is silently omitted

#### Scenario: Symlink cannot widen folder authority

- **GIVEN** a path under a folder grant crosses a symlink to protected space
- **WHEN** policy evaluates it
- **THEN** folder coverage fails
- **AND** protected-path policy denies when applicable

### Requirement: Safe-verb auto-allow short-circuit in declared safe spaces

The system SHALL load an embedded immutable per-platform policy catalog.
`ReadOnlyForAllArguments` SHALL mean no accepted argument shape can write or
delete a file, execute another command, or mutate a remote service through the
executable's argv interpretation. Runtime user overrides SHALL NOT widen the
catalog. Redirects, parser-owned path operands, provider paths, and unknown
shell expansions SHALL remain separate strict effects. Displaying a value
explicitly supplied by the shell SHALL NOT by itself disqualify a phrase.

Safe policy SHALL refine only uncovered candidates. It SHALL require reviewed
phrase coverage, an allowed real or eligible intent scope, no symlink segment,
no writing redirect, and no unknown explicit path fact. Hard deny and protected
paths SHALL run first. Personal and Team safe roots SHALL be session directory
plus declared project directory. Public SHALL use session directory only.

`find`, `awk`, `rg`, and `sort` SHALL not be reviewed-safe. Production policy
code SHALL contain no executable-specific flag exceptions. PowerShell provider
paths SHALL retain existing strict checks.

#### Scenario: Read-only candidate in project scope is covered

- **GIVEN** `head` is reviewed safe
- **AND** its real scope is under a Personal project root
- **WHEN** every earlier stage passes
- **THEN** safe policy covers that candidate

#### Scenario: Undeclared project scope returns an agent correction

- **GIVEN** every shell candidate has a reviewed-safe phrase
- **AND** every effective directory is beneath the exact shell cwd
- **AND** the cwd is outside the declared session and project roots
- **AND** the cwd is not the platform temporary root
- **AND** `set_working_directory` is available to the agent
- **AND** the same filesystem policy used by `set_working_directory` accepts
  the exact cwd without substitution
- **WHEN** policy would otherwise request user approval
- **THEN** the system returns a scope-declaration correction to the agent
- **AND** it does not execute the command or request user approval
- **AND** the correction tells the agent to declare the exact cwd and retry the
  exact command unchanged

#### Scenario: Scope correction cannot hide unsafe work

- **GIVEN** any candidate lacks reviewed-safe phrase coverage
- **OR** any effective directory is outside the exact shell cwd
- **OR** the audience is Public
- **OR** the cwd is the platform temporary root
- **OR** `set_working_directory` is unavailable
- **OR** `set_working_directory` policy would reject or substitute the cwd
- **WHEN** policy evaluates the call
- **THEN** the scope-declaration correction does not apply
- **AND** the normal approval or deny result remains

#### Scenario: Unsafe argument surface excludes whole phrase

- **GIVEN** any accepted argument can write or execute
- **WHEN** maintainers audit the catalog phrase
- **THEN** the phrase is excluded entirely
- **AND** no private flag branch compensates for it

#### Scenario: File redirect remains separate

- **WHEN** reviewed `head` writes through a shell redirect
- **THEN** safe policy does not cover the occurrence
- **AND** redirect path policy still applies

#### Scenario: Public project directory is not safe

- **GIVEN** a Public session has a project directory
- **WHEN** a reviewed read-only candidate runs only there
- **THEN** safe policy does not cover it

#### Scenario: PowerShell environment provider stays strict

- **WHEN** native PowerShell submits `Get-Content Env:SECRET`
- **THEN** the provider is not treated as filesystem safe space
- **AND** the call requires explicit authority or denial

### Requirement: Pattern extraction refuses bash control-flow

Authorization SHALL use canonical ShellSyntaxTree completeness rather than a
second control-flow tokenizer. Supported static loops SHALL expose candidates.
Unsupported branches and runtime-generated loops SHALL remain strict.

An effective finite argument SHALL enter path policy when the parser-owned
`Argument.IsPath` role is true. ShellSyntaxTree 0.3.3 `Exact` and `FiniteSet`
`AuthoredFileSystemValue` facts SHALL also enter path policy. Unknown and all
other alternatives SHALL stay strict. `AuthoredPathShape` SHALL NOT substitute
for the stronger fact or create file authority.

A legacy scanner MAY add a denial when canonical analysis is incomplete. It
SHALL NOT allow, create candidates, create persistent options, or widen scope.

#### Scenario: ShellSyntaxTree 0.3.2 keeps D14 path coverage strict

- **GIVEN** ShellSyntaxTree 0.3.2 reports D14 finite authored values
- **AND** its effective argument has `Argument.IsPath` false
- **WHEN** the maintainer-approved authored-source policy evaluates it
- **THEN** the authored values do not create file authority
- **AND** lexical `AuthoredPathShape` does not cover the candidate

#### Scenario: ShellSyntaxTree 0.3.3 unlocks finite D14 path checks

- **GIVEN** ShellSyntaxTree 0.3.3 reports a finite D14
  `AuthoredFileSystemValue`
- **WHEN** the maintainer-approved authored-source policy evaluates it
- **THEN** each finite `cat` path passes `ToolPathPolicy`
- **AND** the presence of `for` alone does not force a prompt

#### Scenario: Runtime iterator stays one-time

- **WHEN** an iterator depends on command substitution output
- **THEN** the call offers only one-time approval and deny
- **AND** policy does not execute the substitution

#### Scenario: Deny-only scanner cannot authorize

- **GIVEN** canonical analysis is incomplete
- **WHEN** a legacy scan finds no deny pattern
- **THEN** the call remains unresolved
- **AND** it does not receive grant or safe coverage

### Requirement: Approval-gate near-miss diagnostics

Near-miss diagnostics SHALL project only from the actor match trace. A near miss
SHALL identify candidate ID, grant kind, creation timestamp, and enum reason
such as token mismatch, shell mismatch, outside directory, or symlink. It SHALL
not include raw arguments, raw paths, or secrets and SHALL not rescan grants.

Diagnostics SHALL be operator-log-only and SHALL not alter the prompt or final
decision.

#### Scenario: Folder near miss uses actor evidence

- **GIVEN** a token phrase matches but folder scope does not
- **WHEN** the actor returns uncovered coverage
- **THEN** its trace contains `OutsideDirectory`
- **AND** logging uses that row without another store read

#### Scenario: First-time prompt has no fabricated near miss

- **GIVEN** no grant was considered for a candidate
- **WHEN** it remains uncovered
- **THEN** no grant near-miss row is emitted

### Requirement: Subagent inherits parent session-scoped approvals

The approval actor SHALL walk a child scope toward its parent session using the
existing bounded `/subagent/` scope rule. Typed session phrases from the parent
SHALL cover matching child candidates. Unrelated sessions SHALL never share
coverage. The batch actor request SHALL perform this walk within the same
atomic snapshot as persistent matching.

#### Scenario: Parent session phrase covers child candidate

- **GIVEN** the parent chat has a typed session grant for `gh pr view`
- **WHEN** its child submits a matching candidate
- **THEN** the actor returns Session coverage
- **AND** no separate parent-grant scan runs

### Requirement: Shell policy uses the canonical grammar and dialect

The system SHALL select Bash only for native Bash execution and PowerShell only
for native Windows PowerShell execution. Bash invoking `pwsh` SHALL remain one
Bash external command. Every authorization stage SHALL share one canonical
ShellSyntaxTree analysis.

PowerShell SHALL use the selected dialect and `PwshInitialStateMode.Unknown`.
Netclaw SHALL use effective values for runtime and deny policy. It MAY use
authored values only for the approved approval perspective. It SHALL route
ShellSyntaxTree 0.3.3 authored filesystem values through path policy. Unknown
policy-relevant values SHALL not create reusable or safe coverage.

Deny-only defensive scans MAY deny incomplete input but SHALL never authorize
it.

#### Scenario: PowerShell pipeline evaluates every occurrence

- **WHEN** native Windows PowerShell submits a pipeline
- **THEN** every stage receives a candidate or strict finding
- **AND** one covered stage cannot hide an uncovered stage

#### Scenario: Bash does not cross-parse PowerShell payload

- **WHEN** native Bash submits `pwsh -Command 'Get-Content ./a.txt'`
- **THEN** policy evaluates the Bash `pwsh` occurrence
- **AND** it does not create a native PowerShell child candidate

#### Scenario: Authored facts do not replace effective deny facts

- **GIVEN** an argument has finite `AuthoredValue` but unknown effective value
- **WHEN** hard deny or runtime path policy evaluates it
- **THEN** those stages retain the effective uncertainty
- **AND** authored facts are limited to the approved matching perspective

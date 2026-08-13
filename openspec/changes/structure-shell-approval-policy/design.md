## Context

The exact sanitized harvest is `evidence/approval-matrix.json`. The approval
store contained 435 Personal shell grants and no new persistent grant during
the window. The observed responses were one-time, session, denied, or pending.
Complex prompts can expose only `Once` and `Deny`. An observed `Once` response
does not prove that the operator preferred one-time authority.

Current ownership is split. `ToolAccessPolicy` performs synchronous policy,
`DispatchingToolExecutor` coordinates asynchronous approval,
`ToolApprovalAttempt` owns the exact one-time grant for one invocation,
`ToolApprovalActor` owns session and persistent grants, and the session
pipeline owns pending requests, responses, and recovery. The redesign must
preserve those boundaries while making one explainable decision.

ShellSyntaxTree supplies shell facts. Netclaw owns authority. Executable-private
options and operands remain outside both the policy evaluator and safe-catalog
code.

## Goals / Non-Goals

**Goals:**

- Build one immutable preflight fact set.
- Compose grants and safe policy per candidate.
- Preserve one atomic actor snapshot for session and persistent grants.
- Keep real execution facts separate from approval-intent facts.
- Make hard deny, protected paths, and internal errors terminal.
- Use versioned shell-token grant phrases without raw prefix matching.
- Bound and redact every trace field.
- Pin exact Linux and native Windows outcomes.

**Non-Goals:**

- Parse Git, GitHub CLI, .NET CLI, or any other executable's private grammar.
- Rewrite a tool call, execution source, working directory, or model history.
- Treat approval as a process sandbox.
- Infer ambient profiles, aliases, functions, modules, or environment values.
- Auto-approve remote mutation, repository moves, runtime-generated loops, or
  unsupported control flow.
- Add PowerShell causal scope in this slice.

## Decisions

### 1. Coordinate preflight, actor match, and completion

The executor uses one coordinator. It receives the existing `ToolName`,
`ToolExecutionContext`, argument object, `ShellExecutionEnvironment`, and
`ToolApprovalMode`. At entry it snapshots the immutable `ToolRunScope` facts
and the exact `ToolApprovalAttempt.OneTimeApprovedPatterns` set. Despite the
legacy property name, those strings are `OneTimeApprovalKeys` that bind the
filtered phrase and effective-directory set. The coordinator does not invent a
parallel execution context or a scalar retry key.

Only the actor seam needs a new protocol shape:

```csharp
internal sealed record ShellApprovalMatchRequest(
    SessionId? SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    ShellExecutionEnvironment Environment,
    IReadOnlyList<ShellGrantCandidate> Candidates);

internal sealed record ShellGrantCandidate(
    int CandidateId,
    ShellGrantPhrase Phrase,
    string? RealDirectory);

internal sealed record ShellApprovalMatchResult(
    PersistentGrantStoreStatus PersistentStore,
    IReadOnlyList<ShellGrantCandidateMatch> CandidateMatches);

internal abstract record PersistentGrantStoreStatus
{
    private PersistentGrantStoreStatus() { }
    internal sealed record Ready : PersistentGrantStoreStatus;
    internal sealed record Unavailable(ShellPolicyReason Reason) : PersistentGrantStoreStatus;
}

internal sealed record ShellGrantCandidateMatch(
    int CandidateId,
    ToolApprovalMatch? Match,
    IReadOnlyList<ShellGrantNearMiss> NearMisses);
```

`ShellExecutionEnvironment` carries platform, canonical grammar, path style,
and PowerShell dialect. `ToolRunScope.Session` may be sessionless; a bound
scope supplies its nullable session directory. No protocol field assumes a
session ID or directory exists.

`DispatchingToolExecutor` runs preflight once. Its ordered synchronous stages
are canonical parse validation, hard deny, protected paths, approval-mode
resolution, candidate construction, and the existing
`InteractiveApprovalCapability.Unavailable` trust-zone enforcement. A
terminal deny returns immediately. Otherwise it sends exactly one batch
request to `ToolApprovalActor`. The actor atomically snapshots inherited
session grants and, when available, persistent grants; matches every candidate;
and returns the match for each stable ID plus typed persistent-store status. It
does not own or inspect one-time state. An absent file is `Ready` with an empty
persistent snapshot. Expected corruption or migration failure is
`Unavailable`; an unexpected actor/protocol failure remains an internal error.

The coordinator imports actor coverage, applies reviewed safe policy to still
uncovered candidates, and finally checks whether the invocation-owned exact
one-time approval-key set satisfies the remaining prompt as a set. It constructs
one result and never rescans grants. When every candidate is covered by
one-time, session, or reviewed-safe authority, persistent-store unavailability
does not deny the call. If any candidate remains uncovered after those sources
and the persistent store was unavailable, completion returns terminal
`ApprovalStoreUnavailable` instead of a prompt.

Implementation must reuse `ToolExecutionContext`, `ToolAccessDecision`,
`ApprovalCandidate`, `ToolApprovalCheckResult`, `ToolApprovalMatch`, and
`ToolApprovalRequiredContext` where their current contracts fit. It must
replace overlapping types instead of leaving a parallel model. A new DTO is
justified only for the actor batch protocol or a fact no current type can
represent.

Ownership does not move: `ToolApprovalAttempt` owns one-time invocation state;
`ToolApprovalActor` owns session inheritance and persistent snapshots; the
session pipeline owns pending approval, response validation, stale-response
rejection, and recovery. Coordinator facts are immutable snapshots, not actor
or pipeline state.

### 2. Track coverage per candidate

Every ShellSyntaxTree occurrence receives a stable call-local `CandidateId`.
Coverage is a state machine:

```csharp
internal enum ShellCoverageKind
{
    Uncovered = 0,
    OneTime = 1,
    Session = 2,
    PersistentGlobal = 3,
    PersistentFolder = 4,
    ReviewedSafePolicy = 5,
    Denied = 6,
}

internal sealed record ShellCandidateCoverage(
    int CandidateId,
    ShellCoverageKind Kind,
    ShellPolicyReason Reason);
```

A stage may refine only `Uncovered`. Deny is terminal. Allow occurs only when
every candidate has non-deny coverage and every call-level invariant passes.
An expected unresolved parse may produce a one-time prompt with no reusable
choices. An exception, invalid enum, mismatched candidate ID, duplicate actor
result, or impossible transition is an internal failure and terminal deny.

D03 therefore composes global `cd` and `gh api` grants with reviewed `wc` and
`head` safe coverage instead of requiring one stage to authorize the whole
call.

### 3. Keep authorization on canonical facts; retain deny-only defenses

The canonical ShellSyntaxTree result supplies occurrences, paths, redirects,
and control flow to every authorization stage. No second tokenizer may allow,
create candidates, widen scope, or create persistence choices.

Existing legacy scans may remain only as deny-only defense when canonical
analysis is incomplete. A deny-only scan can convert prompt to deny. It cannot
convert deny or prompt to allow, and its output cannot enter a stored grant.

### 4. Model causal approval intent separately

`Execution` is the unmodified ShellSyntaxTree analysis at the real starting
directory. Hard deny, protected paths, folder grants, noninteractive authority,
and process execution use it.

For Bash only, `Intent` can carry an exact target from ShellSyntaxTree 0.3.4.
The leading occurrence must publish `ChangesOnSuccess(Exact(target))`. Its next
top-level action must be success-gated with `&&`. Intent may remain the user's
approval scope for later top-level diagnostics. A semicolon can still execute a
tail in the original directory after failure. Intent is an approval fact, not a
runtime cwd claim.

Intent is invalidated by:

- a later `Unknown` working-directory effect;
- a later `ChangesOnSuccess` effect whose target is not exact;
- `||`, alternate branches, or a join whose incoming intent scopes differ;
- entry to or exit from a subshell/group boundary unless both sides retain the
  same proved intent;
- dynamic identity, command substitution controlling flow, or unsupported
  control flow.

An exact later success-gated `ChangesOnSuccess` effect replaces intent. An
`Unchanged` effect preserves it. Netclaw does not identify transition verbs.
Relative authored paths under intent are rebased only for safe-policy scope.
Protected-path evaluation also checks their real execution projection. Folder
grants never use intent.

An intent target is eligible only when it is exact, absolute, normalized, and
allowed by protected-path policy. It must contain no symlink segment. The
captured platform temporary alias can map to its canonical root. No other
symlink target is eligible. The `cd` candidate and the first non-navigation
action on its success edge must already have one-time, session, or stored-grant
coverage. This user authority lets a later reviewed diagnostic consume intent
outside a normal session or project safe root. Safe policy alone cannot create
causal intent.

Only a reviewed diagnostic entry and an occurrence without a file-writing
redirect can consume eligible intent. Native
PowerShell remains strict in this slice: `Set-Location` does not create causal
intent. Existing PowerShell filesystem-provider checks remain mandatory, and
`Get-Content Env:SECRET` cannot receive filesystem safe-space coverage.

### 5. Persist typed shell-token phrases in schema version 3

New entries persist canonical token arrays plus the canonical shell:

```json
{
  "shell": "Bash",
  "match": "TokenPrefix",
  "verbTokens": ["git", "push"],
  "directory": null
}
```

Matching compares token arrays with the selected shell's case rule. A shorter
grant matches only whole leading tokens. Raw string prefix is never used.

On first successful load of version 2, each valid shell entry becomes
`LegacyExact`. It keeps exact-text behavior. No old grant gains token-prefix
authority. A valid entry that has controls or no safe form is omitted. One
bounded migration diagnostic reports the omission count. The original file is
copied to `.v2.bak` before one atomic version-3 replacement. Session grants use
the same typed phrase model but are not persisted.

Storage recovery is explicit and fail closed:

- an absent file is a valid empty store;
- an absent-version or version-1 file follows the existing v1 quarantine path,
  produces an empty version-3 store only after a successful atomic write, and
  emits one bounded operator diagnostic;
- malformed JSON, a partially invalid version-3 file, an invalid enum or token
  array, or an unsupported future version makes the approval store unavailable;
  no entry from that file authorizes and an approval-dependent call terminates
  with `ApprovalStoreUnavailable` rather than offering a prompt;
- a future-version file is never modified or quarantined;
- failure to create the v2 backup aborts migration and leaves v2 untouched;
- failure of the atomic version-3 replacement leaves v2 and any completed
  backup intact, marks the store unavailable for that check, and retries
  migration on a later load.

The implementation never salvages individual grants from a partially corrupt
file. This avoids silently changing the authority set.

Only a new version-3 grant can use `TokenPrefix`. The user sees that phrase in
the approval surface before Netclaw stores the grant.

A `LegacyExact` entry compares with the projected legacy candidate phrase. It
does not compare with the full command line. A global `gh api` entry therefore
covers both read and mutation calls whose projected phrase is `gh api`. This
behavior preserves version-2 authority and reduces prompts. An operator can
revoke that entry without a reset of unrelated approvals.

### 6. Use a reviewed immutable safe-policy catalog

The bundled per-platform resource contains typed phrase entries. A
`ReviewedDiagnostic` entry classifies the shell-authored invocation. It does
not classify all executable behavior.

No accepted authored argument shape may:

- select a child executable;
- select a caller-authored output file;
- request destructive or persistent configuration state; or
- request a remote mutation.

Tool-private metadata or cache refresh is outside this claim. Ambient
executable configuration is also outside this claim. Paths that an executable
discovers after execution starts are outside this claim.

These exclusions do not relax shell-authored checks. Redirects, parser-owned
filesystem values, provider paths, and unknown shell expansions remain strict.
Bounded shell-local output variables are permitted. Any unresolved later use
of that state remains strict.

The catalog is immutable at runtime. User-overridable safe-verb files are
removed because an agent-writable or operator-edited file can silently widen
authority outside code review. At minimum `find`, `awk`, `rg`, and `sort` are
not eligible. Production code has no flag-specific exceptions. The existing
`git ls-tree` special case is deleted.

Reviewed-safe authorization uses only canonical ShellSyntaxTree token prefixes.
Legacy display and compatibility strings do not establish safe coverage.

The parser-owned source order supplies two conservative guards. No authored
argument may appear before the matched phrase completes. Every argument with a
known lexical path shape must resolve beneath an eligible safe root.

Lexical path shape never creates authority. It only blocks reviewed-safe
coverage when a possible local path escapes the allowed roots or stays
unresolved. Parser-owned filesystem values and redirects still pass through
their stronger existing checks.

Parent sessions and subagents consume the same project-scope correction before
they open an approval request. The correction is available only when the
registered `set_working_directory` tool accepts the exact suggested directory
under its normal filesystem policy. A subagent returns the correction as the
tool result and leaves the authored call in history. If the tool is absent or
rejects the directory, the subagent keeps the existing parent approval bridge
path.

A headless subagent may receive this model-facing correction even though it
cannot open an approval bridge. After a successful declaration, the unchanged
retry follows the ordinary headless authority rules. The declared root prevents
another correction; it does not grant reviewed-safe or stored authority.

A successful child `set_working_directory` call replaces only the child run's
immutable project-scope snapshot. It reloads project instructions through the
same prompt provider and rebuilds the child's system prompt before the next
model call. Later child tool calls use the new scope. The child reports the
local scope in its result, but the parent merge keeps its existing rule: child
project selection does not replace the parent project directory.

Model guidance distinguishes an exact candidate scope from a declared safe
root. An absolute path operand lets policy bind a candidate to that path. It
does not add a safe-space root or make an otherwise uncovered phrase safe. If
a task needs several shell calls in a user-named project that differs from the
current project, the agent declares that project before the first shell call.
This rule also applies to subagents whose exposed tools include the declaration
tool, and to commands with absolute operands. One shell call can use the typed
`WorkingDirectory` argument without changing the persistent project root. The
final headless subagent contract conditionally repeats the multi-command rule
after role guidance so the execution boundary stays clear.

The shared `set_working_directory` validation rejects NUL, CR, and LF before
filesystem resolution. This rule applies to both execution and the eligibility
probe. An invalid path returns a bounded error and cannot enter model history,
child scope, or project-instruction lookup as a successful declaration.

### 7. Consume ShellSyntaxTree 0.3.1 facts through 0.3.4 explicitly

Netclaw uses effective `AnalyzedArgument.Value` for runtime-sensitive checks.
It may use `AuthoredValue` for approval matching only after the maintainer
accepts that ambient Bash attributes, ambient `IFS`, and field splitting are
outside the approval claim. The existing parser-owned `Argument.IsPath`
contract decides whether an effective value is path-relevant. Every finite
effective value for an `IsPath` argument still passes `ToolPathPolicy`; an
unknown path-relevant value stays strict.

ShellSyntaxTree 0.3.3 publishes D14's finite `AuthoredFileSystemValue`. Netclaw
accepts only `Exact` and `FiniteSet`. Each value enters `ToolPathPolicy` and the
approval scope check. Unknown and all other alternatives stay strict. Netclaw
does not infer the role from an executable's private grammar.

ShellSyntaxTree 0.3.4 publishes each occurrence's working-directory effect.
The causal projection consumes this closed fact directly. It never parses a
directory command name, alias, option, or operand.

`AuthoredPathShape` is lexical shape only. It may make review stricter, but it
never establishes that an executable treats an argument as a filesystem
operand and never creates filesystem authority. Repository slugs, container
images, URIs, and slash-bearing data are counterexamples.

`IntegerRange` and `Concatenation` are bounded scalar data only. They cannot
select an executable, create path authority, or justify a redirect. The broad
consumer rule that exempts every Bash environment-variable argument is removed.

### 8. Emit a bounded redacted trace

The trace contains enum stage, enum outcome, enum reason, call-local candidate
ID, executable basename, coverage kind, scope relation, and grant timestamp.
It contains no full command, argument values, environment values, redirect
bodies, raw paths, tokens, secrets, or model content.

The trace has at most one row per stage per candidate and 256 rows total. Each
text field is at most 128 UTF-16 code units. CR, LF, other controls, bidi
controls, and invalid Unicode are escaped. Secret-pattern redaction runs before
logging. Overflow replaces later detail with one `TraceTruncated` row; it never
changes the decision.

The actor returns exact match and bounded near-miss evidence from its one
snapshot. The coordinator projects grant rows from that evidence and returns
one ordered trace. Near-miss diagnostics project from those rows and do not
rescan grants. Trace data is operator-log-only and is not persisted in the
session journal or sent to the model.

### 9. Preserve prompt, actor, and recovery behavior

The original source and approved arguments remain attached to the
session-pipeline pending request. `Once` seeds the exact `OneTimeApprovalKeys` set
on that invocation's `ToolApprovalAttempt` and retries only that blocked
request. A stale, duplicate, expired, or wrong-scope response cannot execute
work. Recovery reconstructs pending approval in the session pipeline and
re-evaluates policy before execution. Parent-session grants continue to cover
child scopes through the actor's existing bounded scope walk.

Prompt display keeps verbatim source separate from normalized policy phrases.
Existing newline, carriage-return, bidi, and multiword-spoof protections remain
in force for display and persistence.

### 10. Use one exact cross-repository catalog

`evidence/approval-matrix.json` is byte-identical to the ShellSyntaxTree
artifact. Every row has exact sanitized input, observed response,
classification, owner, ShellSyntaxTree expectation, and Netclaw expectation.
It is the shared classification catalog, not an implied trace fixture.

`evidence/netclaw-policy-fixtures.json` is Netclaw-owned. It gives exact
candidate IDs, typed phrases, real and intent scopes, available grant/safe
inputs, expected coverage, the ordered bounded trace, and final outcome for the
policy-owned acceptance cases. Tests load these structured fields directly;
they do not branch on Dxx IDs or derive expectations from prose.

The fixture's top-level defaults are executable inputs, not test conventions.
They include tool, audience, approval mode, interactive capability, session,
safe roots, inherited cwd, store status, and a fixed clock. Parser facts use
command indexes. Policy rows use stable candidate
IDs because one parser occurrence can project a different policy cardinality.
Each case supplies its canonical shell environment, initial cwd, and every
stored grant includes its canonical shell tag. The exact executable cases are
D02, D03, D07, D08, D09, D10, D11, D14, D17, and D18.

Complete D03 example:

```text
Input: cd /tmp && gh api ... > slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log
Execution: real scopes remain unchanged
Prerequisites: cd and gh api use persistent global grants
Intent: wc and head use the exact protected-path-safe /tmp target
Trace: two StoredGrantMatch rows, two ReviewedSafePolicy rows, then Completion/Allow
Final: Allow(AllCandidatesCovered)
```

The fixture keeps each causal role and prerequisite ID explicit. It does not
replace execution scope with approval intent.

The policy validates each runtime fallback before reviewed-safe coverage. A
prior target cannot become a later fallback through a symlink. POSIX policy
captures the conventional `/tmp` alias independently of the runtime temp root.
It maps that alias and its safe descendants to the host-resolved canonical
root. Parser-published working-directory effects identify each transition.
Netclaw does not inspect transition command names.

## Risks / Trade-offs

- **New token-prefix grants add authority.** They are token-boundary based,
  shell-tagged, and visible before the user approves them. Migrated grants stay
  exact.
- **Causal intent differs from one runtime failure path.** It is approval-only;
  real facts still control execution and denial.
- **The safe catalog can be wrong.** Whole-argument safety is reviewed in code,
  with adversarial tests and no user override.
- **Trace diagnostics can disclose data.** The schema excludes raw arguments and
  paths, caps fields, escapes controls, and redacts before logging.
- **Refactoring actors can lose pending work.** Recovery, stale response,
  one-time retry, and child inheritance are explicit acceptance tests.

## Migration Plan

1. Record the approved ShellSyntaxTree boundary and exact v2 conversion rule.
2. Freeze exact D01-D18 and current prompt snapshots.
3. Add coordinator, coverage types, and actor batch protocol without behavior
   changes.
4. Move deny and path checks into ordered preflight stages.
5. Migrate approval storage and session grants to typed phrases.
6. Replace safe-list strings with the reviewed immutable catalog.
7. Add Bash causal intent and keep native PowerShell strict.
8. Upgrade ShellSyntaxTree, consume authored facts, and remove broad relaxations
   and command-specific normalization.
9. Update operator skill, guides, behavioral evals, and exact trace snapshots.
10. Validate Linux and native Windows before staged delivery.

For recovery, the operator stops the daemon and keeps the version-3 file. The
operator restores `.v2.bak` and starts the current daemon. The current daemon
can convert version 2 again. No automatic downgrade occurs.

## Open Questions

None. The maintainer approved `AuthoredValue` for approval facts. Effective
facts keep their runtime semantics. All version-2 grants stay exact after
migration. Only a new version-3 approval can create token-prefix authority.

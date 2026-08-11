## Context

The exact sanitized harvest is `evidence/approval-matrix.json`. The approval
store contained 435 Personal shell grants and no new persistent grant during
the window. The observed responses were one-time, session, denied, or pending.

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

For Bash only, `Intent` can carry the exact target of a leading authored
directory transition. The transition begins on the success edge of `cd TARGET
&& ...`. It may remain the user's approval scope for later top-level diagnostic
occurrences until invalidated, even when a semicolon means runtime failure
would continue in the original directory. This is an approval-intent fact, not
a runtime cwd claim.

Intent is invalidated by:

- a later directory mutation whose exact target is unavailable;
- `||`, alternate branches, or a join whose incoming intent scopes differ;
- entry to or exit from a subshell/group boundary unless both sides retain the
  same proved intent;
- dynamic identity, command substitution controlling flow, or unsupported
  control flow.

An exact later success-gated `cd` replaces intent on its success edge. Relative
authored paths under intent are rebased only for safe-policy scope; protected
path evaluation also checks their real execution projection. Folder grants
never use intent.

An intent target is eligible only when it is exact, absolute, normalized,
symlink-free, and allowed by protected-path policy; the `cd` candidate and the
first non-navigation action on its success edge must already have one-time,
session, or stored-grant coverage. This existing user authority is what lets a
later reviewed diagnostic consume intent even when the target is not a normal
session/project safe root. Safe policy alone cannot manufacture causal intent.

Only a catalog entry proved read-only for every accepted argument shape and an
occurrence without a file-writing redirect can consume eligible intent. Native
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

On first successful load of version 2, plain entries whose `verb` is a
whitespace-separated sequence of safe unquoted atoms migrate according to the
maintainer-approved authority choice. Entries containing quotes, escapes, or
ambiguous whitespace migrate as `LegacyExact` and retain exact-string behavior
only. A structurally valid entry containing controls or otherwise not safely
representable is omitted with one bounded migration diagnostic; it never enters
`LegacyExact`. The original file is copied to `.v2.bak` before one atomic
version-3 replacement. Session grants use the same typed phrase model but are
not persisted.

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

This intentionally widens simple existing grants such as `git push` to cover
later static candidate tokens such as `upstream`. It is a material authority
change and requires maintainer approval before implementation.

### 6. Use a reviewed immutable safe-policy catalog

The bundled per-platform resource contains typed phrase entries. A
`ReadOnlyForAllArguments` entry means no accepted argument shape can write or
delete a file, execute another command, or mutate a remote service through the
executable's argv interpretation. Redirects, parser-owned path operands,
provider paths, and unknown shell expansions remain separate strict effects.
Displaying a value explicitly supplied by the shell does not itself make a
phrase executable-private or unsafe.

The catalog is immutable at runtime. User-overridable safe-verb files are
removed because an agent-writable or operator-edited file can silently widen
authority outside code review. At minimum `find`, `awk`, `rg`, and `sort` are
not eligible. Production code has no flag-specific exceptions. The existing
`git ls-tree` special case is deleted.

### 7. Consume ShellSyntaxTree 0.3.1 facts explicitly

Netclaw uses effective `AnalyzedArgument.Value` for runtime-sensitive checks.
It may use `AuthoredValue` for approval matching only after the maintainer
accepts that ambient Bash attributes, ambient `IFS`, and field splitting are
outside the approval claim. The existing parser-owned `Argument.IsPath`
contract decides whether a value is path-relevant. Every effective or authored
finite value for an `IsPath` argument still passes `ToolPathPolicy`; an unknown
path-relevant value stays strict.

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

The fixture's top-level defaults are executable inputs, not test conventions:
tool name, audience, approval mode, interactive capability, session identity
and safe root, project safe root, inherited cwd, and persistent-store status.
Each case supplies its canonical shell environment, initial cwd, and every
stored grant includes its canonical shell tag. The exact executable cases are
D02, D03, D07, D08, D09, D10, D11, D14, D17, and D18.

Complete D03 example:

```text
Input: cd /tmp && gh api ... > slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log
Preflight: candidates C0=cd, C1=gh api, C2=wc, C3=head; intent=/tmp
Actor: C0=PersistentGlobal, C1=PersistentGlobal, C2/C3=Uncovered
Safe policy: C2=ReviewedSafePolicy, C3=ReviewedSafePolicy
Final: Allow(AllCandidatesCovered)
```

## Risks / Trade-offs

- **Token-prefix migration widens authority.** It is versioned, token-boundary
  based, shell-tagged, backed up, and gated on maintainer approval.
- **Causal intent differs from one runtime failure path.** It is approval-only;
  real facts still control execution and denial.
- **The safe catalog can be wrong.** Whole-argument safety is reviewed in code,
  with adversarial tests and no user override.
- **Trace diagnostics can disclose data.** The schema excludes raw arguments and
  paths, caps fields, escapes controls, and redacts before logging.
- **Refactoring actors can lose pending work.** Recovery, stale response,
  one-time retry, and child inheritance are explicit acceptance tests.

## Migration Plan

1. Approve the ShellSyntaxTree API/threat boundary and token-prefix authority
   widening.
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

Rollback requires stopping the daemon, preserving the version-3 file, and
manually restoring the migration-created `.v2.bak` before starting an older
binary. No old binary reads version 3 and no automatic downgrade occurs.

## Open Questions

- Maintainer approval is required to use ShellSyntaxTree `AuthoredValue` for
  approval matching.
- Maintainer approval is required to migrate simple v2 grants to token-prefix
  authority. The conservative alternative is exact matching for all migrated
  entries and prefix matching only for newly approved version-3 entries.

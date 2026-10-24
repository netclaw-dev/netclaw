## Context

See [proposal.md](proposal.md) for the problem and product scope.
The baseline is `aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d`.
Local sessions show repeated consent requests for launch-derived assignments.
Representative cases also include loops with trailing input redirects.
These command shapes do not predict the total benefit.

`ShellExecutionEnvironment` owns the launch snapshot and parser mode.
`ShellCommandAnalyzer` owns complete source and effect accounting.
`ShellAssignmentDigestFactory` owns the canonical assignment identity.
`ApprovalPatternMatching` owns phrase and scope matches and their near-miss reasons.
`ShellPolicyCoordinator` owns final candidate coverage.
These decisions are call-local. The approval store owns durable grants.
The change adds no actor message, persistent record, or configuration field.

## Goals / Non-Goals

**Goals:**

- Correct legacy near-miss explanations while preserving authorization outcomes.
- Establish whether existing exact effective-value facts can safely qualify an assignment grant.
- Preserve the current source, scope, effect, and launch checks.
- Produce repeatable comparisons with explicit expected changes.

**Non-Goals:**

- No executable-specific option or subcommand parser.
- No general program-wide grant, scope expansion, or environment-variable allow list.
- No compound-redirect implementation or package update in this change.
- No deployment, saved-grant migration, or live-daemon change.

## Decisions

### 1. Keep the first repair diagnostic only

The near-miss evaluator currently requires `VerbTokens`, which a valid legacy entry does not store.
Use the entry's existing canonical phrase interpretation for diagnostics.
Do not alter `PhraseMatches`, `MatchesChain`, scope checks, or existing single-token program exceptions.
Keep current result bounds, redaction, comparison rules, and near-miss order.
Test both the explanation and the unchanged grant-match result.

The smaller alternative changes documentation only.
It leaves useful evidence unavailable and does not resolve the diagnostic defect.

### 2. Require an assignment proof before product edits

The experiment compares these forms under the same launch snapshot:

```bash
export DOTNET_ROOT=$HOME/.dotnet; dotnet test ./sample.csproj | head
export DOTNET_ROOT=/home/test/.dotnet; dotnet test ./sample.csproj | head
```

The parser proves an exact effective value for the first form but reports an unknown authored value.
The digest factory rejects that pair today.
The literal form can receive a digest. A plain grant still does not cover it.

Do not infer source safety from one exact value alone.
Check the public parser contract, source spans, complete syntax, nested effects, and exact launch facts together.
Reuse existing AST and launch data. Do not add a parallel environment snapshot or a source-expression parser.
If existing facts cannot prove the source and effects, retain exact consent and document the missing generic parser fact.

Schematic flow; normal admission, path screens, and launch checks remain required:

```text
existing launch snapshot + source
  -> existing parser and complete effect analysis
  -> existing assignment proof, or independently reviewed stronger proof
  -> same digest format: shell, name, scope, export flag, effective value
  -> same candidates and all existing authority checks
  -> matching qualified grant, reusable consent, or exact consent
```

Positive example: one complete assignment derives an exact value from a supported host's proved launch `HOME`.
After proof, an existing grant with that exact assignment identity may cover its normal candidate.
Negative example: a substitution or unknown mutation prevents an exact value or complete effects.
That source retains exact consent and all nested hard-deny checks.
An unqualified grant never covers the qualified assignment.

The parent reviews the written proof and adversarial test results before an agent changes product authorization.
This is an engineering gate under the user's current authorization. It does not require another user confirmation.

### 3. Use existing corpus and test owners

The repository runner generates 55,004 inputs. The historical JSON archive contains 55,144 different-version inputs.
Keep their identities separate and record the corpus hash for every comparison.
Retain exact incident inputs privately. Commit only manually checked, sanitized representatives.
The existing filtered wrapper accepts a JSON list; add no new input feature.
Map every control to the real fixture roots, grant set, audience, host mode, and initial directory.
Command-only replay with synthetic grants is not a replay of the live incident context.

A diagnostic change can change trace text but must preserve allow, deny, correction, consent, coverage, and execution facts.
An assignment experiment needs an explicit transition list and independent review of every changed decision.
No corpus command may execute during analysis.

### 4. Keep loop redirect work separate

Record the current failure, required generic parser facts, and consumer file-authority checks.
A future parser change must account for the compound input redirect and each body command.
Unknown input values do not become safe because the syntax parses.
This change does not include SST production edits or broaden the current consent rules.
The user later authorized a separate SST pull request and Netclaw consumer change.
The canonical plan records that expanded delivery scope and its local package validation gate.

## Risks / Trade-offs

- Exact effective values can conceal unaccounted effects. Require complete source and effect proof plus negative controls.
- A newly reusable candidate still needs a qualified grant. Do not promise an automatic prompt reduction.
- A legacy near-miss repair can change which reason wins. Preserve ordering and bound the explanation tests.
- Synthetic corpus states can omit live context. Record that limit and use actor-level tests for the real boundary.
- Runtime skill edits trigger behavioral evals. Inspect the runner and preserve that gate.
- Concurrent builds can exhaust local resources. Use one shared heavy-build slot and bounded replay jobs.

## Migration Plan

Implement the diagnostic repair first in an independent branch.
The assignment branch starts with tests and a proof memo on the same baseline.
Integrate an assignment implementation only after the proof gate passes.
Update the current architecture document in that implementation diff if a context or candidate changes.
Put proposed architecture only in its future section before implementation.
The existing grant wire format and digest version remain unchanged unless evidence proves a separate migration is necessary.
Such a migration is outside this plan's accepted scope.
Revert source to reverse an unshipped code change. Do not alter or revoke user grants automatically.
Merge and deployment remain separate from local validation.

## Delivery

The [canonical plan](../../../../.systematize/plans/shell-approval-proof/plan.html) owns the delegation matrix, PR sequence, and live evidence status.
The [tasks](tasks.md) own executable completion state.
Diagnostic and assignment authors have separate worktrees.
The corpus lead reviews the diagnostic author independently.
The parent reviews the assignment proof and the combined implementation.
No author's green result alone satisfies an independent review gate.

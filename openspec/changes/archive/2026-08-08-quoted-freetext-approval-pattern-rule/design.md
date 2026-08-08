## Context

The shell approval matcher extracts a verb-chain "pattern" from each command.
It stores the pattern as the reusable approval entry and shows it at the
prompt. Two rules already mark a token as call-specific and stop extraction:
the digit-bearing rule (#1331) and the multi-line rule (#1402). A single-line
multi-word quoted operand fell through both rules. So every distinct commit
message or ticket body produced a new pattern and re-prompted. Issue #1406
tracked this. PR #1815 shipped the fix; commit `4836e881` merged it to `dev`.
This change records the spec for that shipped behavior.

## Goals / Non-Goals

**Goals:**

- State the quoted-free-text termination rule in the `tool-approval-gates`
  spec next to the digit-bearing and multi-line rules.
- Pin the constraints the shipped code enforces with scenarios.

**Non-Goals:**

- No code change. The implementation already merged.
- No change to the live authorization decision or the persisted
  `(verb, directory)` grant.

## Decisions

**Decision: use the internal-whitespace predicate (predicate 1), not the
any-quote predicate (predicate 2).**

The matcher drops a quoted argument only when its decoded text holds internal
whitespace. It keeps a single-word quoted argument.

- Rationale: an unquoted argument cannot hold whitespace, so internal
  whitespace proves the text was quoted free text. This rule leaves a
  single-word token untouched, so `git commit -m "fix"` and `git commit -m fix`
  normalize to the same pattern.
- Alternative considered — predicate 2 (drop any quote-leading argument): it
  also drops single-word quoted values, so `git commit -m "fix"` and
  `git commit -m fix` would normalize differently. That divergence surprises
  operators and consumers. Predicate 1 avoids it.

**Decision: exempt path-shaped arguments.**

The drop runs only where the digit-bearing value rule runs. A path argument
(`IsPath = true`) keeps its scope, so a quoted path with whitespace
(`cat "my file.txt"`) still reaches directory scoping. The exemption mirrors
the gate's own `IsPath` check, so the pattern axis and the scope axis stay
locked together.

**Decision: pattern-string only.**

The rule lives in pattern reconstruction. The live gate resolves candidates
and directories from the parsed argument, independent of the pattern string.
The operator always sees the verbatim command through the display path. So the
rule cannot hide a command or move an authorization decision.

## Risks / Trade-offs

- [A dropped trailing path leaves the secondary `Pattern:` line broader than
  the persisted directory-scoped grant] → Overstating scope is the safe
  direction; the operator sees more scope than they grant, and the verbatim
  command still shows the real path. This matches the pre-existing #1331 and
  #1402 behavior.
- [A wrapper command string (`bash -c "..."`) could look like free text] → The
  analyzer expands the wrapper into inner clauses before pattern
  reconstruction, so the rule never sees the wrapper string. Confirmed by the
  PR #1815 adversarial review.

## Migration Plan

None. The behavior already ships in `dev`. This change only syncs the spec and
archives.

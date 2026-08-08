## Why

Shipped code (issue #1406, PR #1815, commit `4836e881`) added a third
call-specific classification rule to shell approval pattern extraction. The
`tool-approval-gates` spec still lists only two rules. This change reconciles
the spec with the merged behavior. The rule stops a multi-word quoted operand,
such as a commit message, from inflating the stored approval pattern. Without
it, every `git commit -m "new message"` mints a new pattern and re-prompts.

## What Changes

- Add a third pattern-termination rule to the "Shell command pattern matching"
  requirement: a quote-wrapped argument whose decoded text holds internal
  whitespace is call-specific free text. It terminates pattern extraction and
  excludes that argument and everything after it.
- State the constraints the shipped code enforces:
  - A single-word quoted argument is unaffected, so a quoted and an unquoted
    single token normalize to the same pattern.
  - A path-shaped argument (`IsPath = true`) is exempt, so a quoted path with
    whitespace still reaches directory scoping.
  - A preceding flag (for example `--message`) stays in the pattern because it
    carries invocation intent.
  - The rule applies the same way on the gate (candidate) path and on the
    persisted or display pattern path.
- Record that this is pattern-string normalization only. It does not change the
  live authorization decision, the persisted `(verb, directory)` grant, or the
  verbatim command the operator sees at the prompt.

This is not a breaking change. It documents behavior that already ships.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `tool-approval-gates`: the "Shell command pattern matching" requirement gains
  the quoted-free-text termination rule next to the existing digit-bearing rule
  and the multi-line (#1402) rule.

## Impact

- Spec only. The implementation already merged into `dev`.
- Affected spec: `openspec/specs/tool-approval-gates/spec.md`.
- Affected code (already merged, for traceability): the `IsQuotedFreeTextArg`
  helper and the termination check in `ReconstructClauseText`
  (`src/Netclaw.Security/IToolApprovalMatcher.cs`).
- Security impact: none beyond the merged change. The rule is pattern-string
  normalization; the adversarial review of PR #1815 confirmed it does not move
  the authorization decision, drop a path scope, or hide a wrapped command.
- Operational impact: fewer repeat approval prompts for verbs that take a
  multi-word quoted operand.

# Design: Shell Approval Value-Token Normalization

## Context

ShellSyntaxTree's greedy verb walk (parser SPEC §6.1) extends through every
verb-like token — lowercase-leading, `[a-z0-9._-]` body — and its own SPEC
§6.1.1 declares `Clause.Verb` "a convenience hint, not a security contract."
A lowercase-leading value like `v0.4.2` is verb-like by shape and folds into
the chain (`git tag v0.4.2`); a digit-leading `0.4.2` stops the walk and lands
in Args. Netclaw's `ShellApprovalMatcher` compared the full chain for exact
equality against persisted `ApprovalEntry` records, so the two forms of the
same intent gated differently: `git tag 0.4.2` auto-approved under a standing
`git tag` grant; `git tag v0.4.2` re-prompted.

The fix is implemented on the PR branch (PR #1388); this document records the
decisions for the spec delta.

## Goals / Non-Goals

**Goals**

- One stable verb chain per intent, regardless of how the parser splits value
  tokens between Verb and Args.
- Gate (candidate) path and persisted/display pattern path normalize
  identically.
- Zero per-CLI knowledge; bounded, auditable classification.

**Non-Goals**

- Positional/prefix grant matching (see Decision 1 alternatives).
- Windows legacy tokenizer parity (follow-up).
- Approval store migration.

## Decisions

### Decision 1: Morphological classification, one rule

A token is a call-specific value iff it is **not a flag, not path-shaped, and
contains a digit** (`IsCallSpecificValueToken`). This replaces the prior
composition of two shape predicates (bare-integer #1331 + a dotted-version
shape) and deletes both.

**Alternatives considered:**

- **Shape taxonomy (bare integers + version shapes + …)** — rejected: accretes
  a special case per value family (SHAs, IPs, dates, calver…) while still
  mis-drawing boundaries (`git checkout v2` kept but `v2.0` stripped;
  alpha-leading SHAs never normalized, so a `git show` grant could not cover
  the dominant SHA use-case).
- **Positional/prefix matching (grant tokens are a prefix of the command
  tokens)** — rejected: deciding where verbs end and arguments begin
  positionally requires per-CLI verb-depth knowledge — effectively a global
  classification system of shell commands — and changing read semantics
  retroactively rescopes every grant already persisted in deployed
  `tool-approvals.json` files with no schema change to gate a migration on.
  Morphology identifies likely arguments inside an arbitrary, compounded verb
  chain with no CLI knowledge at all.

### Decision 2: Trailing-only trim with a one-token floor

`TrimTrailingValueTokens` strips value tokens only from the end of the parsed
chain and always retains the command word. Mid-chain digit-bearing tokens
(`aws s3 ls`) and heads (`python3`) are never touched. This bounds the
worst-case mis-classification to the chain tail, where the token is most
likely an operand.

### Decision 3: Flag and path exemptions

Flags (`-3`, `--max-count=10`) carry invocation intent, not values; path-shaped
tokens must survive into `ExtractFirstPathArgument`/directory scoping and the
display pattern. Both are excluded from value classification. All-alpha
operands are deliberately unclassified: no shape rule distinguishes `dev`
(branch) from `worktree` (subcommand), and mis-stripping a subcommand silently
widens a grant — the unrecoverable failure direction.

### Decision 4: Apply at both extraction sites, POSIX path only

The trim runs in `ExtractCandidatesViaBashParser` (gate candidates) and
`ReconstructClauseText` (persisted/display patterns) so store and gate stay in
lockstep. The Windows legacy tokenizer path is untouched; tests are
POSIX-gated. Cross-OS grant portability for folded value tokens is a known
limitation tracked as a follow-up.

## Actor Boundaries and Persistence

None changed. The matcher is pure in-process logic inside `Netclaw.Security`;
`ApprovalEntry` schema, `tool-approvals.json` format, approval actors, and
session persistence are untouched. Matching remains exact verb equality —
normalization only changes the strings being compared, and the approval prompt
displays the normalized pattern, so displayed scope equals stored scope.

## Failure Modes and Recovery

- **Parser failure / messy command** → unchanged: fail-empty, gate offers
  Once/Deny only.
- **Under-classification** (all-alpha value, e.g. SHA `abcdef`) → re-prompt;
  recoverable.
- **Over-classification** (digit-bearing true subcommand at chain tail, e.g.
  bare `aws s3` with only flags after) → candidate collapses to the parent
  verb; the prompt displays the collapsed pattern, so any resulting grant is
  exactly what the operator saw and confirmed.
- **Stale digit-bearing entries** (`git show aa211dcb` persisted pre-change) →
  go dead and re-prompt once; recoverable, no migration needed.

## Risks / Trade-offs

- [Digit-bearing operands truncate display patterns mid-arg-list
  (`docker run --name test123 --port=8080` → `docker run --name`)] → the full
  command text is still shown in the prompt header (`FormatForDisplay`); the
  pattern is the grant key, not the audit record.
- [Windows path divergence] → disclosed; follow-up to port the trim to the
  legacy tokenizer.

## Open Questions

(none)

# Proposal: Shell Approval Value-Token Normalization

## Why

The shell approval gate treated semantically-identical commands differently based
on the leading character of a value argument: `git tag 0.4.2` auto-approved under
a standing `git tag` grant while `git tag v0.4.2` re-prompted, because
ShellSyntaxTree's greedy verb walk folds lowercase-leading value tokens
(`v0.4.2`, `aa211dcb`, `feature2`) into the verb chain that the matcher compares
for exact equality. The fix is implemented (PR #1388); this change records the
matching spec delta so `tool-approval-gates` describes actual behavior.

## What Changes

- Generalize the bare-integer exclusion (issue #1331) in the "Shell command
  pattern matching" requirement to a single morphological rule: a token is a
  call-specific value iff it is **not a flag, not path-shaped, and contains a
  digit**. Versions, SHAs, IPs, ports, ticket IDs, and digit-bearing refs all
  normalize uniformly; no taxonomy of value shapes.
- Trailing value tokens that greedy extraction folded into the verb chain are
  trimmed (trailing-only, retaining at least the command word) on **both** the
  gate (candidate) path and the persisted/display pattern path, so the two
  normalize identically.
- Behavior change to an existing scenario: digit-bearing operands now terminate
  the pattern (e.g. `docker run --name test123 --port=8080` persists
  `docker run --name`, not the full token list). The prior "Non-bare numeric
  tokens are preserved" scenario is replaced.
- All-alpha operands (branch names, remote names, package names) remain
  unclassified by design — no shape rule distinguishes them from subcommands.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `tool-approval-gates` — "Shell command pattern matching" requirement: value
  classification generalized from bare integers to digit-bearing tokens;
  trailing verb-chain trim added; one scenario replaced, scenarios added.

## Scope

**In scope (MVP):**

- Spec delta documenting the implemented POSIX (BashParser) extraction behavior.

**Out of scope:**

- Positional/prefix-based grant matching (rejected: requires per-CLI verb-depth
  knowledge — a global classification system of shell commands — and would
  retroactively rescope grants already persisted in deployed
  `tool-approvals.json` files).
- Windows legacy tokenizer path (`ShellTokenizer.ExtractVerbChain` /
  `TraverseApprovalUnits`) — retains prior behavior; follow-up candidate.
- Approval store migration or re-keying of existing entries.

## Security and Operational Impact

- **Grant generalization is bounded.** Trimming is trailing-only with a
  one-token floor, so mid-chain tokens (`aws s3 ls`) and command heads
  (`python3`) are never stripped. Flags and path-shaped tokens are exempt, so
  digit-bearing paths still reach directory scoping.
- **No silent scope change for stored grants.** Matching remains exact verb
  equality against `ApprovalEntry` records; the change only normalizes what the
  candidate/pattern strings contain. Approval prompts display the normalized
  pattern, so what the operator sees remains what is stored.
- **Recoverable failure direction.** A value token the rule fails to classify
  (all-alpha SHA, e.g. `abcdef`) re-prompts (false negative, recoverable) rather
  than silently auto-granting.
- **Operationally inert.** No config, schema, persistence, or actor topology
  changes; no migration required. Existing stored entries keep matching
  (digit-bearing legacy entries such as `git show aa211dcb` go dead and simply
  re-prompt once).

## Impact

- Code (already merged to PR branch): `src/Netclaw.Security/IToolApprovalMatcher.cs`
  (`ShellApprovalMatcher.ExtractCandidatesViaBashParser`,
  `ReconstructClauseText`, `IsCallSpecificValueToken`,
  `TrimTrailingValueTokens`; `IsVersionShapedToken` and `IsBareIntegerToken`
  deleted).
- Tests: `src/Netclaw.Security.Tests/ShellApprovalMatcherTests.cs` (parity,
  persist-path, boundary table).
- Specs: `openspec/specs/tool-approval-gates/spec.md` (this delta).
- PRD traceability: PRD-002 Gateway Security Envelope (SEC-006 Pairing and
  Approval Surfaces — approval prompt/grant surface behavior).

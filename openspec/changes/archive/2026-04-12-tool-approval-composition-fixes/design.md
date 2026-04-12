## Context

`tool-approval-gates` added approval interception across `ToolAccessPolicy`,
`DispatchingToolExecutor`, and session approval retry handling. Follow-on
testing exposed composition edge cases:

- File mutation gating now uses argument-aware matcher keys for control-plane
  paths, but key resolution needs explicit precedence when both path-specific
  and base tool overrides exist.
- Approve-once currently depends on one-time context state and matcher patterns;
  retry matching must align with the filtered unapproved set returned by
  `IToolApprovalService`, not the pre-filter candidate set.
- Shell deny checks are split between operation-level command hard deny and
  resource-level path denial; precedence and user-visible deny semantics need a
  single contract.

The implementation spans `Netclaw.Actors.Tools`, `Netclaw.Security`, and
session retry flow state. Actor boundary remains unchanged: session actor owns
approval decisions and temporary one-time retry grants.

## Goals / Non-Goals

**Goals:**

- Make approval mode resolution deterministic when matcher-derived keys and base
  tool keys both exist.
- Ensure approve-once retry acceptance checks use the same filtered unapproved
  pattern set shown in the interaction prompt.
- Define shell hard-deny composition semantics between operation hard-deny and
  resource hard-deny, including precedence for deny reasons.
- Add regression scenarios to the capability spec so future refactors preserve
  these compositions.

**Non-Goals:**

- Redesign of approval UI/options or interaction protocol.
- New persistence model for approvals.
- Changes to trust audiences, grant categories, or Slack channel UX.

## Decisions

### Decision 1: Approval mode key precedence uses most-specific to least-specific

**Choice:** Resolve approval mode in this order:

1. Matcher-derived key override (for example `file_write:control-plane`)
2. Base tool key override (`file_write`)
3. Matcher fail-closed behavior for Personal audience
4. Audience `DefaultMode`

**Alternatives considered:**

- Matcher key only with no base fallback: rejected because adding path-specific
  matchers unintentionally bypasses existing tool-level policy intent.
- Base key first: rejected because it prevents finer-grained overrides from
  taking effect.

**Rationale:** This preserves backward compatibility for existing overrides while
letting operators tighten high-risk subsets without broadening unrelated calls.

### Decision 2: Approve-once matching is evaluated after unapproved filtering

**Choice:** For approval-gated calls, first compute unapproved patterns via
`IToolApprovalService`; then evaluate one-time retry bypass against that filtered
set.

**Alternatives considered:**

- Check one-time bypass against pre-filter matcher patterns: rejected because it
  can reprompt even when the user just approved the exact prompt set.
- Persist approve-once to shared approval service: rejected because it breaks
  one-shot scope guarantees.

**Rationale:** Prompt set and retry set must be identical to avoid UX/security
drift. One-time state remains in-memory and call-retry scoped.

### Decision 3: Shell deny composition is fail-closed with operation precedence

**Choice:** Shell invocation remains denied if either operation hard-deny
(`ShellCommandPolicy`) or resource hard-deny (`ToolPathPolicy`) matches.
Operation hard-deny is evaluated first; if it matches, resource checks are not
consulted for the result reason.

**Alternatives considered:**

- Resource deny first: rejected because known self-destructive operations should
  short-circuit early and return stable hard-deny categorization.
- Merge both policies into one matcher: rejected for now to keep policy modules
  independently testable.

**Rationale:** This keeps a strict deny floor while preserving diagnosable,
deterministic denial reasons.

## Risks / Trade-offs

- **[Risk] Key fallback could broaden approval-gated scope unexpectedly** ->
  Mitigation: explicit spec scenarios for matcher-key override and base-key
  fallback; add policy tests for both branches.
- **[Risk] Retry-path reordering may miss existing one-time checks** ->
  Mitigation: add executor and pipeline tests that assert no reprompt on the
  immediate retry but prompt on later calls.
- **[Risk] Deny precedence can hide secondary violations** -> Mitigation:
  preserve first-deny reason in user result and audit log while keeping
  independent tests for both operation and resource deny paths.

## Migration Plan

1. Update approval-mode resolution logic and unit tests.
2. Update executor retry matching order to use filtered unapproved patterns.
3. Update shell policy composition checks and denial reason assertions.
4. Update OpenSpec delta scenarios and run targeted test suites.

Rollback is straightforward: revert this change set to restore prior composition
behavior.

## Open Questions

- None for this scope; behavior is constrained to composition clarifications and
  regression coverage.

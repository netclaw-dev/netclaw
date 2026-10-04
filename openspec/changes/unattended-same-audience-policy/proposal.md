## Why

An unattended run (headless chat, a reminder, a webhook, a sub-agent with no
approval bridge) had its own rules: a trust zone, an unresolved-input denial,
and several attended-only exceptions. A test in a chat therefore did not
predict the scheduled run. Owner decision D2 (October 4, 2026) makes the
audience policy the only policy (PRD-002, PRD-008).

## What Changes

- An unattended call uses the same rules as a chat of the same audience:
  file reach, hard deny, protected paths, reviewed-safe catalog, stored grants,
  decision D1, directory proofs, approval modes, and agent corrections.
- The one difference: a call that would prompt in a chat is denied with
  `approval_required_unattended`, because nobody can answer. A stored grant
  that covers the call still allows it.
- Removed: the unattended-only trust zone, the
  `shell_unresolved_trust_zone_input` denial, and the PR 6e rule that let a
  stored grant replace a trusted-root denial
  (`StoredApprovalOutsideTrustedRoots`).
- **BREAKING** for operators: an unattended Personal run now has the file
  reach of a Personal chat. A bounded (`Roots`) profile confines attended and
  unattended runs alike.

In scope: authorization and path policy, tests, docs. Out of scope: new
prompt options, a rehearsal mode, and grant store changes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6, TA-7, TA-8, TA-9, and TA-10 drop the
  attended-only and unattended-only rules and add the
  `approval_required_unattended` denial.

## Impact

- Code: `ToolAuthorizer`, `ToolAccessPolicy`, `PathAccessPolicy`,
  `ShellPolicyCoordinator`, `ShellPolicyProjection`,
  `TemporaryPathCorrectionPolicy`, `ToolAuthorizationDecision`.
- Security: unattended runs gain the reach of their audience. Hard deny,
  protected paths, and credential-store denials do not change. The approval
  outcome snapshot lists each transition, and no attended row changes.
- Operations: a Personal webhook route or reminder can read and write what a
  Personal chat can. Operators who want less reach set a `Roots` profile.

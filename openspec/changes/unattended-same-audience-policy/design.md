## Context

See proposal.md for the motivation. Before D2, the run kind changed the rules
in eight places: the file reach of a `Mode.All` profile, the unresolved-input
screen, reviewed-safe coverage, the D1 command split, the causal-list
directory proof, the managed temporary advice, and the PR 6e grant-first
replacement of a trusted-root denial. The session denied an unattended consent
request after authorization, with a fixed text.

## Goals / Non-Goals

**Goals:**
- One audience policy for attended and unattended runs.
- One visible rule for the remaining difference.

**Non-Goals:**
- A rehearsal mode, new prompt options, or grant store changes.
- Removal of the project-scope declaration advice. D2 makes it unreachable
  for normal profiles; a separate change can remove it.

## Decisions

- **The authorizer owns "prompt → deny".** `ToolAuthorizer.AuthorizeAsync`
  turns `NeedsConsent` into `Denied(approval_required_unattended)` when the
  run scope has `InteractiveApprovalCapability.Unavailable`. Alternative: keep
  the session-level text. Rejected, because the outcome snapshot then showed
  `RequiresApproval` for calls that can never run. The session check stays as
  a backstop.
- **Remove, do not branch.** Each attended-only or unattended-only condition is
  deleted, not inverted. `PathAccessPolicy` keeps confinement only for
  `DeclareProjectScope`. The PR 6e plumbing (`outsideOnly`,
  `OutsideTrustedRoots`, `RequireStoredGrantsAsync`,
  `StoredApprovalOutsideTrustedRoots`) goes away, because no path denial can
  now be "only outside" for one run kind.
- **Bounded profiles keep confinement.** Tests that used "unattended" to reach
  root checks now use a `Roots` profile, which confines both run kinds.

Actor boundaries: no new messages. All state is call-local. The grant store is
unchanged.

## Risks / Trade-offs

- [An unattended Personal run, including a Personal webhook route, gains the
  file reach of a Personal chat] → This is the owner decision. Hard deny,
  protected paths, and credential-store denials are unchanged. A `Roots`
  profile limits both run kinds.
- [Auto mode now allows unresolved input unattended, as in a chat] → Auto is
  an explicit operator choice.
- [Prose input now fails closed with `internal_policy_failure` unattended, as
  it already did in a chat] → Fails closed. The projection defect needs its
  own fix.
- [Failure: an authorizer rule throws] → The call is denied with
  `internal_policy_failure`, as before.
